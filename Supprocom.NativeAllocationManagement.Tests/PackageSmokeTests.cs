using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;
using Xunit.Abstractions;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class PackageSmokeTests
{
    private static readonly SemaphoreSlim PackageGate = new(1, 1);
    private static PackageEvidence? _package;
    private readonly ITestOutputHelper _output;

    public PackageSmokeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task PackageFastPoolRunsWithOneBoundedTokenCheck()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(
                consumerRoot,
                package,
                excludeAnalyzer: false,
                suppressDiagnostics: false,
                executable: true);
            await File.WriteAllTextAsync(
                Path.Combine(consumerRoot, "Program.cs"),
                """
                using Supprocom.NativeAllocationManagement;

                public static class Consumer
                {
                    public static int Main()
                    {
                        using NativePool<int> pool = new(
                            preLease: 4,
                            returnMemoryOnDispose:
                                NativeMemoryReturn.ToNativeMemory);
                        Pooled<int> lease = pool.Rent(
                            4,
                            static writer => writer.Fill(3));
                        try
                        {
                            return lease.Read(static values =>
                                values[0]
                                + values[1]
                                + values[2]
                                + values[3]) == 12
                                    ? 0
                                    : 7;
                        }
                        finally
                        {
                            lease.Dispose();
                        }
                    }
                }
                """);

            string project = Path.Combine(
                consumerRoot,
                "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"",
                consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);

            CommandResult build = await RunDotnetAsync(
                $"build \"{project}\" --no-restore --nologo",
                consumerRoot);
            Assert.True(build.ExitCode == 0, build.Output);

            CommandResult run = await RunDotnetAsync(
                $"run \"{project}\" --no-build --no-restore --nologo",
                consumerRoot);
            Assert.True(run.ExitCode == 0, run.Output);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task PackageReferenceDeliversRuntimeAndAnalyzerWithoutProjectReferences()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: false, suppressDiagnostics: false);
            await File.WriteAllTextAsync(
                Path.Combine(consumerRoot, "Program.cs"),
                """
                using Supprocom.NativeAllocationManagement;

                public static class Consumer
                {
                    public static void Run()
                    {
                        using NativeConcurrentPool<int> pool = new(doNotLeaseOnDeclaration: true);
                        pool.LeaseFromMemory();
                        {
                            ConcurrentPooled<int> values = pool.Rent(1, static writer => writer.Fill(default!));
                            values.Access(static view => view[0] = 7);
                            values.Dispose();
                        }
                        _ = pool.TrimRetainedMemory();
                        _ = pool.TrimRetainedMemoryByBytes(1);
                        _ = pool.TrimRetainedMemoryByLeaseSize(1);

                        using (NativeRegion region = new())
                        {
                            Local<int> local = region.Lease<int>(1, static writer => writer.Fill(default!));
                            local.Access(static values => values[0] = 7);
                        }

                        using NativeConcurrentArena arena = new(doNotLeaseOnDeclaration: true);
                        arena.LeaseFromMemory();
                        {
                            using ConcurrentPooled<int> faces = pool.Rent(1, static writer => writer.Fill(default!));
                            using ConcurrentPooled<int> vertices = pool.Rent(1, static writer => writer.Fill(default!));
                            using ConcurrentPooled<int> indices = pool.Rent(1, static writer => writer.Fill(default!));
                            ConcurrentArenaLease<int> slices = arena.Scratch<int>(1, static writer => writer.Fill(default!));
                            ConcurrentArenaLease<byte> upload = arena.Scratch<byte>(1, static writer => writer.Fill(default!));
                            NativeLeaseOperations.Access(
                                faces,
                                vertices,
                                indices,
                                slices,
                                upload,
                                static (faceView, vertexView, indexView, sliceView, uploadView) =>
                                {
                                    faceView[0] = 1;
                                    vertexView[0] = faceView[0];
                                    indexView[0] = vertexView[0];
                                    sliceView[0] = indexView[0];
                                    uploadView[0] = 1;
                                });
                        }
                        {
                            ConcurrentArenaLease<string> labels = arena.Scratch<string>(1, static writer => writer.Fill(default!));
                            labels.Access(static view => view[0] = "package");
                        }

                        arena.ReleaseLeasesToNativeMemory();
                        _ = arena.TrimRetainedMemory();
                        _ = arena.TrimRetainedMemoryByBytes(1);
                        _ = arena.TrimRetainedMemoryByLeaseSize<int>(1);

                        {
                            scoped ConcurrentArenaLease<int> scopedValues = arena.ScratchScoped<int>(1, static writer => writer.Fill(default!));
                            scopedValues.Access(static view => view[0] = 9);
                        }

                        arena.RecycleScoped();
                    }
                }
                """);

            CommandResult restore = await RunDotnetAsync(
                $"restore \"{Path.Combine(consumerRoot, "Consumer.csproj")}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"",
                consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);

            CommandResult build = await RunDotnetAsync(
                $"build \"{Path.Combine(consumerRoot, "Consumer.csproj")}\" --no-restore --nologo",
                consumerRoot);
            Assert.True(build.ExitCode == 0, build.Output);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task PackageBudgetedOwnersRunAndBundledAnalyzerRejectsPostCompletionPreflight()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package,
                excludeAnalyzer: false, suppressDiagnostics: false,
                executable: true, treatWarningsAsErrors: true);
            string program = Path.Combine(consumerRoot, "Program.cs");
            await File.WriteAllTextAsync(program,
                """
                using Supprocom.NativeAllocationManagement;

                public static class Consumer
                {
                    public static int Main()
                    {
                        NativeMemoryBudget budget = new(64, traceCapacity: 8);
                        using (NativePool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory))
                        {
                            NativeOwnerDiagnosticSnapshot poolSnapshot = pool.CaptureDiagnosticSnapshot();
                            if (poolSnapshot.OwnerId != pool.Id
                                || poolSnapshot.Model != NativeOwnerModel.ThreadConfinedPool
                                || poolSnapshot.RetainedSegmentCount != 0)
                            {
                                return 5;
                            }
                        }
                        using NativeBuilder<int> builder = new(budget, preLease: 4);
                        using (NativeWorkspace<int> workspace = new(budget, preLease: 8))
                        {
                            builder.Append(42);
                            if (builder.TryEnsureCapacity(5)
                                || budget.CaptureStatistics().CommittedBytes != 48
                                || builder.Count != 1)
                            {
                                return 1;
                            }
                        }

                        if (!builder.TryEnsureCapacity(5))
                        {
                            return 2;
                        }

                        NativeTransfer<int> transfer = builder.Complete();
                        try
                        {
                            if (transfer.Id != builder.Id
                                || transfer.Read(static view => view[0]) != 42
                                || budget.CaptureStatistics().CommittedBytes != 32)
                            {
                                return 3;
                            }
                        }
                        finally
                        {
                            transfer.Dispose();
                        }

                        NativeMemoryBudgetStatistics snapshot = budget.CaptureStatistics();
                        System.Span<NativeMemoryTraceEvent> events = stackalloc NativeMemoryTraceEvent[4];
                        if (snapshot.CommittedBytes != 0
                            || snapshot.TraceCapacity != 8
                            || snapshot.TraceCount != 8
                            || snapshot.DroppedTraceEventCount != 1
                            || snapshot.TraceOverflowed
                            || budget.CopyTraceTo(events) != 4
                            || events[3].Kind != NativeMemoryTraceKind.Released
                            || events[3].OwnerId != builder.Id)
                        {
                            return 4;
                        }
                        return 0;
                    }
                }
                """);
            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"",
                consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);
            CommandResult build = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo", consumerRoot);
            Assert.True(build.ExitCode == 0, build.Output);
            CommandResult run = await RunDotnetAsync($"run \"{project}\" --no-build --no-restore --nologo", consumerRoot);
            Assert.True(run.ExitCode == 0, run.Output);
            await File.WriteAllTextAsync(program,
                """
                using Supprocom.NativeAllocationManagement;

                public static class Consumer
                {
                    public static int Main()
                    {
                        NativeMemoryBudget budget = new(64);
                        using NativeBuilder<int> builder = new(budget, preLease: 4);
                        builder.Append(42);
                        NativeTransfer<int> transfer = builder.Complete();
                        try
                        {
                            _ = builder.TryEnsureCapacity(8);
                            return 0;
                        }
                        finally
                        {
                            transfer.Dispose();
                        }
                    }
                }
                """);
            CommandResult invalid = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo -t:Rebuild", consumerRoot);
            _output.WriteLine(invalid.Output);
            Assert.NotEqual(0, invalid.ExitCode);
            Assert.Contains("error NAM1029", invalid.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task PackageBuilderRunsAndBundledAnalyzerRejectsDoubleCompletion()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(
                consumerRoot,
                package,
                excludeAnalyzer: false,
                suppressDiagnostics: false,
                executable: true);
            string program = Path.Combine(
                consumerRoot,
                "Program.cs");
            await File.WriteAllTextAsync(
                program,
                """
                using Supprocom.NativeAllocationManagement;

                public static class Consumer
                {
                    public static int Main()
                    {
                        using NativeBuilder<uint> builder =
                            new NativeBuilder<uint>(preLease: 2);
                        builder.Append(11);
                        builder.Append(22);
                        builder.Append(33);
                        builder.Append(44);
                        NativeTransfer<uint> transfer = builder.Complete();
                        try
                        {
                            return transfer.Read(static values =>
                                values.Length == 4
                                    && values[0] == 11
                                    && values[1] == 22
                                    && values[2] == 33
                                    && values[3] == 44
                                        ? 0
                                        : 7);
                        }
                        finally
                        {
                            transfer.Dispose();
                        }
                    }
                }
                """);

            string project = Path.Combine(
                consumerRoot,
                "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"",
                consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);

            CommandResult validBuild = await RunDotnetAsync(
                $"build \"{project}\" --no-restore --nologo",
                consumerRoot);
            Assert.True(validBuild.ExitCode == 0, validBuild.Output);

            CommandResult run = await RunDotnetAsync(
                $"run \"{project}\" --no-build --no-restore --nologo",
                consumerRoot);
            Assert.True(run.ExitCode == 0, run.Output);

            await File.WriteAllTextAsync(
                program,
                """
                using Supprocom.NativeAllocationManagement;

                public sealed class Holder
                {
                    private NativeTransfer<uint>? _transfer;

                    public void Build()
                    {
                        using NativeBuilder<uint> builder =
                            new NativeBuilder<uint>(preLease: 2);
                        builder.Append(7);
                        _transfer = builder.Complete();
                    }
                }

                public static class Consumer
                {
                    public static int Main()
                    {
                        using NativeBuilder<uint> builder =
                            new NativeBuilder<uint>(preLease: 2);
                        builder.Append(11);
                        NativeTransfer<uint> first = builder.Complete();
                        try
                        {
                            NativeTransfer<uint> second = builder.Complete();
                            second.Dispose();
                            return 0;
                        }
                        finally
                        {
                            first.Dispose();
                        }
                    }
                }
                """);

            CommandResult invalidBuild = await RunDotnetAsync(
                $"build \"{project}\" --no-restore --nologo -t:Rebuild",
                consumerRoot);
            Assert.True(
                invalidBuild.ExitCode != 0,
                invalidBuild.Output);
            Assert.Contains(
                "error NAM1030",
                invalidBuild.Output,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                "error NAM1034",
                invalidBuild.Output,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task PackagePreparedPagesRunAndBundledAnalyzerRejectsUnguardedAcquisition()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: false,
                suppressDiagnostics: false, executable: true, treatWarningsAsErrors: true);
            string program = Path.Combine(consumerRoot, "Program.cs");
            await File.WriteAllTextAsync(program,
                """
                using Supprocom.NativeAllocationManagement;
                using System.Runtime.InteropServices;
                public static class Consumer
                {
                    public static int Main()
                    {
                        NativeMemoryBudget budget = new(128, traceCapacity: 8);
                        using (NativePool<int> pool = new(new NativePoolPreparation(2, 4, 2), budget))
                        {
                            if (!pool.TryRent(4, static writer => writer.Fill(42), out Pooled<int> lease, out _)) return 1;
                            try { if (lease.Read(static view => view[0]) != 42) return 2; }
                            finally { lease.Dispose(); }
                            NativePreparedPoolStatistics snapshot = pool.CapturePreparedSnapshot();
                            if (pool.GetStatistics().HistoryOverflowed || pool.CaptureDiagnosticSnapshot().HistoryOverflowed) return 16;
                            if (snapshot.AvailableSlotCount != 2 || snapshot.RetainedPageCount != 1
                                || snapshot.PeakOccupiedSlotCount != 1 || snapshot.SuccessfulRentCount != 1
                                || snapshot.RetainedBytes != 128 || snapshot.ManagedBankBytes <= 0) return 3;
                        }
                        System.Span<NativeMemoryTraceEvent> events = stackalloc NativeMemoryTraceEvent[8];
                        if (budget.CopyTraceTo(events) != 4 || budget.CaptureStatistics().CommittedBytes != 0
                            || events[1].Kind != NativeMemoryTraceKind.PageAcquired || events[1].AllocationOrdinal != 1
                            || events[2].Kind != NativeMemoryTraceKind.Prepared || events[3].Kind != NativeMemoryTraceKind.Released) return 4;
                        System.Console.WriteLine("prepared-page-output=42; pages=1; peak-slots=1; final-charge=0");
                        NativeMemoryBudget arenaBudget = new(512);
                        using (NativeArena arena = new(new NativeArenaPreparation(16, 16), arenaBudget))
                        {
                            if (!arena.TryScratch<int>(4, static writer => writer.Fill(17), out ArenaLease<int> values)) return 5;
                            if (values.Read(static view => view[3]) != 17) return 6;
                            NativePreparedArenaStatistics snapshot = arena.CapturePreparedSnapshot();
                            if (snapshot.SuccessfulScratchCount != 1 || snapshot.OrdinaryUsedBytes != 16
                                || snapshot.PeakOrdinaryUsedBytes != 16 || snapshot.ScopedAvailableBytes != 16) return 7;
                        }
                        if (arenaBudget.CaptureStatistics().CommittedBytes != 0) return 8;
                        System.Console.WriteLine("prepared-arena-output=17; final-charge=0");
                        NativeMemoryBudget mappedBudget = new(128);
                        using (MappedBuffer buffer = new())
                        using (NativeArena arena = new(buffer, 0, new NativeArenaPreparation(64, 64), mappedBudget))
                        {
                            ArenaLease<int> input = arena.Scratch<int>(1, static writer => writer.Write(19));
                            try
                            {
                                if (!NativeLeaseOperations.TryInitializeScoped<int, int, int, int, int>(input, arena,
                                    1, 1, 1, 1, static (source, a, b, c, d) =>
                                    { a.Fill(source[0]); b.Fill(2); c.Fill(3); d.Fill(4); },
                                    out ArenaLease<int> a, out ArenaLease<int> b,
                                    out ArenaLease<int> c, out ArenaLease<int> d)) return 11;
                                NativeLeaseOperations.Access(input, a, b, c, d, static (source, a, b, c, d) =>
                                {
                                    if (a[0] != source[0] || b[0] != 2 || c[0] != 3 || d[0] != 4)
                                        throw new System.InvalidOperationException("Mapped grouped parity failed.");
                                });
                            }
                            finally { arena.RecycleScoped(); }
                            NativePreparedArenaStatistics snapshot = arena.CapturePreparedSnapshot();
                            if (snapshot.ActiveBorrowedBytes != 128 || snapshot.RetainedBorrowedBytes != 128
                                || snapshot.RetainedBytes != 128 || snapshot.ScopedUsedBytes != 0) return 9;
                        }
                        if (mappedBudget.CaptureStatistics().CommittedBytes != 0) return 10;
                        System.Console.WriteLine("mapped-group-output=19; final-charge=0");
                        NativeMemoryBudget regionBudget = new(512);
                        using (NativeRegion region = new(regionBudget, 16, NativeMemoryReturn.ToNativeMemory))
                        {
                            Local<int> value = region.Lease<int>(1, static writer => writer.Write(23));
                            if (value.Read(static view => view[0]) != 23) return 12;
                            bool failed = false;
                            try
                            {
                                Local<int> incomplete = region.Lease<int>(2, static writer => writer.Write(1));
                                incomplete.Clear();
                            }
                            catch (System.InvalidOperationException) { failed = true; }
                            if (!failed || region.GetStatistics().RequestedBytes != 4) return 13;
                            Local<int> reused = region.Lease<int>(3, static writer => writer.Fill(29));
                            if (reused.Read(static view => view[2]) != 29
                                || region.GetStatistics().RequestedBytes != 16
                                || regionBudget.CaptureStatistics().AllocationCount != 1) return 14;
                        }
                        if (regionBudget.CaptureStatistics().CommittedBytes != 0) return 15;
                        System.Console.WriteLine("region-reused-output=29; final-charge=0");
                        NativeMemoryStatistics beforeClear = NativeMemoryDiagnostics.Snapshot();
                        using (NativeWorkspace<int> workspace = new(preLease: 2))
                        {
                            workspace.Initialize(2, static writer => writer.Fill(42));
                            workspace.Access(static view => view.Clear());
                            if (workspace.Read(static view => view[1]) != 0) return 17;
                        }
                        NativeMemoryStatistics afterClear = NativeMemoryDiagnostics.Snapshot();
                        if (afterClear.MetricsEpoch != beforeClear.MetricsEpoch
                            || afterClear.HistoryOverflowed
                            || afterClear.AllocationCount != beforeClear.AllocationCount + 1
                            || afterClear.FreeCount != beforeClear.FreeCount + 1
                            || afterClear.OutstandingNativeBytes != beforeClear.OutstandingNativeBytes
                            || afterClear.StorageClearCount != beforeClear.StorageClearCount + 2
                            || afterClear.StorageClearBytes != beforeClear.StorageClearBytes + 16
                            || afterClear.WrittenClearBytes != beforeClear.WrittenClearBytes + 16
                            || afterClear.ZeroedAllocationCount < beforeClear.ZeroedAllocationCount
                            || afterClear.DetachedGenerationCount < beforeClear.DetachedGenerationCount
                            || afterClear.BumpTraversalVisitCount < beforeClear.BumpTraversalVisitCount) return 18;
                        System.Console.WriteLine("accounted-clears=2; cleared-bytes=16; final-backing=unchanged");
                        NativeMemoryStatistics beforeCopy = NativeMemoryDiagnostics.Snapshot();
                        using (NativeBuilder<int> builder = new(preLease: 2))
                        {
                            ReadOnlySpan<int> input = [17, 19];
                            builder.Append(input);
                            using NativeTransfer<int> transfer = builder.Complete();
                            int[] exported = new int[2];
                            transfer.Access(view => view.CopyTo(exported));
                            if (exported[0] != 17 || exported[1] != 19) return 19;
                        }
                        if (NativeMemoryDiagnostics.Snapshot().CopiedBytes != beforeCopy.CopiedBytes + 16) return 20;
                        System.Console.WriteLine("accounted-copy-bytes=16; exported-output=17,19");
                        NativeMemoryBudget uniqueBudget = new(16, 8);
                        using (NativeBuilder<int> uniqueBuilder = new(uniqueBudget, 4))
                        {
                            uniqueBuilder.Append(37);
                            NativeTransfer<int>? source = uniqueBuilder.Complete();
                            NativeTransfer<int> unique = NativeTransfer<int>.Move(ref source);
                            try
                            {
                                NativeTransferStatistics observed = unique.CaptureSnapshot();
                                if (!observed.BindingIsActive || observed.OwnerId != uniqueBuilder.Id
                                    || observed.MoveCount != 1 || observed.BindingVersion != 2
                                    || observed.LiveUniqueOwnerCount != 1 || observed.OwnedBackingBytes != 16
                                    || observed.InitializedPayloadBytes != 4 || observed.ControlFieldBytes <= 0
                                    || unique.Read(static view => view[0]) != 37) return 32;
                            }
                            finally { unique.Dispose(); }
                            NativeTransferStatistics returned = unique.CaptureSnapshot();
                            if (returned.BindingIsActive || returned.LiveUniqueOwnerCount != 0
                                || returned.PayloadReturnCount != 1 || returned.PayloadReturnFailureCount != 0
                                || returned.OwnedBackingBytes != 0 || returned.PeakOwnedBackingBytes != 16
                                || returned.PeakBorrowCount != 1 || !unique.TryCompletePayloadReturn()) return 33;
                        }
                        if (uniqueBudget.CaptureStatistics().CommittedBytes != 0) return 34;
                        System.Console.WriteLine("unique-moves=1; returned=1; failures=0; final-charge=0");
                        NativeMemoryBudget retentionBudget = new(2_000_000);
                        using (NativeArena retention = new(retentionBudget, new NativeArenaRetentionPolicy(4096, 4160), 0, NativeMemoryReturn.ToNativeMemory))
                        {
                            {
                                ArenaLease<int> normal = retention.Scratch<int>(1, static writer => writer.Write(17));
                                ArenaLease<byte> outlier = retention.Scratch<byte>(65536, static writer => writer.Fill(23));
                                if (normal.Read(static view => view[0]) != 17 || outlier.Read(static view => view[65535]) != 23) return 21;
                            }
                            retention.Reset();
                            NativeArenaRetentionStatistics snapshot = retention.CaptureRetentionSnapshot();
                            if (!snapshot.Enabled || snapshot.RetainedBytes != 4160 || snapshot.IdleBytes != 4160
                                || snapshot.OversizedBytes != 0 || snapshot.PeakOversizedBytes != 65600
                                || snapshot.ReleasedBytes != 65600 || snapshot.MaintenanceCount != 1) return 22;
                            ArenaLease<int> next = retention.Scratch<int>(1, static writer => writer.Write(29));
                            retention.MaintainRetention();
                            if (next.Read(static view => view[0]) != 29 || retentionBudget.CaptureStatistics().AllocationCount != 2) return 23;
                        }
                        if (retentionBudget.CaptureStatistics().CommittedBytes != 0) return 24;
                        System.Console.WriteLine("outlier-released=65600; normal-retained=4160; final-charge=0");
                        NativeMemoryBudget sharingBudget = new(32, 16);
                        using (NativeBuilder<int> sharingBuilder = new(sharingBudget, 4))
                        {
                            sharingBuilder.Append(42);
                            NativeTransfer<int>? source = sharingBuilder.Complete();
                            try
                            {
                            NativeShared<int> shared = NativeShared<int>.Create(ref source, new(2, 1));
                            if (!shared.TryDowngrade(out NativeWeak<int> weak, out _))
                            { shared.Dispose(); return 25; }
                            try
                            {
                                try
                                {
                                    if (source.HasValue || shared.Read(static view => view[0]) != 42) return 26;
                                    if (!shared.TryShare(out NativeShared<int> share, out _)) return 27;
                                    try { if (share.Read(static view => view[0]) != 42) return 28; }
                                    finally { share.Dispose(); }
                                    if (!shared.TryDetach(sharingBudget, out NativeTransfer<int> detached)) return 29;
                                    try
                                    {
                                        if (detached.Read(static view => view[0]) != 42
                                            || sharingBudget.CaptureStatistics().CommittedBytes != 20) return 30;
                                    }
                                    finally { detached.Dispose(); }
                                }
                                finally { shared.Dispose(); }
                                if (!weak.IsExpired) return 31;
                                if (weak.TryUpgrade(out NativeShared<int> revived, out _))
                                { revived.Dispose(); return 31; }
                                NativeSharingStatistics snapshot = weak.CaptureSnapshot();
                                if (!snapshot.PayloadReleased || snapshot.PayloadReturnCount != 1
                                    || snapshot.StrongBindingCount != 0 || snapshot.WeakBindingCount != 1
                                    || snapshot.OwnedBackingBytes != 0 || snapshot.ExpiredUpgradeCount != 1
                                    || snapshot.ShareCount != 1 || snapshot.DetachCount != 1
                                    || snapshot.PayloadReturnFailureCount != 0 || snapshot.PeakStrongBindingCount != 2
                                    || sharingBudget.CaptureStatistics().CommittedBytes != 0) return 32;
                            }
                            finally { weak.Dispose(); }
                            }
                            finally { source?.Dispose(); }
                        }
                        System.Console.WriteLine("immutable-shared-output=42; overlap=20; expired-upgrade=refused; final-charge=0");
                        return 0;
                    }
                    private sealed class MappedBuffer : SafeBuffer
                    {
                        private readonly System.IntPtr _allocation;
                        public MappedBuffer() : base(ownsHandle: true)
                        {
                            _allocation = Marshal.AllocHGlobal(191);
                            nuint aligned = checked((nuint)_allocation + 63) & ~(nuint)63;
                            SetHandle((System.IntPtr)aligned);
                            Initialize(128);
                        }
                        protected override bool ReleaseHandle()
                        {
                            Marshal.FreeHGlobal(_allocation);
                            return true;
                        }
                    }
                }
                """);
            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"", consumerRoot);
            _output.WriteLine(restore.Output);
            Assert.True(restore.ExitCode == 0, restore.Output);
            CommandResult build = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo", consumerRoot);
            _output.WriteLine(build.Output);
            Assert.True(build.ExitCode == 0, build.Output);
            CommandResult run = await RunDotnetAsync($"run \"{project}\" --no-build --no-restore --nologo", consumerRoot);
            _output.WriteLine(run.Output);
            Assert.True(run.ExitCode == 0, run.Output);
            await File.WriteAllTextAsync(program,
                """
                using Supprocom.NativeAllocationManagement;
                public static class Consumer
                {
                    public static void Main()
                    {
                        using NativePool<int> pool = new(new NativePoolPreparation(2, 4, 2), null);
                        pool.TryRent(4, static writer => writer.Fill(42), out Pooled<int> lease, out _);
                        lease.Dispose();
                    }
                }
                """);
            CommandResult invalid = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo -t:Rebuild", consumerRoot);
            _output.WriteLine(invalid.Output);
            Assert.NotEqual(0, invalid.ExitCode);
            Assert.Contains("error NAM1050", invalid.Output, StringComparison.OrdinalIgnoreCase);
            await File.WriteAllTextAsync(program,
                """
                using Supprocom.NativeAllocationManagement;
                public static class Consumer
                {
                    public static void Main()
                    {
                        using NativeArena arena = new(new NativeArenaPreparation(16, 16), new NativeMemoryBudget(512));
                        arena.TryScratch<int>(4, static writer => writer.Fill(17), out ArenaLease<int> values);
                        values.Clear();
                    }
                }
                """);
            CommandResult invalidArena = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo -t:Rebuild", consumerRoot);
            _output.WriteLine(invalidArena.Output);
            Assert.NotEqual(0, invalidArena.ExitCode);
            Assert.Contains("error NAM1050", invalidArena.Output, StringComparison.OrdinalIgnoreCase);
            await File.WriteAllTextAsync(program,
                """
                using Supprocom.NativeAllocationManagement;
                public static class Consumer
                {
                    public static void Main()
                    {
                        using NativeArena arena = new(new NativeArenaPreparation(16, 16), new NativeMemoryBudget(512));
                        ArenaLease<int> input = arena.Scratch<int>(1, static writer => writer.Write(17));
                        try
                        {
                            NativeLeaseOperations.TryInitializeScoped<int, int, int, int, int>(input, arena,
                                1, 1, 1, 1, static (_, a, b, c, d) =>
                                { a.Fill(1); b.Fill(2); c.Fill(3); d.Fill(4); },
                                out ArenaLease<int> a, out ArenaLease<int> b, out ArenaLease<int> c, out ArenaLease<int> d);
                            d.Clear();
                        }
                        finally { arena.RecycleScoped(); }
                    }
                }
                """);
            CommandResult invalidGroup = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo -t:Rebuild", consumerRoot);
            _output.WriteLine(invalidGroup.Output);
            Assert.NotEqual(0, invalidGroup.ExitCode);
            Assert.Contains("error NAM1050", invalidGroup.Output, StringComparison.OrdinalIgnoreCase);
            await File.WriteAllTextAsync(program,
                """
                using Supprocom.NativeAllocationManagement;
                public static class Consumer
                {
                    public static void Main()
                    {
                        using NativeBuilder<int> builder = new(4);
                        builder.Append(42);
                        NativeTransfer<int>? source = builder.Complete();
                        using NativeShared<int> shared = NativeShared<int>.Create(ref source, new(2, 1));
                        shared.TryShare(out NativeShared<int> share, out _);
                        share.Dispose();
                    }
                }
                """);
            CommandResult invalidSharing = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo -t:Rebuild", consumerRoot);
            _output.WriteLine(invalidSharing.Output);
            Assert.NotEqual(0, invalidSharing.ExitCode);
            Assert.Contains("error NAM1050", invalidSharing.Output, StringComparison.OrdinalIgnoreCase);
            await File.WriteAllTextAsync(program,
                """
                using Supprocom.NativeAllocationManagement;
                public static class Consumer
                {
                    public static void Main()
                    {
                        using NativeBuilder<int> builder = new(4);
                        builder.Append(42);
                        NativeTransfer<int>? source = builder.Complete();
                        using NativeShared<int> shared = NativeShared<int>.Create(ref source, new(2, 0));
                        NativeShared<int>[] aliases = [shared];
                    }
                }
                """);
            CommandResult sharingAlias = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo -t:Rebuild", consumerRoot);
            _output.WriteLine(sharingAlias.Output);
            Assert.NotEqual(0, sharingAlias.ExitCode);
            Assert.Contains("error NAM1021", sharingAlias.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task PackageReferenceStorageUsesNativeSlotsForReferencesAcrossReuse()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: false, suppressDiagnostics: false, executable: true);
            await File.WriteAllTextAsync(
                Path.Combine(consumerRoot, "Program.cs"),
                """
                using Supprocom.NativeAllocationManagement;

                public struct ReferenceCell
                {
                    public string? Text { get; set; }
                    public int Number { get; set; }
                }

                public static class Consumer
                {
                    public static int Main()
                    {
                        bool valid = true;
                        NativeConcurrentPool<string> pool = new(preLease: 2);
                        ConcurrentPooled<string> first = pool.Rent(2, static writer => writer.Fill(default!));
                        first.Access(static view =>
                        {
                            view[0] = "first";
                            view[1] = "second";
                        });
                        valid &= first.Read(static view =>
                            view[0] == "first" && view[1] == "second");

                        first.Dispose();
                        ConcurrentPooled<string> reused = pool.Rent(2, static writer => writer.Fill(default!));
                        valid &= reused.Read(static view =>
                            view[0] is null && view[1] is null);

                        reused.Dispose();
                        pool.Dispose();

                        NativeConcurrentArena arena = new();
                        {
                            ConcurrentArenaLease<ReferenceCell> firstArena = arena.Scratch<ReferenceCell>(1, static writer => writer.Fill(default!));
                            firstArena.Access(static view =>
                                view[0] = new ReferenceCell
                                {
                                    Text = "arena",
                                    Number = 4
                                });
                            valid &= firstArena.Read(static view =>
                                view[0].Text == "arena"
                                && view[0].Number == 4);
                        }

                        arena.ReleaseLeasesToNativeMemory();
                        {
                            ConcurrentArenaLease<ReferenceCell> reusedArena = arena.Scratch<ReferenceCell>(1, static writer => writer.Fill(default!));
                            valid &= reusedArena.Read(static view =>
                                view[0].Text is null
                                && view[0].Number == 0);
                        }

                        arena.Dispose();
                        return valid ? 0 : 15;
                    }
                }
                """);

            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"",
                consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);

            CommandResult build = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo", consumerRoot);
            Assert.True(build.ExitCode == 0, build.Output);

            CommandResult run = await RunDotnetAsync($"run \"{project}\" --no-build --no-restore --nologo", consumerRoot);
            Assert.True(run.ExitCode == 0, run.Output);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task PackageAnalyzerRejectsScopedAcquisitionThroughNonExclusiveArenaReceiver()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: false, suppressDiagnostics: false);
            await File.WriteAllTextAsync(
                Path.Combine(consumerRoot, "Consumer.cs"),
                """
                using Supprocom.NativeAllocationManagement;

                public static class Consumer
                {
                    public static void Run(NativeConcurrentArena arena)
                    {
                        scoped ConcurrentArenaLease<int> values = arena.ScratchScoped<int>(1, static writer => writer.Fill(default!));
                        values.Access(static view => view[0] = 1);
                        arena.RecycleScoped();
                    }
                }
                """);

            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"",
                consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);

            CommandResult build = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo", consumerRoot);
            Assert.True(build.ExitCode != 0, build.Output);
            Assert.Contains("NAM1018", build.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("NAM1007", build.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task PackageAnalyzerAcceptsExplicitRegionUsingStatement()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: false, suppressDiagnostics: false, executable: true);
            await File.WriteAllTextAsync(
                Path.Combine(consumerRoot, "Program.cs"),
                """
                using Supprocom.NativeAllocationManagement;

                using (NativeRegion region = new())
                {
                    Local<int> value = region.Lease<int>(1, static writer => writer.Fill(default!));
                    value.Access(static values => values[0] = 42);
                }
                """);

            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"",
                consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);

            CommandResult build = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo", consumerRoot);
            Assert.True(build.ExitCode == 0, build.Output);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task PackageAnalyzerAcceptsTopLevelRegionUsingDeclaration()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: false, suppressDiagnostics: false, executable: true);
            await File.WriteAllTextAsync(
                Path.Combine(consumerRoot, "Program.cs"),
                """
                using Supprocom.NativeAllocationManagement;

                using NativeRegion region = new();
                Local<int> value = region.Lease<int>(1, static writer => writer.Fill(default!));
                value.Access(static values => values[0] = 42);
                """);

            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"",
                consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);

            CommandResult build = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo", consumerRoot);
            Assert.True(build.ExitCode == 0, build.Output);
            Assert.DoesNotContain("NAM1006", build.Output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("NAM1012", build.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task PackageAnalyzerAcceptsNestedRegionUsingDeclarations()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: false, suppressDiagnostics: false, executable: true);
            await File.WriteAllTextAsync(
                Path.Combine(consumerRoot, "Program.cs"),
                """
                using Supprocom.NativeAllocationManagement;

                using NativeRegion outer = new();
                using NativeRegion inner = new();
                Local<int> value = outer.Lease<int>(1, static writer => writer.Fill(default!));
                value.Access(static values => values[0] = 42);
                """);

            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"",
                consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);

            CommandResult build = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo", consumerRoot);
            Assert.True(build.ExitCode == 0, build.Output);
            Assert.DoesNotContain("NAM1006", build.Output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("NAM1010", build.Output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("NAM1012", build.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task PackageAnalyzerAcceptsBlockRegionUsingDeclaration()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: false, suppressDiagnostics: false);
            await File.WriteAllTextAsync(
                Path.Combine(consumerRoot, "Program.cs"),
                """
                using Supprocom.NativeAllocationManagement;

                public static class Consumer
                {
                    public static void Run()
                    {
                        using NativeRegion region = new();
                        Local<int> value = region.Lease<int>(1, static writer => writer.Fill(default!));
                        value.Access(static values => values[0] = 42);
                    }
                }
                """);

            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"",
                consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);

            CommandResult build = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo", consumerRoot);
            Assert.True(build.ExitCode == 0, build.Output);
            Assert.DoesNotContain("NAM1006", build.Output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("NAM1012", build.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task PackageManagedAllocationWarningFollowsWarningsAsErrorsPolicy()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(
                consumerRoot,
                package,
                excludeAnalyzer: false,
                suppressDiagnostics: false,
                treatWarningsAsErrors: true);
            await File.WriteAllTextAsync(
                Path.Combine(consumerRoot, "Program.cs"),
                """
                using Supprocom.NativeAllocationManagement;

                public static class Consumer
                {
                    public static void Run()
                    {
                        using NativeRegion region = new();
                        object value = new object();
                        _ = value;
                    }
                }
                """);

            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"",
                consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);

            CommandResult build = await RunDotnetAsync(
                $"build \"{project}\" --no-restore --nologo",
                consumerRoot);
            Assert.True(build.ExitCode != 0, build.Output);
            Assert.Contains("NAM1035", build.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task PackageNamAllowAcceptsOnlyTheManagedAllocationWarning()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(
                consumerRoot,
                package,
                excludeAnalyzer: false,
                suppressDiagnostics: false,
                treatWarningsAsErrors: true);
            string program = Path.Combine(consumerRoot, "Program.cs");
            await File.WriteAllTextAsync(
                program,
                """
                using Supprocom.NativeAllocationManagement;

                public static class Consumer
                {
                    public static void Run()
                    {
                        using NativeRegion region = new();
                        // NAMALLOW
                        object value = new object();
                        _ = value;
                    }
                }
                """);

            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"",
                consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);

            CommandResult accepted = await RunDotnetAsync(
                $"build \"{project}\" --no-restore --nologo",
                consumerRoot);
            Assert.True(accepted.ExitCode == 0, accepted.Output);

            await File.WriteAllTextAsync(
                program,
                """
                using Supprocom.NativeAllocationManagement;

                public static class Consumer
                {
                    public static void Run()
                    {
                        using NativeRegion region = new();
                        // NAMALLOW
                        object value = new object();
                        _ = value;
                        NativeRegion invalid = new();
                    }
                }
                """);

            CommandResult rejected = await RunDotnetAsync(
                $"build \"{project}\" --no-restore --nologo",
                consumerRoot);
            Assert.True(rejected.ExitCode != 0, rejected.Output);
            Assert.Contains("NAM1006", rejected.Output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("NAM1035", rejected.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task PackageAnalyzerRejectsPreActivationUse()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: false, suppressDiagnostics: false);
            await File.WriteAllTextAsync(
                Path.Combine(consumerRoot, "Program.cs"),
                """
                using Supprocom.NativeAllocationManagement;

                public static class Consumer
                {
                    public static void Run()
                    {
                        NativeConcurrentPool<int> pool = new(doNotLeaseOnDeclaration: true);
                        _ = pool.Rent(1, static writer => writer.Fill(default!));
                        pool.Dispose();

                        using (NativeRegion region = new())
                        {
                            Local<int> value = region.Lease<int>(1, static writer => writer.Fill(default!));
                            _ = value.Length;
                        }
                    }
                }
                """);

            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"",
                consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);

            CommandResult build = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo", consumerRoot);
            Assert.True(build.ExitCode != 0, build.Output);
            Assert.Contains("NAM1009", build.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task PackageArenaRequiresScratchOperation()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: false, suppressDiagnostics: false);
            await File.WriteAllTextAsync(
                Path.Combine(consumerRoot, "Program.cs"),
                """
                using Supprocom.NativeAllocationManagement;

                public static class Consumer
                {
                    public static void Run()
                    {
                        using NativeConcurrentArena arena = new();
                        _ = arena.Lease<int>(1);
                    }
                }
                """);

            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"",
                consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);

            CommandResult build = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo", consumerRoot);
            Assert.True(build.ExitCode != 0, build.Output);
            Assert.Contains("CS1061", build.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task PackageRegionDoesNotExposeAllocateOperation()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: false, suppressDiagnostics: false);
            await File.WriteAllTextAsync(
                Path.Combine(consumerRoot, "Program.cs"),
                """
                using Supprocom.NativeAllocationManagement;

                public static class Consumer
                {
                    public static void Run()
                    {
                        using (NativeRegion region = new())
                        {
                            _ = region.Allocate<int>(1);
                        }
                    }
                }
                """);

            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"",
                consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);

            CommandResult build = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo", consumerRoot);
            Assert.True(build.ExitCode != 0, build.Output);
            Assert.Contains("CS1061", build.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task PackageRejectsTrimOnDerivedHandlesAndRemovedLifecycleSpellings()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: false, suppressDiagnostics: false);
            await File.WriteAllTextAsync(
                Path.Combine(consumerRoot, "Program.cs"),
                """
                using Supprocom.NativeAllocationManagement;

                public static class Consumer
                {
                    public static void Run()
                    {
                        NativeConcurrentPool<int> pool = new();
                        ConcurrentPooled<int> value = pool.Rent(1, static writer => writer.Fill(default!));
                        _ = value.TrimRetainedMemory();
                        pool.ReturnToNativeMemory();
                    }
                }
                """);

            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"",
                consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);

            CommandResult build = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo", consumerRoot);
            Assert.True(build.ExitCode != 0, build.Output);
            Assert.Contains("CS1061", build.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("TrimRetainedMemory", build.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ReturnToNativeMemory", build.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task PackageAnalyzerRejectsNestedRootAbandonedBeforeOwnerDisposeInIsolatedConsumer()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: false, suppressDiagnostics: false);
            await File.WriteAllTextAsync(
                Path.Combine(consumerRoot, "Program.cs"),
                """
                using Supprocom.NativeAllocationManagement;

                public static class Consumer
                {
                    public static void AbandonNestedLease()
                    {
                        NativeConcurrentPool<int> pool = new();
                        {
                            ConcurrentPooled<int> value = pool.Rent(1, static writer => writer.Fill(default!));
                        }

                        pool.Dispose();
                    }

                    public static void AbandonNestedLeaseBeforeOwnerDispose()
                    {
                        NativeConcurrentPool<int> pool = new();
                        {
                            ConcurrentPooled<int> value = pool.Rent(1, static writer => writer.Fill(default!));
                        }

                        pool.Dispose();
                    }
                }
                """);

            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"",
                consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);

            CommandResult build = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo", consumerRoot);
            Assert.True(build.ExitCode != 0, build.Output);
            string[] diagnostics = build.Output
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.Contains("error NAM1003", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(2, diagnostics.Length);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task PackageAnalyzerRejectsStaleHandleInAnIsolatedConsumer()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: false, suppressDiagnostics: false);
            await File.WriteAllTextAsync(
                Path.Combine(consumerRoot, "Program.cs"),
                """
                using Supprocom.NativeAllocationManagement;

                public static class Consumer
                {
                    public static void Run()
                    {
                        NativeConcurrentPool<int> pool = new();
                        ConcurrentPooled<int> stale = pool.Rent(1, static writer => writer.Fill(default!));
                        pool.ReturnMemoryToNativeMemory();
                        _ = stale.Length;
                    }
                }
                """);

            CommandResult restore = await RunDotnetAsync(
                $"restore \"{Path.Combine(consumerRoot, "Consumer.csproj")}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"",
                consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);

            CommandResult build = await RunDotnetAsync(
                $"build \"{Path.Combine(consumerRoot, "Consumer.csproj")}\" --no-restore --nologo",
                consumerRoot);
            Assert.True(build.ExitCode != 0, build.Output);
            Assert.True(build.Output.Contains("NAM1004", StringComparison.OrdinalIgnoreCase), build.Output);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task RemovingThePackageAnalyzerFailsThroughBuildTransitiveVerification()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: true, suppressDiagnostics: false);
            await File.WriteAllTextAsync(
                Path.Combine(consumerRoot, "Program.cs"),
                """
                using Supprocom.NativeAllocationManagement;

                public static class Consumer
                {
                    public static void Run()
                    {
                        using NativeConcurrentPool<int> pool = new();
                        using ConcurrentPooled<int> values = pool.Rent(1, static writer => writer.Fill(default!));
                        values.Access(static view => view[0] = 7);
                    }
                }
                """);

            CommandResult restore = await RunDotnetAsync(
                $"restore \"{Path.Combine(consumerRoot, "Consumer.csproj")}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"",
                consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);

            CommandResult build = await RunDotnetAsync(
                $"build \"{Path.Combine(consumerRoot, "Consumer.csproj")}\" --no-restore --nologo",
                consumerRoot);
            Assert.True(build.ExitCode != 0, build.Output);
            Assert.True(build.Output.Contains("NAM9001", StringComparison.OrdinalIgnoreCase), build.Output);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task DeferredReturnWarningFollowsConsumerWarningsAsErrorsPolicy()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(
                consumerRoot,
                package,
                excludeAnalyzer: false,
                suppressDiagnostics: false);
            await File.WriteAllTextAsync(
                Path.Combine(consumerRoot, "Consumer.cs"),
                """
                using Supprocom.NativeAllocationManagement;

                public static class Consumer
                {
                    public static void Run()
                    {
                        NativeConcurrentPool<int> pool = new();
                        ConcurrentPooled<int> values = pool.Rent(1, static writer => writer.Fill(default!));
                        pool.ReturnMemoryToGarbageCollector();
                        pool.Dispose();
                    }
                }
                """);

            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"",
                consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);

            CommandResult warningBuild = await RunDotnetAsync(
                $"build \"{project}\" --no-restore --nologo",
                consumerRoot);
            Assert.True(warningBuild.ExitCode == 0, warningBuild.Output);
            Assert.Contains("warning NAM1017", warningBuild.Output, StringComparison.OrdinalIgnoreCase);

            WriteConsumerProject(
                consumerRoot,
                package,
                excludeAnalyzer: false,
                suppressDiagnostics: false,
                treatWarningsAsErrors: true);
            CommandResult errorBuild = await RunDotnetAsync(
                $"build \"{project}\" --no-restore --nologo",
                consumerRoot);
            Assert.True(errorBuild.ExitCode != 0, errorBuild.Output);
            Assert.Contains("error NAM1017", errorBuild.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task NativeMemoryReturnLiveRootIsAHardPackageAnalyzerError()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: false, suppressDiagnostics: false);
            await File.WriteAllTextAsync(
                Path.Combine(consumerRoot, "Consumer.cs"),
                """
                using Supprocom.NativeAllocationManagement;

                public static class Consumer
                {
                    public static void Run()
                    {
                        NativeConcurrentPool<int> pool = new();
                        ConcurrentPooled<int> value = pool.Rent(1, static writer => writer.Fill(default!));
                        pool.ReturnMemoryToNativeMemory();
                        pool.Dispose();
                    }
                }
                """);

            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"",
                consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);

            CommandResult build = await RunDotnetAsync(
                $"build \"{project}\" --no-restore --nologo",
                consumerRoot);
            Assert.True(build.ExitCode != 0, build.Output);
            Assert.Contains("error NAM1007", build.Output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("NAM1017", build.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task ScopedCompletionWarningFollowsConsumerWarningsAsErrorsPolicy()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: false, suppressDiagnostics: false);
            await File.WriteAllTextAsync(
                Path.Combine(consumerRoot, "Consumer.cs"),
                """
                using Supprocom.NativeAllocationManagement;

                public static class Consumer
                {
                    public static void Run()
                    {
                        using NativeConcurrentArena arena = new();
                        {
                            scoped ConcurrentArenaLease<int> values = arena.ScratchScoped<int>(1, static writer => writer.Fill(default!));
                            values.Access(static view => view[0] = 1);
                        }
                    }
                }
                """);

            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"",
                consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);

            CommandResult warningBuild = await RunDotnetAsync(
                $"build \"{project}\" --no-restore --nologo",
                consumerRoot);
            Assert.True(warningBuild.ExitCode == 0, warningBuild.Output);
            Assert.Contains("warning NAM1020", warningBuild.Output, StringComparison.OrdinalIgnoreCase);

            WriteConsumerProject(
                consumerRoot,
                package,
                excludeAnalyzer: false,
                suppressDiagnostics: false,
                treatWarningsAsErrors: true);
            CommandResult errorBuild = await RunDotnetAsync(
                $"build \"{project}\" --no-restore --nologo",
                consumerRoot);
            Assert.True(errorBuild.ExitCode != 0, errorBuild.Output);
            Assert.Contains("error NAM1020", errorBuild.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task SuppressedAnalyzerStillGetsTheRuntimeStaleHandleGuard()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: false, suppressDiagnostics: true);
            await File.WriteAllTextAsync(
                Path.Combine(consumerRoot, "Program.cs"),
                """
                using Supprocom.NativeAllocationManagement;

                public static class Consumer
                {
                    public static int Main()
                    {
                        NativeConcurrentPool<int> deferredPool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
                        ConcurrentPooled<int> borrowed = deferredPool.Rent(1, static writer => writer.Fill(default!));
                        bool callbackCompleted = false;
                        borrowed.Access(span =>
                        {
                            deferredPool.ReturnMemoryToGarbageCollector();
                            span[0] = 42;
                            callbackCompleted = span[0] == 42;
                        });
                        if (!callbackCompleted)
                        {
                            return 12;
                        }

                        try
                        {
                            _ = borrowed.Length;
                            return 13;
                        }
                        catch (NativeAllocationReturnedException)
                        {
                            borrowed.Dispose();
                        }

                        deferredPool.LeaseFromMemory();
                        ConcurrentPooled<int> current = deferredPool.Rent(1, static writer => writer.Fill(default!));
                        if (current.Read(static view => view[0]) != 0)
                        {
                            return 14;
                        }

                        current.Dispose();
                        deferredPool.Dispose();

                        NativeConcurrentPool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
                        ConcurrentPooled<int> stale = pool.Rent(1, static writer => writer.Fill(default!));
                        pool.ReturnMemoryToNativeMemory();
                        try
                        {
                            _ = stale.Length;
                            return 10;
                        }
                        catch (NativeAllocationReturnedException)
                        {
                            pool.Dispose();
                            stale.Dispose();
                        }

                        NativeConcurrentPool<int> guardedPool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
                        ConcurrentPooled<int> guarded = guardedPool.Rent(1, static writer => writer.Fill(default!));
                        try
                        {
                            guarded.Access(_ => guardedPool.ReturnMemoryToNativeMemory());
                            return 11;
                        }
                        catch (NativeAllocationInUseException)
                        {
                            guarded.Dispose();
                            guardedPool.Dispose();
                            return 0;
                        }
                    }
                }
                """);

            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"",
                consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);

            CommandResult build = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo", consumerRoot);
            Assert.True(build.ExitCode == 0, build.Output);

            CommandResult run = await RunDotnetAsync($"run \"{project}\" --no-build --no-restore --nologo", consumerRoot);
            Assert.True(run.ExitCode == 0, run.Output);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task SuppressedAnalyzerStillGetsOwnerWideReturnAndDetachedGenerationGuards()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: false, suppressDiagnostics: true, executable: true);
            await File.WriteAllTextAsync(
                Path.Combine(consumerRoot, "Program.cs"),
                """
                using Supprocom.NativeAllocationManagement;

                public static class Consumer
                {
                    public static int Main()
                    {
                        NativeConcurrentPool<int> strictPool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
                        ConcurrentPooled<int> strictBorrow = strictPool.Rent(1, static writer => writer.Fill(default!));
                        bool strictRejected = false;
                        strictBorrow.Access(span =>
                        {
                            strictPool.ReleaseLeasesToGarbageCollector();
                            try
                            {
                                strictPool.ReturnMemoryToNativeMemory();
                            }
                            catch (NativeAllocationInUseException)
                            {
                                strictRejected = true;
                            }

                            span[0] = 41;
                            strictRejected &= span[0] == 41;
                        });

                        if (!strictRejected)
                        {
                            return 1;
                        }

                        strictPool.ReturnMemoryToNativeMemory();
                        strictPool.Dispose();

                        NativeConcurrentPool<int> gcPool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
                        ConcurrentPooled<int> detachedBorrow = gcPool.Rent(1, static writer => writer.Fill(default!));
                        bool detachedOperationWasValid = false;
                        detachedBorrow.Access(span =>
                        {
                            gcPool.ReleaseLeasesToGarbageCollector();
                            gcPool.ReturnMemoryToGarbageCollector();
                            gcPool.LeaseFromMemory();
                            ConcurrentPooled<int> freshInsideCallback = gcPool.Rent(1, static writer => writer.Fill(default!));
                            bool freshWasZeroed = freshInsideCallback.Read(
                                static view => view[0]) == 0;
                            freshInsideCallback.Dispose();
                            gcPool.ReturnMemoryToNativeMemory();
                            gcPool.Dispose();
                            span[0] = 19;
                            detachedOperationWasValid = freshWasZeroed && span[0] == 19;
                        });

                        if (!detachedOperationWasValid)
                        {
                            return 2;
                        }

                        return 0;
                    }
                }
                """);

            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"",
                consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);

            CommandResult build = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo", consumerRoot);
            Assert.True(build.ExitCode == 0, build.Output);

            CommandResult run = await RunDotnetAsync($"run \"{project}\" --no-build --no-restore --nologo", consumerRoot);
            Assert.True(run.ExitCode == 0, run.Output);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    private void WriteEvidence(PackageEvidence package)
    {
        _output.WriteLine($"package={package.Path}");
        _output.WriteLine($"version={package.Version}");
        _output.WriteLine($"commit={package.RepositoryCommit}");
        _output.WriteLine($"artifactSha256={package.ArtifactSha256}");
        _output.WriteLine($"runtimeSha256={package.RuntimeAssemblySha256}");
        _output.WriteLine($"analyzerSha256={package.AnalyzerAssemblySha256}");
    }

    private static void WriteConsumerProject(
        string consumerRoot,
        PackageEvidence package,
        bool excludeAnalyzer,
        bool suppressDiagnostics,
        bool executable = false,
        bool treatWarningsAsErrors = false)
    {
        string analyzerAssets = excludeAnalyzer ? " ExcludeAssets=\"analyzers\"" : string.Empty;
        string outputType = executable || suppressDiagnostics ? "Exe" : "Library";
        string warningsAsErrors = treatWarningsAsErrors
            ? "<TreatWarningsAsErrors>true</TreatWarningsAsErrors>"
            : string.Empty;
        string analyzerRemovalTarget = excludeAnalyzer
            ? """
              <Target Name="RemoveBundledAnalyzerAsset" BeforeTargets="NAMVerifyAnalyzerPresence">
                <ItemGroup>
                  <Analyzer Remove="@(Analyzer)" />
                </ItemGroup>
              </Target>
            """
            : string.Empty;
        string noWarn = suppressDiagnostics
            ? "<NoWarn>$(NoWarn);NAM1003;NAM1004;NAM1007;NAM1009;NAM1017</NoWarn>"
            : string.Empty;
        File.WriteAllText(
            Path.Combine(consumerRoot, "Consumer.csproj"),
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>{outputType}</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                {noWarn}
                {warningsAsErrors}
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Supprocom.NativeAllocationManagement" Version="{package.Version}"{analyzerAssets} />
              </ItemGroup>
              {analyzerRemovalTarget}
            </Project>
            """);
    }

    private static async Task<PackageEvidence> GetPackageAsync()
    {
        await PackageGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_package is not null)
            {
                return _package;
            }

            string repositoryRoot = FindRepositoryRoot();
            string version = "0.1.0-smoke."
                + Guid.NewGuid().ToString("N")[..12];
            string packageDirectory = Path.Combine(Path.GetTempPath(), "nam-package-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(packageDirectory);
            string packagePath = Path.Combine(packageDirectory, $"Supprocom.NativeAllocationManagement.{version}.nupkg");
            CommandResult pack = await RunDotnetAsync(
                $"pack Supprocom.NativeAllocationManagement\\Supprocom.NativeAllocationManagement.csproj --no-restore --nologo -c Release -p:PackageVersion={version} -p:PackageOutputPath=\"{packageDirectory}\"",
                repositoryRoot).ConfigureAwait(true);
            Assert.True(pack.ExitCode == 0, pack.Output);
            Assert.True(File.Exists(packagePath), pack.Output);

            PackageEvidence evidence = ReadPackage(packagePath, packageDirectory, version);
            string expectedCommit = await ReadGitHeadAsync(repositoryRoot).ConfigureAwait(true);
            Assert.Equal(expectedCommit, evidence.RepositoryCommit);
            _package = evidence;
            return evidence;
        }
        finally
        {
            PackageGate.Release();
        }
    }

    private static PackageEvidence ReadPackage(string packagePath, string sourceDirectory, string version)
    {
        using FileStream stream = File.OpenRead(packagePath);
        string artifactHash = Convert.ToHexString(SHA256.HashData(stream));
        stream.Position = 0;
        using ZipArchive archive = new(stream, ZipArchiveMode.Read, leaveOpen: false);
        ZipArchiveEntry nuspecEntry = archive.GetEntry("Supprocom.NativeAllocationManagement.nuspec")
            ?? throw new InvalidDataException("The package does not contain its nuspec.");
        using StreamReader reader = new(nuspecEntry.Open());
        XDocument nuspec = XDocument.Parse(reader.ReadToEnd());
        string commit = nuspec.Descendants().First(element => string.Equals(element.Name.LocalName, "repository", StringComparison.Ordinal)).Attribute("commit")?.Value
            ?? throw new InvalidDataException("The package nuspec does not contain repository commit metadata.");
        string authors = nuspec.Descendants().First(element => string.Equals(element.Name.LocalName, "authors", StringComparison.Ordinal)).Value;
        string description = nuspec.Descendants().First(element => string.Equals(element.Name.LocalName, "description", StringComparison.Ordinal)).Value;
        XElement licenseElement = nuspec.Descendants().First(element => string.Equals(element.Name.LocalName, "license", StringComparison.Ordinal));
        string license = licenseElement.Attribute("type")?.Value
            ?? throw new InvalidDataException("The package nuspec does not contain license metadata.");
        Assert.Equal("Supprocom", authors);
        Assert.DoesNotContain("Package Description", description, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("expression", license);
        Assert.Equal("AGPL-3.0-only", licenseElement.Value);
        string runtimeHash = HashEntry(archive, "lib/net10.0/Supprocom.NativeAllocationManagement.dll");
        string analyzerHash = HashEntry(archive, "analyzers/dotnet/cs/Supprocom.NativeAllocationManagement.Analyzers.dll");
        return new PackageEvidence(packagePath, sourceDirectory, version, commit, artifactHash, runtimeHash, analyzerHash);
    }

    private static string HashEntry(ZipArchive archive, string name)
    {
        ZipArchiveEntry entry = archive.GetEntry(name) ?? throw new InvalidDataException($"The package does not contain {name}.");
        using Stream content = entry.Open();
        return Convert.ToHexString(SHA256.HashData(content));
    }

    private static async Task<string> ReadGitHeadAsync(string repositoryRoot)
    {
        CommandResult result = await RunProcessAsync("git", "rev-parse HEAD", repositoryRoot).ConfigureAwait(true);
        Assert.Equal(0, result.ExitCode);
        return result.Output.Trim();
    }

    private static async Task<CommandResult> RunDotnetAsync(string arguments, string workingDirectory)
    {
        return await RunProcessAsync(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet", arguments, workingDirectory).ConfigureAwait(true);
    }

    private static async Task<CommandResult> RunProcessAsync(string fileName, string arguments, string workingDirectory)
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        Assert.True(process.Start(), $"The {fileName} process did not start.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException exception)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw new TimeoutException($"{fileName} {arguments} exceeded the 90 second smoke-test timeout.", exception);
        }

        string output = await stdout.ConfigureAwait(true) + Environment.NewLine + await stderr.ConfigureAwait(true);
        return new CommandResult(process.ExitCode, output);
    }

    private static string FindRepositoryRoot()
        => RepositoryTestPaths.Root;

    private static string CreateConsumerRoot()
    {
        string path = Path.Combine(Path.GetTempPath(), "nam-package-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteConsumerRoot(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed record PackageEvidence(
        string Path,
        string SourceDirectory,
        string Version,
        string RepositoryCommit,
        string ArtifactSha256,
        string RuntimeAssemblySha256,
        string AnalyzerAssemblySha256);

    private sealed class CommandResult
    {
        internal CommandResult(int exitCode, string output)
        {
            ExitCode = exitCode;
            Output = output;
        }

        internal int ExitCode { get; }
        internal string Output { get; }
    }
}
