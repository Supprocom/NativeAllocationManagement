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
    private static readonly System.Text.Json.JsonSerializerOptions SymbolJsonOptions = new() { WriteIndented = true };
    private readonly ITestOutputHelper _output;

    public PackageSmokeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PackageAnalyzerRejectsActivePooledBindingReplacement(bool prepared)
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: false, suppressDiagnostics: false);
            string replacement = prepared
                ? "if (pool.TryRent(4, static writer => writer.Fill(2), out lease, out _)) lease.Dispose();"
                : "lease = pool.Rent(4, static writer => writer.Fill(2)); lease.Dispose();";
            await File.WriteAllTextAsync(Path.Combine(consumerRoot, "Program.cs"), $$"""
                using Supprocom.NativeAllocationManagement;
                public static class Consumer
                {
                    public static void Run()
                    {
                        using NativePool<int> pool = new(new NativePoolPreparation(4, 16, 2), null);
                        if (!pool.TryRent(4, static writer => writer.Fill(1), out Pooled<int> lease, out _)) return;
                        {{replacement}}
                    }
                }
                """);
            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"", consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);
            CommandResult build = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo", consumerRoot);
            Assert.True(build.ExitCode != 0, build.Output);
            Assert.Contains("error NAM1003", build.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("error CS", build.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("AD0001", build.Output, StringComparison.Ordinal);
        }
        finally { DeleteConsumerRoot(consumerRoot); }
    }

    [Fact]
    public async Task PackageAnalyzerAcceptsCompletedPooledBindingReplacementAndExecution()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: false, suppressDiagnostics: false,
                executable: true, treatWarningsAsErrors: true);
            await File.WriteAllTextAsync(Path.Combine(consumerRoot, "Program.cs"), """
                using System;
                using Supprocom.NativeAllocationManagement;
                using NativePool<int> pool = new(new NativePoolPreparation(4, 16, 2), null);
                if (!pool.TryRent(4, static writer => writer.Fill(1), out Pooled<int> lease, out _)) throw new InvalidOperationException();
                lease.Dispose();
                if (!pool.TryRent(4, static writer => writer.Fill(2), out lease, out _)) throw new InvalidOperationException();
                int value = lease.Read(static view => view[0]);
                lease.Dispose();
                if (value != 2) throw new InvalidOperationException();
                Console.WriteLine("lease-replacement completed=2 value=2");
                """);
            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"", consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);
            CommandResult build = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo", consumerRoot);
            Assert.True(build.ExitCode == 0, build.Output);
            CommandResult run = await RunDotnetAsync($"run --project \"{project}\" --no-build --no-restore", consumerRoot);
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Contains("lease-replacement completed=2 value=2", run.Output, StringComparison.Ordinal);
        }
        finally { DeleteConsumerRoot(consumerRoot); }
    }

    [Fact]
    public async Task PackageEventDiagnosticOracleVerifiesCompleteOrderedHistories()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: false, suppressDiagnostics: false,
                executable: true, treatWarningsAsErrors: true);
            foreach (string name in new[] { "event", "trace" })
                File.Copy(Path.Combine(FindRepositoryRoot(), "conformance", "native-" + name + "-diagnostic-oracle.cs"),
                    Path.Combine(consumerRoot, "Native" + char.ToUpperInvariant(name[0]) + name[1..] + "DiagnosticOracle.cs"));
            await File.WriteAllTextAsync(Path.Combine(consumerRoot, "Program.cs"),
                """
                using Supprocom.NativeAllocationManagement.Conformance;

                foreach (int capacity in new[] { 0, 64 })
                {
                    NativeEventDiagnosticOracle.RunOrdinary(capacity);
                    NativeEventDiagnosticOracle.RunPermission(capacity);
                    NativeEventDiagnosticOracle.RunCancelled(capacity, false);
                    NativeEventDiagnosticOracle.RunCancelled(capacity, true);
                    NativeEventDiagnosticOracle.RunLayout(capacity);
                    NativeEventDiagnosticOracle.RunSharing(capacity);
                    NativeEventDiagnosticOracle.RunPreparedPages(capacity);
                }
                System.Console.WriteLine("event-oracle normal-families=7 event-fields=12 tracing-modes=2 histories=complete cleanup=complete");
                """);
            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"", consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);
            CommandResult build = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo", consumerRoot);
            Assert.True(build.ExitCode == 0, build.Output);
            CommandResult run = await RunDotnetAsync($"run --project \"{project}\" --no-build --no-restore", consumerRoot);
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Contains("event-oracle normal-families=7 event-fields=12 tracing-modes=2 histories=complete cleanup=complete", run.Output, StringComparison.Ordinal);
        }
        finally { DeleteConsumerRoot(consumerRoot); }
    }

    [Fact]
    public async Task PackageLayoutDiagnosticOracleMatchesEveryField()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: false, suppressDiagnostics: false,
                executable: true, treatWarningsAsErrors: true);
            foreach (string name in new[] { "layout", "admission" })
                File.Copy(Path.Combine(FindRepositoryRoot(), "conformance", "native-" + name + "-diagnostic-oracle.cs"),
                    Path.Combine(consumerRoot, "Native" + char.ToUpperInvariant(name[0]) + name[1..] + "DiagnosticOracle.cs"));
            await File.WriteAllTextAsync(Path.Combine(consumerRoot, "Program.cs"),
                """
                using Supprocom.NativeAllocationManagement.Conformance;

                foreach (int capacity in new[] { 0, 16 })
                {
                    NativeLayoutDiagnosticOracle.Run(capacity);
                    NativeLayoutDiagnosticOracle.RunEmpty(capacity, false);
                    NativeLayoutDiagnosticOracle.RunEmpty(capacity, true);
                }
                System.Console.WriteLine("layout-oracle schemas=2 fields=24 nested-fields=38 tracing-modes=2 paths=prepared,moved,detached,empty cleanup=complete");
                """);
            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"", consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);
            CommandResult build = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo", consumerRoot);
            Assert.True(build.ExitCode == 0, build.Output);
            CommandResult run = await RunDotnetAsync($"run --project \"{project}\" --no-build --no-restore", consumerRoot);
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Contains("layout-oracle schemas=2 fields=24 nested-fields=38 tracing-modes=2 paths=prepared,moved,detached,empty cleanup=complete", run.Output, StringComparison.Ordinal);
        }
        finally { DeleteConsumerRoot(consumerRoot); }
    }

    [Fact]
    public async Task PackageOwnerDiagnosticOracleMatchesEveryField()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: false, suppressDiagnostics: false,
                executable: true, treatWarningsAsErrors: true);
            foreach (string name in new[] { "owner", "capacity", "admission" })
                File.Copy(Path.Combine(FindRepositoryRoot(), "conformance", "native-" + name + "-diagnostic-oracle.cs"),
                    Path.Combine(consumerRoot, "Native" + char.ToUpperInvariant(name[0]) + name[1..] + "DiagnosticOracle.cs"));
            await File.WriteAllTextAsync(Path.Combine(consumerRoot, "Program.cs"),
                """
                using Supprocom.NativeAllocationManagement.Conformance;

                foreach (int capacity in new[] { 0, 16 })
                {
                    NativeOwnerDiagnosticOracle.RunBuilder(capacity);
                    NativeOwnerDiagnosticOracle.RunWorkspace(capacity);
                    NativeOwnerDiagnosticOracle.RunPool(capacity);
                    NativeOwnerDiagnosticOracle.RunArena(capacity);
                    NativeOwnerDiagnosticOracle.RunRegion(capacity);
                    NativeOwnerDiagnosticOracle.RunSynchronizedPool(capacity);
                }
                System.Console.WriteLine("owner-oracle schemas=2 fields=47 tracing-modes=2 paths=builder,workspace,pool,arena,region,roots cleanup=complete");
                """);
            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"", consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);
            CommandResult build = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo", consumerRoot);
            Assert.True(build.ExitCode == 0, build.Output);
            CommandResult run = await RunDotnetAsync($"run --project \"{project}\" --no-build --no-restore", consumerRoot);
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Contains("owner-oracle schemas=2 fields=47 tracing-modes=2 paths=builder,workspace,pool,arena,region,roots cleanup=complete", run.Output, StringComparison.Ordinal);
        }
        finally { DeleteConsumerRoot(consumerRoot); }
    }

    [Fact]
    public async Task PackageTraceDiagnosticOracleMatchesEveryField()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: false, suppressDiagnostics: false,
                executable: true, treatWarningsAsErrors: true);
            File.Copy(Path.Combine(FindRepositoryRoot(), "conformance", "native-trace-diagnostic-oracle.cs"),
                Path.Combine(consumerRoot, "NativeTraceDiagnosticOracle.cs"));
            await File.WriteAllTextAsync(Path.Combine(consumerRoot, "Program.cs"),
                """
                using Supprocom.NativeAllocationManagement.Conformance;

                foreach (int capacity in new[] { 0, 2, 16 })
                {
                    NativeTraceDiagnosticOracle.RunDirect(capacity);
                    NativeTraceDiagnosticOracle.RunReservation(capacity);
                }
                NativeTraceDiagnosticOracle.RunOwnerlessRefusal();
                NativeTraceDiagnosticOracle.RunGenerationZero();
                System.Console.WriteLine("trace-oracle schemas=1 fields=12 tracing-modes=3 paths=growth,reservation,ownerless,generation-zero cleanup=complete");
                """);
            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"", consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);
            CommandResult build = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo", consumerRoot);
            Assert.True(build.ExitCode == 0, build.Output);
            CommandResult run = await RunDotnetAsync($"run --project \"{project}\" --no-build --no-restore", consumerRoot);
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Contains("trace-oracle schemas=1 fields=12 tracing-modes=3 paths=growth,reservation,ownerless,generation-zero cleanup=complete", run.Output, StringComparison.Ordinal);
        }
        finally { DeleteConsumerRoot(consumerRoot); }
    }

    [Fact]
    public async Task PackageCapacityDiagnosticOracleMatchesEveryField()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: false, suppressDiagnostics: false,
                executable: true, treatWarningsAsErrors: true);
            File.Copy(Path.Combine(FindRepositoryRoot(), "conformance", "native-capacity-diagnostic-oracle.cs"),
                Path.Combine(consumerRoot, "NativeCapacityDiagnosticOracle.cs"));
            File.Copy(Path.Combine(FindRepositoryRoot(), "conformance", "native-admission-diagnostic-oracle.cs"),
                Path.Combine(consumerRoot, "NativeAdmissionDiagnosticOracle.cs"));
            await File.WriteAllTextAsync(Path.Combine(consumerRoot, "Program.cs"),
                """
                using Supprocom.NativeAllocationManagement.Conformance;

                NativeCapacityDiagnosticOracle.RunPool(traceCapacity: 0);
                NativeCapacityDiagnosticOracle.RunPool(traceCapacity: 16);
                NativeCapacityDiagnosticOracle.RunArena(traceCapacity: 0);
                NativeCapacityDiagnosticOracle.RunArena(traceCapacity: 16);
                NativeCapacityDiagnosticOracle.RunRetention(traceCapacity: 0);
                NativeCapacityDiagnosticOracle.RunRetention(traceCapacity: 16);
                System.Console.WriteLine("capacity-oracle schemas=3 fields=45 tracing-modes=2 paths=pages,lanes,retention cleanup=complete");
                """);
            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"", consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);
            CommandResult build = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo", consumerRoot);
            Assert.True(build.ExitCode == 0, build.Output);
            CommandResult run = await RunDotnetAsync($"run --project \"{project}\" --no-build --no-restore", consumerRoot);
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Contains("capacity-oracle schemas=3 fields=45 tracing-modes=2 paths=pages,lanes,retention cleanup=complete", run.Output, StringComparison.Ordinal);
        }
        finally { DeleteConsumerRoot(consumerRoot); }
    }

    [Fact]
    public async Task PackageAdmissionDiagnosticOracleMatchesEveryField()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: false, suppressDiagnostics: false,
                executable: true, treatWarningsAsErrors: true);
            File.Copy(Path.Combine(FindRepositoryRoot(), "conformance", "native-admission-diagnostic-oracle.cs"),
                Path.Combine(consumerRoot, "NativeAdmissionDiagnosticOracle.cs"));
            await File.WriteAllTextAsync(Path.Combine(consumerRoot, "Program.cs"),
                """
                using Supprocom.NativeAllocationManagement.Conformance;

                NativeAdmissionDiagnosticOracle.Run(traceCapacity: 0);
                NativeAdmissionDiagnosticOracle.Run(traceCapacity: 16);
                NativeAdmissionDiagnosticOracle.RunOrdinaryUnique(traceCapacity: 0);
                NativeAdmissionDiagnosticOracle.RunOrdinaryUnique(traceCapacity: 16);
                System.Console.WriteLine("admission-oracle schemas=3 fields=60 modes=2 paths=reservation,activation,unique cleanup=complete");
                """);
            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"", consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);
            CommandResult build = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo", consumerRoot);
            Assert.True(build.ExitCode == 0, build.Output);
            CommandResult run = await RunDotnetAsync($"run --project \"{project}\" --no-build --no-restore", consumerRoot);
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Contains("admission-oracle schemas=3 fields=60 modes=2 paths=reservation,activation,unique cleanup=complete", run.Output, StringComparison.Ordinal);
        }
        finally { DeleteConsumerRoot(consumerRoot); }
    }

    [Fact]
    public async Task PackageSharingDiagnosticOracleMatchesEveryField()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: false, suppressDiagnostics: false,
                executable: true, treatWarningsAsErrors: true);
            File.Copy(Path.Combine(FindRepositoryRoot(), "conformance", "native-sharing-diagnostic-oracle.cs"),
                Path.Combine(consumerRoot, "NativeSharingDiagnosticOracle.cs"));
            await File.WriteAllTextAsync(Path.Combine(consumerRoot, "Program.cs"),
                """
                using Supprocom.NativeAllocationManagement.Conformance;

                NativeSharingDiagnosticOracle.Run(traceCapacity: 0);
                NativeSharingDiagnosticOracle.Run(traceCapacity: 16);
                System.Console.WriteLine("sharing-oracle schemas=1 fields=26 modes=2 paths=share,weak,slice,detach cleanup=complete");
                """);
            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"", consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);
            CommandResult build = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo", consumerRoot);
            Assert.True(build.ExitCode == 0, build.Output);
            CommandResult run = await RunDotnetAsync($"run --project \"{project}\" --no-build --no-restore", consumerRoot);
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Contains("sharing-oracle schemas=1 fields=26 modes=2 paths=share,weak,slice,detach cleanup=complete", run.Output, StringComparison.Ordinal);
        }
        finally { DeleteConsumerRoot(consumerRoot); }
    }

    [Fact]
    public async Task PackageProcessDiagnosticOracleMatchesEveryField()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: false, suppressDiagnostics: false,
                executable: true, treatWarningsAsErrors: true);
            File.Copy(Path.Combine(FindRepositoryRoot(), "conformance", "native-process-diagnostic-oracle.cs"),
                Path.Combine(consumerRoot, "NativeProcessDiagnosticOracle.cs"));
            await File.WriteAllTextAsync(Path.Combine(consumerRoot, "Program.cs"),
                """
                using Supprocom.NativeAllocationManagement.Conformance;

                NativeProcessDiagnosticOracle.Run();
                System.Console.WriteLine("process-oracle schemas=1 fields=20 paths=workspace,builder,transfer cleanup=complete");
                """);
            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"", consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);
            CommandResult build = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo", consumerRoot);
            Assert.True(build.ExitCode == 0, build.Output);
            CommandResult run = await RunDotnetAsync($"run --project \"{project}\" --no-build --no-restore", consumerRoot);
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Contains("process-oracle schemas=1 fields=20 paths=workspace,builder,transfer cleanup=complete", run.Output, StringComparison.Ordinal);
        }
        finally { DeleteConsumerRoot(consumerRoot); }
    }

    [Fact]
    public async Task PackageBudgetDiagnosticOracleMatchesEveryField()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: false, suppressDiagnostics: false,
                executable: true, treatWarningsAsErrors: true);
            File.Copy(Path.Combine(FindRepositoryRoot(), "conformance", "native-budget-diagnostic-oracle.cs"),
                Path.Combine(consumerRoot, "NativeBudgetDiagnosticOracle.cs"));
            await File.WriteAllTextAsync(Path.Combine(consumerRoot, "Program.cs"),
                """
                using Supprocom.NativeAllocationManagement.Conformance;

                NativeBudgetDiagnosticOracle.Run(traceCapacity: 0);
                NativeBudgetDiagnosticOracle.Run(traceCapacity: 4);
                System.Console.WriteLine("budget-oracle schemas=1 fields=19 modes=2 paths=builder,pending,prepared");
                """);
            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"", consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);
            CommandResult build = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo", consumerRoot);
            Assert.True(build.ExitCode == 0, build.Output);
            CommandResult run = await RunDotnetAsync($"run --project \"{project}\" --no-build --no-restore", consumerRoot);
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Contains("budget-oracle schemas=1 fields=19 modes=2 paths=builder,pending,prepared", run.Output, StringComparison.Ordinal);
        }
        finally { DeleteConsumerRoot(consumerRoot); }
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
                $"run --project \"{project}\" --no-build --no-restore",
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
                            int result = workspace.Process(8, 17, static (values, value) =>
                            {
                                values.Fill(value);
                                return values[7];
                            });
                            workspace.Dispose();
                            NativeOwnerDiagnosticSnapshot workspaceSnapshot = workspace.CaptureDiagnosticSnapshot();
                            if (result != 17
                                || workspaceSnapshot.Model != NativeOwnerModel.ThreadConfinedWorkspace
                                || workspaceSnapshot.Lifecycle != NativeOwnerLifecycle.Disposed
                                || workspaceSnapshot.OutstandingNativeBytes != 0
                                || workspaceSnapshot.PeakOutstandingNativeBytes != 32
                                || workspace.GetStatistics().PeakInitializedPayloadBytes != 32)
                            {
                                return 6;
                            }
                        }

                        if (!builder.TryEnsureCapacity(5))
                        {
                            return 2;
                        }

                        NativeTransfer<int> transfer = builder.Complete();
                        try
                        {
                            NativeOwnerDiagnosticSnapshot builderSnapshot = builder.CaptureDiagnosticSnapshot();
                            if (transfer.Id != builder.Id
                                || transfer.Read(static view => view[0]) != 42
                                || budget.CaptureStatistics().CommittedBytes != 32
                                || builderSnapshot.Model != NativeOwnerModel.SingleWriterBuilder
                                || builderSnapshot.Lifecycle != NativeOwnerLifecycle.Returned
                                || builderSnapshot.OutstandingNativeBytes != 0
                                || builderSnapshot.PeakOutstandingNativeBytes != 32
                                || builder.GetStatistics().PeakInitializedPayloadBytes != 4)
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
                        int copied = budget.CopyTraceTo(events);
                        System.Console.WriteLine($"traceCount={snapshot.TraceCount};dropped={snapshot.DroppedTraceEventCount};copied={copied};committed={snapshot.CommittedBytes}");
                        foreach (NativeMemoryTraceEvent item in events)
                            System.Console.WriteLine($"sequence={item.Sequence};kind={item.Kind};owner={item.OwnerId};correlation={item.CorrelationId};extent={item.RequestedBytes};committed={item.CommittedBytes}");
                        if (snapshot.CommittedBytes != 0
                            || snapshot.TraceCapacity != 8
                            || snapshot.TraceCount != 8
                            || snapshot.DroppedTraceEventCount != 2
                            || snapshot.TraceOverflowed
                            || copied != 4
                            || events[2].Kind != NativeMemoryTraceKind.Released
                            || events[2].OwnerId != builder.Id
                            || events[3].Kind != NativeMemoryTraceKind.UniqueReturned
                            || events[3].OwnerId != builder.Id
                            || events[3].CorrelationId != transfer.Id
                            || events[3].RequestedBytes != 32
                            || events[3].CommittedBytes != 0)
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
            CommandResult run = await RunDotnetAsync($"run --project \"{project}\" --no-build --no-restore", consumerRoot);
            _output.WriteLine(run.Output);
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
                            if (builder.GetStatistics().OutstandingNativeBytes != 0
                                || builder.CaptureDiagnosticSnapshot().PeakInitializedPayloadBytes != 16)
                            {
                                return 9;
                            }
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
                $"run --project \"{project}\" --no-build --no-restore",
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
                                || snapshot.RetainedBytes != 32 || snapshot.ManagedBankBytes <= 0) return 3;
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
                        NativeMemoryBudget admissionBudget = new(8, 16);
                        if (!admissionBudget.TryReserve<int>(2, out NativeMemoryReservation<int>? permission, out NativeMemoryAdmissionExhaustionReason admissionReason)) return 35;
                        try
                        {
                            if (admissionReason != NativeMemoryAdmissionExhaustionReason.None) return 35;
                            permission.Value.PrepareBacking();
                            using NativeTransfer<int> admitted = NativeMemoryReservation<int>.Activate(ref permission, static writer => writer.Fill(43));
                            if (admitted.Read(static view => view[1]) != 43 || admitted.CaptureSnapshot().MoveCount != 0
                                || admissionBudget.CaptureAdmissionStatistics().ActivationCount != 1
                                || admissionBudget.CaptureAdmissionStatistics().OutstandingReservationCount != 0) return 36;
                        }
                        finally { permission?.Dispose(); }
                        if (admissionBudget.CaptureStatistics().CommittedBytes != 0
                            || admissionBudget.CaptureStatistics().ReservedBytes != 0
                            || admissionBudget.CaptureStatistics().AllocationCount != 1
                            || admissionBudget.CaptureStatistics().FreeCount != 1) return 37;
                        System.Console.WriteLine("admitted=1; prepared=1; activated=1; returned=1; final-charge=0");
                        NativeLayoutBuilder layoutBuilder = new(2);
                        NativeLayoutField<byte> layoutBytes = layoutBuilder.Add<byte>(3);
                        NativeLayoutField<int> layoutIntegers = layoutBuilder.Add<int>(2, 16);
                        NativeLayout layout = layoutBuilder.Build();
                        NativeMemoryBudget layoutBudget = new(layout.BackingBytes + 8, 32);
                        if (!layout.TryReserve(layoutBudget, out NativeLayoutReservation? layoutPermission, out _)) return 38;
                        try
                        {
                            layoutPermission.Value.PrepareBacking();
                            using NativeLayoutOwner layoutOwner = NativeLayoutReservation.Activate(ref layoutPermission,
                                (layoutBytes, layoutIntegers), static (writer, fields) =>
                                { writer.Region(fields.layoutBytes).Fill(7); writer.Region(fields.layoutIntegers).Fill(47); });
                            if (layoutOwner.Read((layoutBytes, layoutIntegers), static (view, fields) =>
                                view.Region(fields.layoutBytes)[0] + view.Region(fields.layoutIntegers)[0] + view.Region(fields.layoutIntegers)[1]) != 101) return 39;
                            using NativeTransfer<int> fieldCopy = layoutOwner.DetachField(layoutIntegers, layoutBudget);
                            NativeLayoutStatistics layoutObserved = layoutOwner.CaptureSnapshot();
                            if (fieldCopy.Read(static view => view[1]) != 47 || layoutObserved.RegionCount != 2
                                || layoutObserved.InitializedRegionCount != 2 || layoutObserved.LogicalInitializedBytes != 11
                                || layoutObserved.CopiedBytes != 8 || layoutObserved.DetachedOwnerCount != 1
                                || !layoutObserved.LayoutIsPrepared || !layoutObserved.InitializationCompleted
                                || layoutObserved.Ownership.OwnedBackingBytes != layout.BackingBytes
                                || layout.Describe(layoutIntegers).OffsetBytes != 16) return 40;
                        }
                        finally { layoutPermission?.Dispose(); }
                        if (layoutBudget.CaptureStatistics().CommittedBytes != 0 || layoutBudget.CaptureStatistics().ReservedBytes != 0
                            || layoutBudget.CaptureStatistics().AllocationCount != 2 || layoutBudget.CaptureStatistics().FreeCount != 2) return 41;
                        System.Console.WriteLine("typed-layout-output=101; fields=2; copied-bytes=8; final-charge=0");
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
            CommandResult run = await RunDotnetAsync($"run --project \"{project}\" --no-build --no-restore", consumerRoot);
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
                        NativeMemoryBudget budget = new(4);
                        budget.TryReserve<int>(1, out NativeMemoryReservation<int>? permission, out _);
                        permission!.Value.PrepareBacking();
                    }
                }
                """);
            CommandResult invalidAdmission = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo -t:Rebuild", consumerRoot);
            _output.WriteLine(invalidAdmission.Output);
            Assert.NotEqual(0, invalidAdmission.ExitCode);
            Assert.Contains("error NAM1050", invalidAdmission.Output, StringComparison.OrdinalIgnoreCase);
            await File.WriteAllTextAsync(program,
                """
                using Supprocom.NativeAllocationManagement;
                public static class Consumer
                {
                    public static void Main()
                    {
                        NativeLayoutBuilder builder = new(1);
                        builder.Add<int>(1);
                        NativeLayout layout = builder.Build();
                        layout.TryReserve(new(layout.BackingBytes), out NativeLayoutReservation? permission, out _);
                        permission!.Value.PrepareBacking();
                    }
                }
                """);
            CommandResult invalidLayout = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo -t:Rebuild", consumerRoot);
            _output.WriteLine(invalidLayout.Output);
            Assert.NotEqual(0, invalidLayout.ExitCode);
            Assert.Contains("error NAM1050", invalidLayout.Output, StringComparison.OrdinalIgnoreCase);
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

            CommandResult run = await RunDotnetAsync($"run --project \"{project}\" --no-build --no-restore", consumerRoot);
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

            CommandResult run = await RunDotnetAsync($"run --project \"{project}\" --no-build --no-restore", consumerRoot);
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

            CommandResult run = await RunDotnetAsync($"run --project \"{project}\" --no-build --no-restore", consumerRoot);
            Assert.True(run.ExitCode == 0, run.Output);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task PackageCodeFixIsDiscoveredAndAppliedByAnIsolatedMefWorkspaceHost()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            await File.WriteAllTextAsync(project, CodeFixPackageFixture.Project(package.Version));
            await File.WriteAllTextAsync(Path.Combine(consumerRoot, "Program.cs"), CodeFixPackageFixture.Program);
            string configuration = Path.Combine(consumerRoot, "NuGet.config");
            await File.WriteAllTextAsync(configuration, PackageFixtureEvidence.RestoreConfiguration(package.SourceDirectory));
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --configfile \"{configuration}\"",
                consumerRoot);
            _output.WriteLine(restore.Output);
            Assert.True(restore.ExitCode == 0, restore.Output);
            CommandResult build = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo", consumerRoot);
            _output.WriteLine(build.Output);
            Assert.True(build.ExitCode == 0, build.Output);
            CommandResult run = await RunDotnetAsync($"run --project \"{project}\" --no-build --no-restore -- {package.RuntimeAssemblySha256} {package.AnalyzerAssemblySha256} {package.CodeFixAssemblySha256}", consumerRoot);
            _output.WriteLine(run.Output);
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Contains("packageMefExports=1;codeActions=1;correctedCompilerErrors=0;correctedNamDiagnostics=0", run.Output, StringComparison.Ordinal);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Theory]
    [InlineData("NativeTransfer<int>")]
    [InlineData("NativeMemoryReservation<int>")]
    [InlineData("NativeLayoutOwner")]
    [InlineData("NativeLayoutReservation")]
    [InlineData("NativeShared<int>")]
    [InlineData("NativeWeak<int>")]
    public async Task PackageAnalyzerEnforcesGeneratedParameterAndControlIdentityPrograms(string type)
    {
        ArgumentNullException.ThrowIfNull(type);
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string root = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(root, package, excludeAnalyzer: false, suppressDiagnostics: false, treatWarningsAsErrors: true);
            string program = Path.Combine(root, "Program.cs"), project = Path.Combine(root, "Consumer.csproj");
            await File.WriteAllTextAsync(program, $$"""
                using Supprocom.NativeAllocationManagement;
                public static class Consumer
                {
                    public static void Drop({{type}} owner) { }
                    public static void InvalidIdentity() { {{type}} owner = default; _ = owner.Id; }
                    public static void InvalidOut(out {{type}} owner) { owner = default; }
                }
                """);
            CommandResult restore = await RunDotnetAsync($"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(root, ".packages")}\" --source \"{package.SourceDirectory}\"", root);
            Assert.True(restore.ExitCode == 0, restore.Output);
            CommandResult invalid = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo", root);
            _output.WriteLine(invalid.Output);
            Assert.NotEqual(0, invalid.ExitCode);
            Assert.Contains("NAM1025", invalid.Output, StringComparison.Ordinal);
            Assert.Contains("NAM1022", invalid.Output, StringComparison.Ordinal);
            Assert.Contains("NAM1027", invalid.Output, StringComparison.Ordinal);
            await File.WriteAllTextAsync(program, $$"""
                using Supprocom.NativeAllocationManagement;
                public static class Consumer
                {
                    public static void End({{type}} owner) { owner.Dispose(); _ = owner.Id; }
                    public static void Callback()
                    { System.Action<{{type}}> end = owner => { owner.Dispose(); _ = owner.Id; }; }
                }
                """);
            CommandResult valid = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo", root);
            Assert.True(valid.ExitCode == 0, valid.Output);
        }
        finally { DeleteConsumerRoot(root); }
    }

    [Fact]
    public async Task PackageGeneratedRuntimeModelsRemainSafeWithOwnershipWarningsSuppressed()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string root = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(root, package, excludeAnalyzer: false, suppressDiagnostics: false, executable: true);
            await WriteGeneratedRuntimeSourcesAsync(root);
            string project = Path.Combine(root, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync($"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(root, ".packages")}\" --source \"{package.SourceDirectory}\"", root);
            Assert.True(restore.ExitCode == 0, restore.Output);
            CommandResult build = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo", root);
            Assert.True(build.ExitCode == 0, build.Output);
            CommandResult run = await RunDotnetAsync($"run --project \"{project}\" --no-build --no-restore", root);
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Contains("generatedRuntimeModels=passed;seeds=3;projectReferences=0", run.Output, StringComparison.Ordinal);
            Assert.Contains("generatedBoundaryModels=passed;seeds=4;families=3;tracing-modes=2", run.Output, StringComparison.Ordinal);
        }
        finally { DeleteConsumerRoot(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PackageGeneratedRuntimeModelsRunAsActualTrimmedAndNativeBinaries(bool nativeAot)
    {
        const int ColdPublishDeadlineSeconds = 300;
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string root = CreateConsumerRoot();
        try
        {
            string rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
            _output.WriteLine($"deploymentRid={rid};nativeAot={nativeAot};coldPublishDeadlineSeconds={ColdPublishDeadlineSeconds}");
            WriteConsumerProject(root, package, excludeAnalyzer: false, suppressDiagnostics: false,
                executable: true, treatWarningsAsErrors: true);
            string project = Path.Combine(root, "Consumer.csproj");
            XDocument deployment = XDocument.Load(project);
            XElement properties = deployment.Root!.Element("PropertyGroup")!;
            properties.Add(new XElement("RuntimeIdentifier", rid), new XElement("SelfContained", "true"),
                new XElement("PublishTrimmed", "true"), new XElement("TrimMode", "full"),
                new XElement("PublishAot", nativeAot ? "true" : "false"),
                new XElement("TrimmerSingleWarn", "false"), new XElement("ILLinkTreatWarningsAsErrors", "true"));
            deployment.Root.Add(new XElement("ItemGroup",
                new XElement("TrimmerRootAssembly", new XAttribute("Include", "Supprocom.NativeAllocationManagement"))));
            deployment.Save(project);
            await WriteGeneratedRuntimeSourcesAsync(root);
            string configuration = Path.Combine(root, "NuGet.config");
            await File.WriteAllTextAsync(configuration, PackageFixtureEvidence.RestoreConfiguration(package.SourceDirectory));
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(root, ".packages")}\" --configfile \"{configuration}\"", root);
            Assert.True(restore.ExitCode == 0, restore.Output);
            string published = Path.Combine(root, "published");
            CommandResult publish = await RunDotnetAsync(
                $"publish \"{project}\" -c Release --no-restore --nologo --self-contained true -o \"{published}\"", root,
                ColdPublishDeadlineSeconds);
            Assert.True(publish.ExitCode == 0, publish.Output);
            string executable = Path.Combine(published, OperatingSystem.IsWindows() ? "Consumer.exe" : "Consumer");
            Assert.True(File.Exists(executable), publish.Output);
            CommandResult execution = await RunProcessAsync(executable, string.Empty, root);
            Assert.True(execution.ExitCode == 0, execution.Output);
            Assert.Contains("generatedRuntimeModels=passed;seeds=3;projectReferences=0", execution.Output, StringComparison.Ordinal);
            Assert.Contains("generatedBoundaryModels=passed;seeds=4;families=3;tracing-modes=2", execution.Output, StringComparison.Ordinal);
            Assert.Contains($"dynamicCodeSupported={!nativeAot}", execution.Output, StringComparison.Ordinal);
            Assert.Contains($"hostRid={rid}", execution.Output, StringComparison.Ordinal);
            _output.WriteLine($"publishedExecutable={executable}");
            using FileStream executableBytes = File.OpenRead(executable);
            byte[] executableHash = await SHA256.HashDataAsync(executableBytes);
            _output.WriteLine($"publishedExecutableSha256={Convert.ToHexString(executableHash)}");
            if (RetainPackageEvidence && PackageFixtureEvidence.RetainArchive(root, "executed-package-consumer") is { } consumerArchive)
                _output.WriteLine($"durableConsumerArchive={consumerArchive}");
        }
        finally { DeleteConsumerRoot(root); }
    }

    private static async Task WriteGeneratedRuntimeSourcesAsync(string root)
    {
        string project = Path.Combine(root, "Consumer.csproj");
        XDocument generatedProject = XDocument.Load(project);
        // This fixture's actual SafeBuffer provider is the explicit unsafe
        // registration boundary, not an escaping unsafe ordinary consumer.
        generatedProject.Root!.Element("PropertyGroup")!.Add(new XElement("AllowUnsafeBlocks", "true"));
        generatedProject.Save(project);
        string ownershipDiagnostics = string.Join(", ", new Analyzers.NativeAllocationAnalyzer().SupportedDiagnostics
            .Select(static descriptor => descriptor.Id).Order(StringComparer.Ordinal));
        foreach (string source in new[] { "NativeGeneratedScenarios.cs", "NativeGeneratedScenarios.Concurrent.cs",
            "NativeGeneratedScenarios.StorageBoundaries.cs", "NativeGeneratedScenarios.MappedBoundaries.cs" })
        {
            string text = await File.ReadAllTextAsync(Path.Combine(RepositoryTestPaths.Root, "Supprocom.NativeAllocationManagement.Tests", source)).ConfigureAwait(false);
            // Adversarial stored aliases/forwarding test runtime guards with the
            // actual bundled analyzer loaded. No deployment warning is suppressed.
            await File.WriteAllTextAsync(Path.Combine(root, source),
                $"#pragma warning disable {ownershipDiagnostics}\n" + text).ConfigureAwait(false);
        }
        await File.WriteAllTextAsync(Path.Combine(root, "Program.cs"), """
            using Supprocom.NativeAllocationManagement.Tests;
            public static class Consumer
            {
                public static async System.Threading.Tasks.Task<int> Main()
                {
                    System.Console.WriteLine($"hostRid={System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}");
                    System.Console.WriteLine($"dynamicCodeSupported={System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported}");
                    foreach (int seed in new[] { 17, 101, 379 })
                    {
                        System.Console.WriteLine($"seed={seed}");
                        NativeGeneratedScenarios.RunAdmission(seed, 2048, System.Console.WriteLine);
                        NativeGeneratedScenarios.RunSharing(seed, 512, System.Console.WriteLine);
                        NativeGeneratedScenarios.RunPreparedPool(seed, 512, System.Console.WriteLine);
                        NativeGeneratedScenarios.RunPreparedArena(seed, 512, System.Console.WriteLine);
                        NativeGeneratedScenarios.RunLayouts(seed, 64, System.Console.WriteLine);
                        NativeGeneratedScenarios.RunOutliers(seed, 128, System.Console.WriteLine);
                        NativeGeneratedScenarios.RunDirectOwners(seed, 128, System.Console.WriteLine);
                        await NativeGeneratedScenarios.RunSharingSchedulesAsync(seed, 32, System.Console.WriteLine);
                        await NativeGeneratedScenarios.RunBorrowReturnSchedulesAsync(seed, 16, System.Console.WriteLine);
                    }
                    System.Console.WriteLine("generatedRuntimeModels=passed;seeds=3;projectReferences=0");
                    foreach (int seed in new[] { 23, 149, 541, 887 })
                    {
                        foreach (int traceCapacity in new[] { 0, 64 })
                        {
                            NativeGeneratedScenarios.RunSparsePageReuse(seed, 32, traceCapacity, System.Console.WriteLine);
                            NativeGeneratedScenarios.RunTighterGrowthAtTheCap(seed, 32, traceCapacity, System.Console.WriteLine);
                            NativeGeneratedScenarios.RunMappedCompositeFailures(seed, 16, traceCapacity, System.Console.WriteLine);
                        }
                    }
                    System.Console.WriteLine("generatedBoundaryModels=passed;seeds=4;families=3;tracing-modes=2");
                    return 0;
                }
            }
            """).ConfigureAwait(false);
    }

    private void WriteEvidence(PackageEvidence package)
    {
        _output.WriteLine($"package={package.Path}");
        _output.WriteLine($"version={package.Version}");
        _output.WriteLine($"commit={package.RepositoryCommit}");
        _output.WriteLine($"artifactSha256={package.ArtifactSha256}");
        _output.WriteLine($"runtimeSha256={package.RuntimeAssemblySha256}");
        _output.WriteLine($"analyzerSha256={package.AnalyzerAssemblySha256}");
        _output.WriteLine($"codeFixSha256={package.CodeFixAssemblySha256}");
        _output.WriteLine($"symbols={package.SymbolPath}");
        _output.WriteLine($"symbolsSha256={package.SymbolArtifactSha256}");
        _output.WriteLine($"symbolVerification={Path.Combine(package.SourceDirectory, "symbols-verification.json")}");
        if (RetainPackageEvidence && PackageFixtureEvidence.RetainArchive(package.SourceDirectory, "verified-package-pair") is { } packageArchive)
            _output.WriteLine($"durablePackageArchive={packageArchive}");
    }

    private static void WriteConsumerProject(
        string consumerRoot,
        PackageEvidence package,
        bool excludeAnalyzer,
        bool suppressDiagnostics,
        bool executable = false,
        bool treatWarningsAsErrors = false)
    {
        File.WriteAllText(Path.Combine(consumerRoot, "NuGet.config"),
            PackageFixtureEvidence.RestoreConfiguration(package.SourceDirectory));
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
        string codeFixHash = HashEntry(archive, "analyzers/dotnet/cs/Supprocom.NativeAllocationManagement.CodeFixes.dll");
        Assert.DoesNotContain(nuspec.Descendants(), static element => string.Equals(element.Name.LocalName, "dependency", StringComparison.Ordinal));
        string symbolPath = Path.ChangeExtension(packagePath, ".snupkg");
        using FileStream symbolStream = File.OpenRead(symbolPath);
        string symbolHash = Convert.ToHexString(SHA256.HashData(symbolStream));
        symbolStream.Position = 0;
        using ZipArchive symbols = new(symbolStream, ZipArchiveMode.Read, leaveOpen: false);
        using StreamReader symbolNuspecReader = new(symbols.GetEntry("Supprocom.NativeAllocationManagement.nuspec")!.Open());
        XDocument symbolNuspec = XDocument.Parse(symbolNuspecReader.ReadToEnd());
        Assert.Equal("Supprocom.NativeAllocationManagement", symbolNuspec.Descendants()
            .First(static element => string.Equals(element.Name.LocalName, "id", StringComparison.Ordinal)).Value);
        Assert.Equal(version, symbolNuspec.Descendants().First(static element => string.Equals(element.Name.LocalName, "version", StringComparison.Ordinal)).Value);
        Assert.Equal(commit, symbolNuspec.Descendants().First(static element => string.Equals(element.Name.LocalName, "repository", StringComparison.Ordinal)).Attribute("commit")!.Value);
        Assert.Contains(symbolNuspec.Descendants(), static element => string.Equals(element.Name.LocalName, "packageType", StringComparison.Ordinal)
            && string.Equals(element.Attribute("name")?.Value, "SymbolsPackage", StringComparison.Ordinal));
        List<object> symbolEvidence = [];
        foreach (string assemblyPath in new[] { "lib/net10.0/Supprocom.NativeAllocationManagement.dll",
            "analyzers/dotnet/cs/Supprocom.NativeAllocationManagement.Analyzers.dll",
            "analyzers/dotnet/cs/Supprocom.NativeAllocationManagement.CodeFixes.dll" })
        {
            string pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
            byte[] assemblyBytes = ReadEntry(archive, assemblyPath);
            byte[] pdbBytes = ReadEntry(symbols, pdbPath);
            NativePackageSymbolVerifier.VerifyPair(assemblyBytes, pdbBytes);
            NativeSymbolSourceEvidence sourceEvidence = NativePackageSymbolVerifier.VerifySource(pdbBytes, FindRepositoryRoot(), commit);
            string pdbHash = Convert.ToHexString(SHA256.HashData(pdbBytes));
            if (assemblyPath.StartsWith("analyzers/", StringComparison.Ordinal))
                Assert.Equal(pdbHash, HashEntry(archive, pdbPath));
            symbolEvidence.Add(new
            {
                AssemblyPath = assemblyPath,
                PdbPath = pdbPath,
                PdbLength = pdbBytes.Length,
                PdbSHA256 = pdbHash,
                Source = sourceEvidence
            });
        }
        Assert.Equal(3, symbols.Entries.Count(static entry => entry.FullName.EndsWith(".pdb", StringComparison.Ordinal)));
        Assert.DoesNotContain(symbols.Entries, static entry => entry.FullName.EndsWith(".dll", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(sourceDirectory, "symbols-verification.json"),
            System.Text.Json.JsonSerializer.Serialize(symbolEvidence, SymbolJsonOptions));
        return new PackageEvidence(packagePath, sourceDirectory, version, commit, artifactHash, runtimeHash, analyzerHash, codeFixHash,
            symbolPath, symbolHash);
    }

    private static byte[] ReadEntry(ZipArchive archive, string name)
    {
        ZipArchiveEntry entry = archive.GetEntry(name) ?? throw new InvalidDataException($"The package does not contain {name}.");
        using Stream content = entry.Open();
        using MemoryStream result = new();
        content.CopyTo(result);
        return result.ToArray();
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

    internal static async Task<CommandResult> RunDotnetAsync(string arguments, string workingDirectory, int timeoutSeconds = 90)
    {
        return await RunProcessAsync(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet", arguments, workingDirectory, timeoutSeconds).ConfigureAwait(true);
    }

    private static async Task<CommandResult> RunProcessAsync(string fileName, string arguments, string workingDirectory, int timeoutSeconds = 90)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutSeconds);
        string? evidence = PackageFixtureEvidence.Begin(workingDirectory, RetainPackageEvidence);
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

        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        try
        {
            Assert.True(process.Start(), $"The {fileName} process did not start.");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            await PackageFixtureEvidence.CompleteAsync(evidence, fileName, arguments, workingDirectory, startedAt,
                exitCode: null, timedOut: false, string.Empty, exception.ToString(), timeoutSeconds).ConfigureAwait(true);
            throw;
        }
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(timeoutSeconds));
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

            await PackageFixtureEvidence.CompleteAsync(evidence, fileName, arguments, workingDirectory, startedAt,
                process.HasExited ? process.ExitCode : null, timedOut: true,
                await stdout.ConfigureAwait(true), await stderr.ConfigureAwait(true), timeoutSeconds).ConfigureAwait(true);
            throw new TimeoutException($"{fileName} {arguments} exceeded the {timeoutSeconds} second smoke-test timeout.", exception);
        }

        string standardOutput = await stdout.ConfigureAwait(true);
        string standardError = await stderr.ConfigureAwait(true);
        await PackageFixtureEvidence.CompleteAsync(evidence, fileName, arguments, workingDirectory, startedAt,
            process.ExitCode, timedOut: false, standardOutput, standardError, timeoutSeconds).ConfigureAwait(true);
        string output = standardOutput + Environment.NewLine + standardError;
        return new CommandResult(process.ExitCode, output);
    }

    private static bool RetainPackageEvidence => PackageFixtureEvidence.IsEnabled(
        Environment.GetEnvironmentVariable("NAM_RETAIN_PACKAGE_EVIDENCE"));

    private static string FindRepositoryRoot()
        => RepositoryTestPaths.Root;

    private string CreateConsumerRoot()
    {
        string path = Path.Combine(Path.GetTempPath(), "nam-package-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "Directory.Build.props"), PackageFixtureEvidence.BuildProperties);
        _output.WriteLine($"consumerRoot={path}");
        return path;
    }

    private static void DeleteConsumerRoot(string path)
    {
        if (RetainPackageEvidence) return;
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
        string AnalyzerAssemblySha256,
        string CodeFixAssemblySha256,
        string SymbolPath,
        string SymbolArtifactSha256);

    internal sealed class CommandResult
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
