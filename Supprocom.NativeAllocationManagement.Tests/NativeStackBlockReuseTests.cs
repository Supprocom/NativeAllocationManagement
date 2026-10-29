namespace Supprocom.NativeAllocationManagement.Tests;

// Transfers and concurrent arena lanes find a thread's cache by the stack block
// of the caller's frame. These tests run the public paths as a thread whose
// stack reuses an exited thread's memory, by naming the exited thread's block
// through the test-only override. A fault injected anywhere in the process
// turns thread caches off, so this runs alone.
[Collection(ProcessCounterGroup.Name)]
public sealed class NativeStackBlockReuseTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void ATransferDisposedFromItsExitedRentersBlockGoesBackThroughThatCache()
    {
        Assert.True(NativeStackBlockForTest.Enabled);
        using NativeConcurrentPool<int> pool = new();
        NativeOwnerKernel kernel = pool.KernelForTransfer;
        (NativePoolThreadCache exited, nuint block, NativeTransfer<int> held) = RentFromExitedThread(pool);

        int stashed = exited.StashCount;
        using (new OverrideBlock(block))
        {
            held.Dispose();
        }

        Assert.Equal(stashed + 1, exited.StashCount);
        Assert.Equal(0, pool.GetStatistics().InitializedPayloadBytes);
        Assert.Equal(1, Volatile.Read(ref exited.Revoked));

        // The revocation dropped the exited thread's cache: the same block now
        // leads to this thread's own.
        using (new OverrideBlock(block))
        {
            NativeTransfer<int> next = pool.RentTransferable(4, 8);
            NativePoolThreadCache? own = kernel.ThreadCacheForTest();
            Assert.NotNull(own);
            Assert.NotSame(exited, own);
            Assert.Equal(1, own!.TransferCount);
            Assert.Equal(8, next.Read(static view => view[3]));
            next.Dispose();
        }

        Assert.Equal(0, pool.GetStatistics().InitializedPayloadBytes);
    }

    [Fact]
    public void AnOperationFromAnExitedRentersBlockCountsInThatCache()
    {
        using NativeConcurrentPool<int> pool = new();
        (NativePoolThreadCache exited, nuint block, NativeTransfer<int> held) = RentFromExitedThread(pool);

        int during = -1;
        int value;
        using (new OverrideBlock(block))
        {
            value = held.Read(view =>
            {
                during = exited.ActiveOperations;
                return view[3];
            });
        }

        Assert.Equal(7, value);
        Assert.Equal(1, during);
        Assert.Equal(0, exited.ActiveOperations);
        held.Dispose();
        Assert.Equal(0, pool.GetStatistics().InitializedPayloadBytes);
    }

    [Fact]
    public void ScratchFromAnExitedThreadsBlockTakesItsLaneUntilARangeDoesNotFit()
    {
        NativeConcurrentArena arena = null!;
        Thread maker = new(() => arena = new NativeConcurrentArena(preAllocateBytes: 1 << 16));
        maker.Start();
        Assert.True(maker.Join(Timeout));
        using (arena)
        {
            NativeOwnerKernel kernel = arena.KernelForInitialization;
            NativePoolThreadCache? exited = null;
            nuint block = 0;
            Thread worker = new(() =>
            {
                for (int index = 0; index < 3; index++)
                {
                    _ = arena.Scratch(4, index).Read(static view => view[0]);
                }

                exited = kernel.ThreadCacheForTest();
                block = exited?.OwnerBlock ?? 0;
            });
            worker.Start();
            Assert.True(worker.Join(Timeout));
            Assert.NotNull(exited);
            Assert.NotEqual((nuint)0, block);

            using (new OverrideBlock(block))
            {
                ConcurrentArenaLease<int> small = arena.Scratch(4, 5);
                Assert.Same(exited, small.LaneCacheForTest);
                Assert.Equal(20, small.Read(static view => view[0] + view[1] + view[2] + view[3]));

                // More than the exited thread's lane holds, but not a large
                // range: the claim takes this thread's own cache.
                ConcurrentArenaLease<int> wide = arena.Scratch(3000, 6);
                Assert.NotNull(wide.LaneCacheForTest);
                Assert.NotSame(exited, wide.LaneCacheForTest);
                Assert.Same(kernel.ThreadCacheForTest(), wide.LaneCacheForTest);
                Assert.Equal(6, wide.Read(static view => view[2999]));
            }

            arena.ReleaseLeasesToNativeMemory();
            Assert.Equal(0, arena.GetStatistics().InitializedPayloadBytes);
        }
    }

    // A thread that rents and returns transferable leases until its cache
    // records its block, then rents one more and ends, handing it over.
    private static (NativePoolThreadCache Cache, nuint Block, NativeTransfer<int> Held) RentFromExitedThread(NativeConcurrentPool<int> pool)
    {
        NativeOwnerKernel kernel = pool.KernelForTransfer;
        NativePoolThreadCache? cache = null;
        nuint block = 0;
        NativeTransfer<int> held = default;
        Thread renter = new(() =>
        {
            for (int index = 0; index < 3; index++)
            {
                pool.RentTransferable(4, index).Dispose();
            }

            cache = kernel.ThreadCacheForTest();
            block = cache?.OwnerBlock ?? 0;
            held = pool.RentTransferable(4, 7);
        });
        renter.Start();
        Assert.True(renter.Join(Timeout));
        Assert.NotNull(cache);
        Assert.NotEqual((nuint)0, block);
        return (cache!, block, held);
    }

    private readonly struct OverrideBlock : IDisposable
    {
        private readonly nuint _previous;

        internal OverrideBlock(nuint block)
        {
            _previous = NativeStackBlockForTest.Override;
            NativeStackBlockForTest.Override = block;
        }

        public void Dispose() => NativeStackBlockForTest.Override = _previous;
    }
}
