using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement.Tests;

// A concurrent pool finds a renting thread's cache by the stack block of the
// caller's frame before the thread-local tables. A thread whose stack reuses an
// exited thread's memory may take the exited thread's cache; these tests
// simulate that by renting with the exited thread's recorded block. A fault
// injected anywhere in the process turns thread caches off, so this runs alone.
[Collection(ProcessCounterGroup.Name)]
public sealed class NativeConcurrentPoolBlockLookupTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "DisposeFromDeeperFrame disposes the lease from a deeper stack frame.")]
    public void AThreadReusingAnExitedThreadsBlockTakesItsCacheUntilARevocationDropsIt()
    {
        using NativeConcurrentPool<int> pool = new();
        NativeOwnerKernel kernel = pool.KernelForTransfer;
        (NativePoolThreadCache exited, nuint block) = RunExitedOwner(pool);

        NativeAllocation record;
        ConcurrentPooled<int> lease = new(kernel, kernel.RentFilledAtBlockForTest(block, 4, 5));
        record = lease.AllocationStateForComposite;
        Assert.Same(exited, record.CacheOwner);
        Assert.Equal(20, lease.Read(static view => view[0] + view[1] + view[2] + view[3]));

        // Returned from another stack block: the return follows the record's
        // cache, not the block, and puts it back in the exited thread's cache.
        DisposeFromDeeperFrame(lease);
        Assert.Same(exited, record.CacheOwner);
        Assert.Equal(NativeAllocationLifecycle.Returned, record.Lifecycle);

        // The revocation drops the exited thread's cache; its abandoned lease
        // stays live in the generation.
        Assert.Equal(32, pool.GetStatistics().InitializedPayloadBytes);
        Assert.Equal(1, Volatile.Read(ref exited.Revoked));

        // The same block now leads to this thread's own cache.
        using (ConcurrentPooled<int> own = new(kernel, kernel.RentFilledAtBlockForTest(block, 4, 6)))
        {
            NativePoolThreadCache? cache = own.AllocationStateForComposite.CacheOwner;
            Assert.NotNull(cache);
            Assert.NotSame(exited, cache);
            Assert.Same(kernel.ThreadCacheForTest(), cache);
        }

        Assert.Equal(32, pool.GetStatistics().InitializedPayloadBytes);
        pool.ReleaseLeasesToNativeMemory();
        Assert.Equal(0, pool.GetStatistics().InitializedPayloadBytes);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "DisposeFromDeeperFrame disposes the lease from a deeper stack frame.")]
    public void ALeaseFromAReusedCacheHeldAcrossItsRevocationReturnsThroughTheGeneration()
    {
        using NativeConcurrentPool<int> pool = new();
        NativeOwnerKernel kernel = pool.KernelForTransfer;
        (NativePoolThreadCache exited, nuint block) = RunExitedOwner(pool);

        ConcurrentPooled<int> lease = new(kernel, kernel.RentFilledAtBlockForTest(block, 4, 5));
        NativeAllocation record = lease.AllocationStateForComposite;
        Assert.Same(exited, record.CacheOwner);

        // The revocation folds the lease into the generation with the cache.
        Assert.Equal(48, pool.GetStatistics().InitializedPayloadBytes);
        Assert.Null(record.CacheOwner);
        Assert.Equal(20, lease.Read(static view => view[0] + view[1] + view[2] + view[3]));

        // So its return, from another stack block, takes the locked path.
        DisposeFromDeeperFrame(lease);
        Assert.Null(record.CacheOwner);
        Assert.Equal(32, pool.GetStatistics().InitializedPayloadBytes);
        pool.ReleaseLeasesToNativeMemory();
        Assert.Equal(0, pool.GetStatistics().InitializedPayloadBytes);
    }

    [Fact]
    public void ThreadsWhoseBlocksShareATableSlotKeepTheirOwnCaches()
    {
        using NativeConcurrentPool<int> pool = new();
        NativeOwnerKernel kernel = pool.KernelForTransfer;
        // Two made-up blocks with the same table slot.
        nuint first = 0x7000_0123;
        nuint second = first + 64;
        using SemaphoreSlim firstTurn = new(1);
        using SemaphoreSlim secondTurn = new(0);
        int failures = 0;

        Thread Start(nuint block, SemaphoreSlim mine, SemaphoreSlim theirs, int value) => new(() =>
        {
            for (int round = 0; round < 8; round++)
            {
                if (!mine.Wait(Timeout))
                {
                    Interlocked.Increment(ref failures);
                    return;
                }

                try
                {
                    using ConcurrentPooled<int> lease = new(kernel, kernel.RentFilledAtBlockForTest(block, 4, value + round));
                    if (!ReferenceEquals(lease.AllocationStateForComposite.CacheOwner, kernel.ThreadCacheForTest())
                        || lease.Read(static view => view[3]) != value + round)
                    {
                        Interlocked.Increment(ref failures);
                    }
                }
                finally
                {
                    theirs.Release();
                }
            }
        });

        Thread one = Start(first, firstTurn, secondTurn, 100);
        Thread two = Start(second, secondTurn, firstTurn, 200);
        one.Start();
        two.Start();
        Assert.True(one.Join(Timeout));
        Assert.True(two.Join(Timeout));
        Assert.Equal(0, failures);
        Assert.Equal(0, pool.GetStatistics().InitializedPayloadBytes);
    }

    [Fact]
    public void AnOwnerFindsItsOwnCacheAgainAfterARevocation()
    {
        using NativeConcurrentPool<int> pool = new();
        NativeOwnerKernel kernel = pool.KernelForTransfer;
        nuint block = 0x7100_0000;
        for (int index = 0; index < 3; index++)
        {
            new ConcurrentPooled<int>(kernel, kernel.RentFilledAtBlockForTest(block, 4, index)).Dispose();
        }

        NativePoolThreadCache? cache = kernel.ThreadCacheForTest();
        Assert.NotNull(cache);
        Assert.Equal(block, cache!.OwnerBlock);
        Assert.Equal(0, pool.GetStatistics().InitializedPayloadBytes);
        Assert.Equal(1, Volatile.Read(ref cache.Revoked));

        using ConcurrentPooled<int> lease = new(kernel, kernel.RentFilledAtBlockForTest(block, 4, 9));
        Assert.Same(cache, lease.AllocationStateForComposite.CacheOwner);
        Assert.Equal(0, Volatile.Read(ref cache.Revoked));
    }

    // A thread that rents and returns until its cache records its block, then
    // ends holding one lease of eight elements.
    private static (NativePoolThreadCache Cache, nuint Block) RunExitedOwner(NativeConcurrentPool<int> pool)
    {
        NativeOwnerKernel kernel = pool.KernelForTransfer;
        NativePoolThreadCache? cache = null;
        nuint block = 0;
        Thread owner = new(() =>
        {
            for (int index = 0; index < 3; index++)
            {
                pool.Rent(4, index).Dispose();
            }

            cache = kernel.ThreadCacheForTest();
            block = cache?.OwnerBlock ?? 0;
            _ = pool.Rent(8, 3);
        });
        owner.Start();
        Assert.True(owner.Join(Timeout));
        Assert.NotNull(cache);
        Assert.NotEqual((nuint)0, block);
        return (cache!, block);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DisposeFromDeeperFrame(ConcurrentPooled<int> lease)
    {
        Span<byte> pad = stackalloc byte[16 * 1024];
        pad[^1] = 1;
        lease.Dispose();
        Assert.Equal(1, pad[^1]);
    }
}
