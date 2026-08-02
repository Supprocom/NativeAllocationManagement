namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeArenaTransferBatchTests
{
    [Fact]
    public void MoveInvalidatesOldAliasesAndReturnsOneCompactSlot()
    {
        using NativeArena arena = new(
            preAllocateBytes: 128,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeArenaTransferBatch<int> batch =
            arena.CreateTransferBatch<int>(2, 4);
        NativeArenaTransferBatchLease<int> source =
            Initialize(batch, 0, 17);
        NativeArenaTransferBatchLease<int> alias = source;

        NativeArenaTransferBatchLease<int> destination =
            NativeArenaTransferBatchLease<int>.Move(ref source);

        Assert.Throws<ObjectDisposedException>(
            () => source.Read(static view => view[0]));
        Assert.Throws<NativeAllocationReturnedException>(
            () => alias.Read(static view => view[0]));
        Assert.Equal(
            17,
            destination.Read(static view => view[0]));
        Assert.Equal(1, arena.CurrentAllocationRecordCountForTest);
        Assert.Equal(
            0,
            arena.CurrentTransferMetricsForTest.DictionaryRecords);

        alias.Dispose();
        destination.Dispose();

        Assert.Equal(0, arena.CurrentAllocationRecordCountForTest);
        Assert.Equal(2, arena.CurrentTransferMetricsForTest.SlotCreations);
    }

    [Fact]
    public void FailedInitializerReturnsOnlyItsSlot()
    {
        using NativeArena arena = new(
            preAllocateBytes: 128,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeArenaTransferBatch<int> batch =
            arena.CreateTransferBatch<int>(2, 4);

        Assert.Throws<InvalidOperationException>(
            () => InitializeWithFailure(batch, 0));

        NativeArenaTransferBatchLease<int> first =
            Initialize(batch, 0, 23);
        NativeArenaTransferBatchLease<int> second =
            Initialize(batch, 1, 29);

        Assert.Equal(23, first.Read(static view => view[3]));
        Assert.Equal(29, second.Read(static view => view[3]));
        first.Dispose();
        second.Dispose();
        Assert.Equal(0, arena.CurrentAllocationRecordCountForTest);
    }

    [Fact]
    public void PreparedPublicationKeepsTheSlotInitializingUntilRelease()
    {
        NativeArena arena = new(
            preAllocateBytes: 128,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeArenaTransferBatch<int> batch =
            arena.CreateTransferBatch<int>(1, 4);
        NativeArenaTransferBatchInitialization<int> initialization =
            batch.BeginInitialization(0);
        initialization.Values.Fill(73);
        NativeArenaTransferBatchPublication<int> publication =
            initialization.PreparePublication();
        initialization.Dispose();

        Assert.Throws<NativeAllocationInUseException>(arena.Dispose);

        NativeArenaTransferBatchLease<int> lease =
            publication.Publish();
        Assert.Equal(73, lease.Read(static view => view[3]));
        lease.Dispose();
        publication.Dispose();
        arena.Dispose();
    }

    [Fact]
    public void AbortedPreparedPublicationReturnsOnlyItsSlot()
    {
        using NativeArena arena = new(
            preAllocateBytes: 128,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeArenaTransferBatch<int> batch =
            arena.CreateTransferBatch<int>(1, 4);
        NativeArenaTransferBatchInitialization<int> initialization =
            batch.BeginInitialization(0);
        initialization.Values.Fill(79);
        NativeArenaTransferBatchPublication<int> publication =
            initialization.PreparePublication();
        initialization.Dispose();

        publication.Dispose();

        NativeArenaTransferBatchLease<int> lease =
            Initialize(batch, 0, 83);
        Assert.Equal(83, lease.Read(static view => view[3]));
        lease.Dispose();
    }

    [Fact]
    public async Task ReturnRejectsAnActiveCallbackWithoutReusingStorage()
    {
        using NativeArena arena = new(
            preAllocateBytes: 128,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeArenaTransferBatch<int> batch =
            arena.CreateTransferBatch<int>(1, 8);
        NativeArenaTransferBatchLease<int> lease =
            Initialize(batch, 0, 31);
        NativeArenaTransferBatchLease<int> alias = lease;
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();

        Task<int> access = Task.Run(() => lease.Read(
            view =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException(
                        "The batch callback did not receive its release signal.");
                }

                return view[0];
            }));

        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Assert.Throws<InvalidOperationException>(alias.Dispose);
        release.Set();
        Assert.Equal(31, await access);
        Assert.Equal(31, lease.Read(static view => view[0]));

        lease.Dispose();
        alias.Dispose();
        Assert.Equal(0, arena.CurrentAllocationRecordCountForTest);
    }

    [Fact]
    public async Task OwnerDisposalRejectsAnActiveBatchCallback()
    {
        NativeArena arena = new(
            preAllocateBytes: 128,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeArenaTransferBatch<int> batch =
            arena.CreateTransferBatch<int>(1, 8);
        NativeArenaTransferBatchLease<int> lease =
            Initialize(batch, 0, 47);
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();

        Task<int> access = Task.Run(() => lease.Read(
            view =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException(
                        "The batch callback did not receive its release signal.");
                }

                return view[0];
            }));

        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Throws<NativeAllocationInUseException>(arena.Dispose);
            Assert.Equal(NativeOwnerLifecycle.Active, arena.CurrentLifecycle);
            release.Set();
            Assert.Equal(47, await access);
            lease.Dispose();
            arena.Dispose();
            Assert.Equal(
                NativeOwnerLifecycle.Disposed,
                arena.CurrentLifecycle);
        }
        finally
        {
            release.Set();
            if (arena.CurrentLifecycle != NativeOwnerLifecycle.Disposed)
            {
                lease.Dispose();
                arena.Dispose();
            }
        }
    }

    [Fact]
    public void OwnerDisposalRejectsAnActiveBatchInitialization()
    {
        NativeArena arena = new(
            preAllocateBytes: 128,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeArenaTransferBatch<int> batch =
            arena.CreateTransferBatch<int>(1, 8);
        using NativeArenaTransferBatchInitialization<int>
            initialization = batch.BeginInitialization(0);

        Assert.Throws<NativeAllocationInUseException>(arena.Dispose);
        initialization.Values.Fill(61);
        NativeArenaTransferBatchLease<int> lease =
            initialization.Publish();
        Assert.Equal(61, lease.Read(static view => view[7]));
        lease.Dispose();
        arena.Dispose();
    }

    [Fact]
    public async Task OrdinaryScratchUsesItsLaneDuringBatchInitialization()
    {
        using NativeArena arena = new(
            preAllocateBytes: 4_096,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeArenaTransferBatch<int> batch =
            arena.CreateTransferBatch<int>(1, 8);
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();

        Task initialize = Task.Run(() =>
        {
            NativeArenaTransferBatchInitialization<int>
                initialization = batch.BeginInitialization(0);
            try
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException(
                        "The batch initializer did not receive its release signal.");
                }

                initialization.Values.Fill(67);
                NativeArenaTransferBatchLease<int> lease =
                    initialization.Publish();
                lease.Dispose();
            }
            finally
            {
                initialization.Dispose();
            }
        });

        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        ArenaLease<int> local = arena.Scratch<int>(
            8,
            static writer => writer.Fill(71));
        Assert.Equal(71, local.Read(static view => view[7]));
        release.Set();
        await initialize;
        Assert.Equal(0, arena.CurrentAllocationRecordCountForTest);
        Assert.Equal(
            0,
            arena.CurrentTransferMetricsForTest.DictionaryRecords);
        arena.Dispose();
    }

    [Fact]
    public async Task DeferredResetKeepsBatchStorageUntilCallbackExit()
    {
        using NativeArena arena = new(
            preAllocateBytes: 128,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeArenaTransferBatch<int> batch =
            arena.CreateTransferBatch<int>(1, 8);
        NativeArenaTransferBatchLease<int> lease =
            Initialize(batch, 0, 53);
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();

        Task<int> access = Task.Run(() => lease.Read(
            view =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException(
                        "The batch callback did not receive its release signal.");
                }

                return view[7];
            }));

        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        arena.ReleaseLeasesToGarbageCollector();
        release.Set();
        Assert.Equal(53, await access);
        Assert.Throws<NativeAllocationReturnedException>(
            () => lease.Read(static view => view[0]));
        lease.Dispose();

        ArenaLease<int> next = arena.Scratch<int>(
            8,
            static writer => writer.Fill(59));
        Assert.Equal(59, next.Read(static view => view[7]));
    }

    [Fact]
    public void GenerationResetInvalidatesBatchHandlesAndMetadata()
    {
        using NativeArena arena = new(
            preAllocateBytes: 128,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeArenaTransferBatch<int> batch =
            arena.CreateTransferBatch<int>(1, 8);
        NativeArenaTransferBatchLease<int> lease =
            Initialize(batch, 0, 37);

        arena.ReleaseLeasesToNativeMemory();

        Assert.Throws<NativeAllocationReturnedException>(
            () => lease.Read(static view => view[0]));
        Assert.Throws<NativeAllocationReturnedException>(
            () => Initialize(batch, 0, 41));
        lease.Dispose();

        NativeArenaTransferBatch<int> nextBatch =
            arena.CreateTransferBatch<int>(1, 8);
        NativeArenaTransferBatchLease<int> next =
            Initialize(nextBatch, 0, 43);
        Assert.Equal(43, next.Read(static view => view[7]));
        next.Dispose();
    }

    [Fact]
    public async Task FixedSlotsInitializeConcurrentlyWithoutOverlap()
    {
        const int slotCount = 24;
        const int length = 256;
        using NativeArena arena = new(
            preAllocateBytes: slotCount * length * sizeof(long),
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeArenaTransferBatch<long> batch =
            arena.CreateTransferBatch<long>(slotCount, length);
        NativeArenaTransferBatchLease<long>[] leases =
            new NativeArenaTransferBatchLease<long>[slotCount];

        await Task.WhenAll(Enumerable.Range(0, slotCount).Select(
            index => Task.Run(() =>
            {
                leases[index] = Initialize(
                    batch,
                    index,
                    index + 101L);
            })));

        for (int index = 0; index < slotCount; index++)
        {
            Assert.Equal(
                index + 101L,
                leases[index].Read(static view => view[255]));
            leases[index].Dispose();
        }

        Assert.Equal(0, arena.CurrentAllocationRecordCountForTest);
        Assert.Equal(
            0,
            arena.CurrentTransferMetricsForTest.DictionaryRecords);
    }

    [Fact]
    public void FixedBatchCommonPathHasNoGeneralAllocationRecords()
    {
        string repositoryRoot = FindRepositoryRoot();
        string batchSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Supprocom.NativeAllocationManagement",
            "NativeArenaTransferBatch.cs"));
        string kernelSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Supprocom.NativeAllocationManagement",
            "NativeMemoryKernel.cs"));
        string commonPath = GetSourceRange(
            kernelSource,
            "TryBeginArenaTransferBatchInitialization(",
            "LeaseConcurrentBumpInitialized<T>");
        string compactState = GetSourceRange(
            kernelSource,
            "internal sealed class NativeArenaTransferBatchState",
            "internal sealed class NativeGenerationOwner");
        string releasePublication = GetSourceRange(
            compactState,
            "TryPublishInitialization(",
            "        if (IsValid)");

        Assert.DoesNotContain(
            "NativeAllocation?",
            batchSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "NativeAllocation ",
            batchSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "NativeRegionAllocation",
            batchSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Dictionary<",
            batchSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "lock (",
            batchSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "NativeAllocation?",
            commonPath,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "NativeAllocation ",
            commonPath,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ArenaTransferSlots",
            commonPath,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "EnterArenaTransferLane",
            commonPath,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Dictionary<",
            commonPath,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "NativeOperationAdmission",
            commonPath,
            StringComparison.Ordinal);
        Assert.Contains(
            "Interlocked.CompareExchange",
            compactState,
            StringComparison.Ordinal);
        Assert.Contains(
            "Volatile.Write",
            releasePublication,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Interlocked.CompareExchange",
            releasePublication,
            StringComparison.Ordinal);
        Assert.Equal(
            64,
            System.Runtime.InteropServices.Marshal.SizeOf<
                NativeArenaTransferBatchSlot>());
    }

    private static string GetSourceRange(
        string source,
        string startMarker,
        string endMarker)
    {
        int start = source.IndexOf(
            startMarker,
            StringComparison.Ordinal);
        int end = source.IndexOf(
            endMarker,
            start,
            StringComparison.Ordinal);
        Assert.True(start >= 0);
        Assert.True(end > start);
        return source[start..end];
    }

    private static NativeArenaTransferBatchLease<T> Initialize<T>(
        NativeArenaTransferBatch<T> batch,
        int slotIndex,
        T value)
        where T : unmanaged
    {
        using NativeArenaTransferBatchInitialization<T>
            initialization = batch.BeginInitialization(slotIndex);
        initialization.Values.Fill(value);
        return initialization.Publish();
    }

    private static void InitializeWithFailure(
        NativeArenaTransferBatch<int> batch,
        int slotIndex)
    {
        using NativeArenaTransferBatchInitialization<int>
            initialization = batch.BeginInitialization(slotIndex);
        initialization.Values[0] = 1;
        throw new InvalidOperationException(
            "Injected initializer failure.");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(
                directory.FullName,
                "Supprocom.NativeAllocationManagement.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException(
                "The repository root was not found.");
    }
}
