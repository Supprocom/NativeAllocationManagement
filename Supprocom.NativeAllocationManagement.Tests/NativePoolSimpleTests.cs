using System.Reflection;
using System.Collections.Concurrent;
using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativePoolSimpleTests
{
    [Fact]
    public void RentPublishesOnlyCompleteInitialization()
    {
        using NativePool<int> pool = new(
            preLease: 8,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);

        Pooled<int> lease = pool.Rent(
            8,
            static writer => writer.Fill(17));
        try
        {
            Assert.Equal(8, lease.Length);
            Assert.Equal(8, lease.Capacity);
            Assert.Equal(
                136,
                lease.Read(static view =>
                {
                    int sum = 0;
                    foreach (int value in view.AsSpan())
                    {
                        sum += value;
                    }

                    return sum;
                }));
        }
        finally
        {
            lease.Dispose();
        }
    }

    [Fact]
    public void FailedInitializerReturnsTheSlabOnce()
    {
        using NativePool<int> pool = new(
            preLease: 4,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);

        Assert.Throws<MarkerException>(() => pool.Rent(
            4,
            static writer =>
            {
                writer.Write(1);
                throw new MarkerException();
            }));

        NativeOwnerStatistics afterFailure = pool.GetStatistics();
        Assert.Equal(0, afterFailure.RequestedBytes);
        Assert.Equal(1, afterFailure.AvailableSegmentCount);

        Pooled<int> lease = pool.Rent(
            4,
            static writer => writer.Fill(9));
        lease.Dispose();
        Assert.Equal(0, pool.GetStatistics().RequestedBytes);
    }

    [Fact]
    public void IncompleteInitializerReportsProgressAndReturnsTheSlab()
    {
        using NativePool<int> pool = new(
            preLease: 4,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() => pool.Rent(
                4,
                static writer => writer.Write(1)));
        Assert.Equal(
            "The native lease initializer wrote 1 of 4 required elements.",
            exception.Message);

        NativeOwnerStatistics afterFailure = pool.GetStatistics();
        Assert.Equal(0, afterFailure.RequestedBytes);
        Assert.Equal(1, afterFailure.AvailableSegmentCount);

        Pooled<int> lease = pool.Rent(
            4,
            static writer => writer.Fill(9));
        lease.Dispose();
        Assert.Equal(0, pool.GetStatistics().RequestedBytes);
    }

    [Fact]
    public void StaleCopyCannotAccessOrReturnAReusedSlab()
    {
        using NativePool<int> pool = new(
            preLease: 4,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Pooled<int> first = pool.Rent(
            4,
            static writer => writer.Fill(1));
        Pooled<int> stale = first;
        first.Dispose();

        Pooled<int> second = pool.Rent(
            4,
            static writer => writer.Fill(2));
        try
        {
            AssertAccessIsReturned(stale);
            AssertDisposeIsReturned(stale);
            Assert.Equal(
                8,
                second.Read(static view =>
                {
                    int sum = 0;
                    foreach (int value in view.AsSpan())
                    {
                        sum += value;
                    }

                    return sum;
                }));
        }
        finally
        {
            second.Dispose();
        }
    }

    [Fact]
    public void LeaseTokenExhaustionFailsBeforeSlabStateChanges()
    {
        using NativePool<int> pool = new(
            preLease: 1,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        FieldInfo kernelField = typeof(NativePool<int>).GetField(
            "_kernel",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        object kernel = kernelField.GetValue(pool)!;
        FieldInfo tokenField = kernel.GetType().GetField(
            "_leaseTokenCounter",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        tokenField.SetValue(kernel, long.MaxValue - 1);

        Pooled<int> finalLease = pool.Rent(
            1,
            static writer => writer.Write(1));
        finalLease.Dispose();

        Assert.Throws<InvalidOperationException>(() => pool.Rent(
            1,
            static writer => writer.Write(2)));
        NativeOwnerStatistics statistics = pool.GetStatistics();
        Assert.Equal(0, statistics.RequestedBytes);
        Assert.Equal(1, statistics.AvailableSegmentCount);
    }

    [Fact]
    public void ActiveBorrowRejectsOwnerDisposal()
    {
        using NativePool<int> pool = new(
            preLease: 1,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Pooled<int> lease = pool.Rent(
            1,
            static writer => writer.Write(3));

        lease.Access(view =>
        {
            Assert.Equal(3, view[0]);
            Assert.Throws<InvalidOperationException>(
                () => pool.Dispose());
        });

        lease.Dispose();
    }

    [Fact]
    public void PersistentLeaseProcessesOnlyTheSelectedPrefix()
    {
        using NativePool<int> pool = new(
            preLease: 8,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Pooled<int> lease = pool.Rent(
            8,
            static writer => writer.Fill(0));
        try
        {
            int first = lease.Process(
                6,
                4,
                static (values, value) =>
                {
                    values.Fill(value);
                    int sum = 0;
                    foreach (int item in values)
                    {
                        sum += item;
                    }

                    return sum;
                });
            int second = lease.Process(
                2,
                7,
                static (values, value) =>
                {
                    values.Fill(value);
                    return values.Length;
                });

            Assert.Equal(24, first);
            Assert.Equal(2, second);
            Assert.Equal(
                [7, 7, 4, 4, 4, 4, 0, 0],
                lease.Read(static view => view.AsSpan().ToArray()));
        }
        finally
        {
            lease.Dispose();
        }
    }

    [Fact]
    public void FailedPersistentProcessReleasesTheBorrow()
    {
        using NativePool<int> pool = new(
            preLease: 4,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Pooled<int> lease = pool.Rent(
            4,
            static writer => writer.Fill(0));

        try
        {
            lease.Process<int, int>(
                4,
                1,
                static (values, value) =>
                {
                    values.Fill(value);
                    throw new MarkerException();
                });
            Assert.Fail("The processor failure was not returned.");
        }
        catch (MarkerException)
        {
        }

        Assert.Equal(
            12,
            lease.Process(
                4,
                3,
                static (values, value) =>
                {
                    values.Fill(value);
                    return values.ToArray().Sum();
                }));
        lease.Dispose();
    }

    [Fact]
    public void PersistentProcessRejectsAnInvalidLogicalLength()
    {
        using NativePool<int> pool = new(
            preLease: 4,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Pooled<int> lease = pool.Rent(
            4,
            static writer => writer.Fill(0));

        try
        {
            lease.Process(
                -1,
                0,
                static (values, state) => state);
            Assert.Fail("The negative logical length was accepted.");
        }
        catch (ArgumentOutOfRangeException)
        {
        }

        try
        {
            lease.Process(
                5,
                0,
                static (values, state) => state);
            Assert.Fail("The excessive logical length was accepted.");
        }
        catch (ArgumentOutOfRangeException)
        {
        }

        lease.Dispose();
    }

    [Fact]
    public void CrossThreadUseFailsClosed()
    {
        using NativePool<int> pool = new(
            preLease: 2,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                pool.Rent(
                    2,
                    static writer => writer.Fill(4));
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.IsType<NativeAllocationStateException>(failure);
    }

    [Fact]
    public void TrimFreesOnlyIdleSlabs()
    {
        using NativePool<int> pool = new(
            preLease: 4,
            preAllocateBytes: 24,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Pooled<int> lease = pool.Rent(
            4,
            static writer => writer.Fill(1));

        nuint released = pool.TrimRetainedMemory();
        Assert.Equal((nuint)24, released);
        Assert.Equal(16, pool.GetStatistics().RetainedBytes);

        lease.Dispose();
        Assert.Equal((nuint)16, pool.TrimRetainedMemory());
        Assert.Equal(0, pool.GetStatistics().RetainedBytes);
    }

    [Fact]
    public void ReturnedSlabCacheDoesNotHideALargerFreeSlab()
    {
        using NativePool<int> pool = new(
            preLease: 8,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Pooled<int> large = pool.Rent(
            8,
            static writer => writer.Fill(1));
        Pooled<int> small = pool.Rent(
            4,
            static writer => writer.Fill(2));
        large.Dispose();
        small.Dispose();
        long freshSegments =
            pool.GetStatistics().FreshSegmentAllocationCount;

        Pooled<int> selected = pool.Rent(
            7,
            static writer => writer.Fill(3));
        try
        {
            Assert.Equal(8, selected.Capacity);
            Assert.Equal(
                freshSegments,
                pool.GetStatistics().FreshSegmentAllocationCount);
        }
        finally
        {
            selected.Dispose();
        }
    }

    [Fact]
    public void ReturnedSlabCacheRejoinsClassesForAnotherLength()
    {
        using NativePool<int> pool = new(
            preLease: 8,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Pooled<int> first = pool.Rent(
            8,
            static writer => writer.Fill(1));
        first.Dispose();
        long freshSegments =
            pool.GetStatistics().FreshSegmentAllocationCount;

        Pooled<int> selected = pool.Rent(
            7,
            static writer => writer.Fill(2));
        try
        {
            Assert.Equal(8, selected.Capacity);
            Assert.Equal(
                freshSegments,
                pool.GetStatistics().FreshSegmentAllocationCount);
        }
        finally
        {
            selected.Dispose();
        }
    }

    [Fact]
    public void RetiredPoolReleasesStorageOnTheCoordinatorThreadOnce()
    {
        NativeMemoryTestHooks.Reset();
        NativePool<int> pool = new(
            preLease: 16,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Pooled<int> lease = pool.Rent(
            16,
            static writer => writer.Fill(7));
        lease.Dispose();
        pool.Retire();

        Exception? failure = null;
        Thread coordinator = new(() =>
        {
            try
            {
                pool.ReleaseRetiredStorage();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        coordinator.Start();
        Assert.True(coordinator.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(failure);

        NativeMemoryTestMetrics released =
            NativeMemoryTestHooks.Snapshot();
        Assert.Equal(1, released.AllocationCount);
        Assert.Equal(1, released.FreeCount);
        Assert.Equal(0, released.OutstandingNativeBytes);
        Assert.Throws<InvalidOperationException>(
            pool.ReleaseRetiredStorage);
        Assert.Equal(
            released.FreeCount,
            NativeMemoryTestHooks.Snapshot().FreeCount);
    }

    [Fact]
    public void RetirementRejectsEachActiveWorkerState()
    {
        NativePool<int> pool = new(
            preLease: 4,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        InvalidOperationException? initializerFailure = null;
        Pooled<int> lease = pool.Rent(
            4,
            writer =>
            {
                initializerFailure = Assert.Throws<InvalidOperationException>(
                    pool.Retire);
                writer.Fill(1);
            });
        Assert.NotNull(initializerFailure);

        Assert.Throws<InvalidOperationException>(pool.Retire);
        lease.Access(values =>
        {
            Assert.Equal(1, values[0]);
            Assert.Throws<InvalidOperationException>(pool.Retire);
        });

        lease.Dispose();
        pool.Retire();
        pool.ReleaseRetiredStorage();
    }

    [Fact]
    public void RetiredPoolRejectsWorkerOperationsAndOrdinaryDisposal()
    {
        NativePool<int> pool = new(
            preLease: 4,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        pool.Retire();

        Assert.Throws<NativeAllocationDisposedException>(() => pool.Rent(
            1,
            static writer => writer.Write(1)));
        Assert.Throws<NativeAllocationDisposedException>(
            () => _ = pool.GetStatistics());
        Assert.Throws<NativeAllocationDisposedException>(
            () => _ = pool.TrimRetainedMemory());
        Assert.Throws<NativeAllocationDisposedException>(pool.Retire);
        Assert.Throws<InvalidOperationException>(pool.Dispose);

        pool.ReleaseRetiredStorage();
    }

    [Fact]
    public void RetiredCleanupRejectsMissingRetirement()
    {
        using NativePool<int> pool = new(
            preLease: 4,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);

        Assert.Throws<InvalidOperationException>(
            pool.ReleaseRetiredStorage);
    }

    [Fact]
    public void OwnerBoundariesRejectTheWrongThread()
    {
        NativePool<int> pool = new(
            preLease: 4,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ConcurrentQueue<Exception> failures = new();
        Thread thread = new(() =>
        {
            Capture(() => _ = pool.GetStatistics());
            Capture(() => _ = pool.TrimRetainedMemory());
            Capture(pool.Retire);
            Capture(pool.Dispose);

            void Capture(Action action)
            {
                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    failures.Enqueue(exception);
                }
            }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));

        Assert.Equal(4, failures.Count);
        Assert.All(
            failures,
            static failure => Assert.IsType<
                NativeAllocationStateException>(failure));
        pool.Retire();
        pool.ReleaseRetiredStorage();
    }

    [Fact]
    public void ConcurrentCoordinatorCleanupFreesStorageOnce()
    {
        NativeMemoryTestHooks.Reset();
        NativePool<int> pool = new(
            preLease: 16,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        pool.Retire();
        ConcurrentQueue<Exception> failures = new();
        Thread[] coordinators = Enumerable.Range(0, 2)
            .Select(_ => new Thread(() =>
            {
                try
                {
                    pool.ReleaseRetiredStorage();
                }
                catch (Exception exception)
                {
                    failures.Enqueue(exception);
                }
            }))
            .ToArray();
        foreach (Thread coordinator in coordinators)
        {
            coordinator.Start();
        }

        foreach (Thread coordinator in coordinators)
        {
            Assert.True(coordinator.Join(TimeSpan.FromSeconds(5)));
        }

        Assert.Single(failures);
        Assert.IsType<InvalidOperationException>(failures.Single());
        NativeMemoryTestMetrics released =
            NativeMemoryTestHooks.Snapshot();
        Assert.Equal(1, released.FreeCount);
        Assert.Equal(0, released.OutstandingNativeBytes);
    }

    [Fact]
    public void ThreadLocalWorkersRetireBeforeCoordinatorCleanup()
    {
        const int workerCount = 24;
        const int buildCount = 1_179;
        NativeMemoryTestHooks.Reset();
        using ThreadLocal<RetiredPoolWorker> workers = new(
            static () => new RetiredPoolWorker(),
            trackAllValues: true);
        ConcurrentQueue<Exception> failures = new();
        long checksum = 0;
        Thread[] threads = new Thread[workerCount];
        for (int workerIndex = 0; workerIndex < workerCount; workerIndex++)
        {
            int capturedIndex = workerIndex;
            threads[workerIndex] = new Thread(() =>
            {
                try
                {
                    RetiredPoolWorker worker = workers.Value!;
                    for (int build = capturedIndex;
                        build < buildCount;
                        build += workerCount)
                    {
                        Interlocked.Add(
                            ref checksum,
                            worker.Build());
                    }

                    worker.Retire();
                }
                catch (Exception exception)
                {
                    failures.Enqueue(exception);
                }
            });
            threads[workerIndex].Start();
        }

        foreach (Thread thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        }

        Assert.Empty(failures);
        Assert.Equal(buildCount * 2L, checksum);
        Assert.Equal(workerCount, workers.Values.Count);
        foreach (RetiredPoolWorker worker in workers.Values)
        {
            worker.Dispose();
        }

        NativeMemoryTestMetrics released =
            NativeMemoryTestHooks.Snapshot();
        Assert.Equal(workerCount, released.AllocationCount);
        Assert.Equal(workerCount, released.FreeCount);
        Assert.Equal(0, released.OutstandingNativeBytes);
    }

    [Fact]
    public void AbandonedRetiredPoolUsesFinalizationForEmergencyCleanup()
    {
        NativeMemoryTestHooks.Reset();
        WeakReference abandoned = AbandonRetiredPool();

        for (int attempt = 0;
            attempt < 4 && abandoned.IsAlive;
            attempt++)
        {
            GC.Collect(
                2,
                GCCollectionMode.Forced,
                blocking: true,
                compacting: true);
            GC.WaitForPendingFinalizers();
        }

        Assert.False(abandoned.IsAlive);
        NativeMemoryTestMetrics released =
            NativeMemoryTestHooks.Snapshot();
        Assert.Equal(1, released.AllocationCount);
        Assert.Equal(1, released.FreeCount);
        Assert.Equal(0, released.OutstandingNativeBytes);
    }

    [Fact]
    public void PoolWorkerHotPathHasNoSynchronizationOrGeneralRecords()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Supprocom.NativeAllocationManagement",
            "NativePoolKernel.cs"));
        string rent = GetSourceRange(
            source,
            "internal Pooled<T> Rent(",
            "internal IntPtr EnterBorrow(");
        string access = GetSourceRange(
            source,
            "internal IntPtr EnterBorrow(",
            "internal void ExitBorrow(");
        string returned = GetSourceRange(
            source,
            "internal void Return(",
            "internal NativeOwnerStatistics GetStatistics()");

        foreach (string path in new[] { rent, access, returned })
        {
            Assert.DoesNotContain(
                "lock (",
                path,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "Interlocked.",
                path,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "Dictionary<",
                path,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "NativeOperationAdmission",
                path,
                StringComparison.Ordinal);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference AbandonRetiredPool()
    {
        NativePool<int> pool = new(
            preLease: 16,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        pool.Retire();
        return new WeakReference(pool);
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

    private sealed class RetiredPoolWorker : IDisposable
    {
        private const int Capacity = 153_600;
        private readonly NativePool<short> _pool = new(
            preLease: Capacity,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);

        internal int Build()
        {
            Pooled<short> lease = _pool.Rent(
                Capacity,
                static writer => writer.Fill(1));
            try
            {
                return lease.Read(static values =>
                    values[0] + values[values.Length - 1]);
            }
            finally
            {
                lease.Dispose();
            }
        }

        internal void Retire() => _pool.Retire();

        public void Dispose() => _pool.ReleaseRetiredStorage();
    }

    private sealed class MarkerException : Exception;

    private static void AssertAccessIsReturned(Pooled<int> lease)
    {
        try
        {
            lease.Access(static view => view.Clear());
            Assert.Fail("The stale lease accessed returned storage.");
        }
        catch (NativeAllocationReturnedException)
        {
        }
    }

    private static void AssertDisposeIsReturned(Pooled<int> lease)
    {
        try
        {
            lease.Dispose();
            Assert.Fail("The stale lease returned storage twice.");
        }
        catch (NativeAllocationReturnedException)
        {
        }
    }
}
