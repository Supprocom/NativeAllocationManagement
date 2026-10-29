using System.Reflection;

namespace Supprocom.NativeAllocationManagement.Tests;

// A thread keeps one cache per pool it uses, however many pools that is; a
// pool's cache slot is given back when the pool is disposed.
public sealed class NativePoolThreadCacheTableTests
{
    [Fact]
    public void AThreadUsingManyPoolsKeepsACacheForEach()
    {
        RunOnFreshThread(static () =>
        {
            NativeConcurrentPool<int>[] pools = new NativeConcurrentPool<int>[20];
            foreach (ref NativeConcurrentPool<int> pool in pools.AsSpan())
            {
                pool = new NativeConcurrentPool<int>();
            }

            try
            {
                for (int round = 0; round < 3; round++)
                {
                    for (int index = 0; index < pools.Length; index++)
                    {
                        using ConcurrentPooled<int> lease = pools[index].Rent(4, index + round);
                        Assert.Equal(index + round, lease.Read(static view => view[3]));
                    }
                }

                NativePoolThreadCache?[] table = Table();
                foreach (NativeConcurrentPool<int> pool in pools)
                {
                    NativeOwnerKernel kernel = pool.KernelForTransfer;
                    NativePoolThreadCache? cache = table[kernel.CacheSlot];
                    Assert.NotNull(cache);
                    Assert.Equal(kernel.Id, cache.KernelId);
                }
            }
            finally
            {
                foreach (NativeConcurrentPool<int> pool in pools)
                {
                    pool.Dispose();
                }
            }
        });
    }

    [Fact]
    public void ADisposedPoolGivesBackItsSlotAndALaterPoolGetsItsOwnCache()
    {
        RunOnFreshThread(static () =>
        {
            NativeConcurrentPool<int> first = new();
            using (ConcurrentPooled<int> lease = first.Rent(4, 1))
            {
                Assert.Equal(1, lease.Read(static view => view[0]));
            }

            NativeOwnerKernel firstKernel = first.KernelForTransfer;
            first.Dispose();
            Assert.Equal(-1, firstKernel.CacheSlot);

            using NativeConcurrentPool<int> second = new();
            using (ConcurrentPooled<int> lease = second.Rent(4, 2))
            {
                Assert.Equal(2, lease.Read(static view => view[0]));
            }

            NativeOwnerKernel secondKernel = second.KernelForTransfer;
            NativePoolThreadCache? cache = Table()[secondKernel.CacheSlot];
            Assert.NotNull(cache);
            Assert.Equal(secondKernel.Id, cache.KernelId);
        });
    }

    private static NativePoolThreadCache?[] Table() =>
        Assert.IsType<NativePoolThreadCache?[]>(
            typeof(NativePoolThreadCaches).GetField("t_bySlot", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null));

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "The worker failure is rethrown on the test thread.")]
    private static void RunOnFreshThread(Action action)
    {
        Exception? failure = null;
        Thread worker = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
