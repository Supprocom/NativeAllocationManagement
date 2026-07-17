using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class RuntimeConcurrencyCoverageTests
{
    [Fact]
    public async Task EveryNativeTouchRejectsBothWholeGenerationReturnPoliciesWhileEntered()
    {
        string[] operations = ["get_Item", "set_Item", "Clear", "CopyFrom", "CopyTo", "Access", "Read"];
        foreach (NativeReturn policy in Enum.GetValues<NativeReturn>())
        {
            NativePool<int> pool = new();
            try
            {
                foreach (string operation in operations)
                {
                    await RaceReturnAgainstOperationAsync(pool, policy, operation);
                }
            }
            finally
            {
                NativeMemoryTestHooks.Reset();
                pool.Dispose();
            }
        }
    }

    [Fact]
    public async Task EveryNativeTouchRejectsAWholeGenerationReturnThatWinsBeforeEntry()
    {
        string[] operations = ["get_Item", "set_Item", "Clear", "CopyFrom", "CopyTo", "Access", "Read"];
        foreach (NativeReturn policy in Enum.GetValues<NativeReturn>())
        {
            foreach (string operation in operations)
            {
                await RaceReturnWinsBeforeOperationEntryAsync(policy, operation);
            }
        }
    }

    [Fact]
    public async Task DisposeRestoresActiveStateAfterAConcurrentOperationWins()
    {
        foreach (NativeReturn policy in Enum.GetValues<NativeReturn>())
        {
            NativePool<int> pool = new(returnOnDispose: policy);
            ManualResetEventSlim entered = new();
            ManualResetEventSlim release = new();
            Exception? disposeException = null;
            NativeMemoryTestHooks.SetOperationEntered(operation =>
            {
                if (operation != nameof(Pooled<int>.Access))
                {
                    return;
                }

                entered.Set();
                try
                {
                    pool.Dispose();
                }
                catch (Exception exception)
                {
                    disposeException = exception;
                }

                release.Wait(TimeSpan.FromSeconds(10));
            });

            try
            {
                Task worker = Task.Run(() =>
                {
                    Pooled<int> lease = pool.Rent(1);
                    try
                    {
                        lease.Access(static span => span[0] = 5);
                    }
                    finally
                    {
                        lease.Dispose();
                    }
                });

                Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
                release.Set();
                await worker;
                Assert.IsType<NativeAllocationInUseException>(disposeException);

                pool.Dispose();
            }
            finally
            {
                release.Set();
                NativeMemoryTestHooks.Reset();
            }
        }
    }

    [Fact]
    public void ReturnAndReLeaseRaceIsSerializedForBothPolicies()
    {
        foreach (NativeReturn policy in Enum.GetValues<NativeReturn>())
        {
            NativePool<int> pool = new(returnOnDispose: policy);
            using Barrier barrier = new(3);
            Exception? returnException = null;
            Exception? leaseException = null;

            Thread returnThread = new(() =>
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(10));
                try
                {
                    Return(pool, policy);
                }
                catch (Exception exception)
                {
                    returnException = exception;
                }
            });
            Thread leaseThread = new(() =>
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(10));
                try
                {
                    pool.LeaseFromMemory();
                }
                catch (Exception exception)
                {
                    leaseException = exception;
                }
            });

            returnThread.Start();
            leaseThread.Start();
            barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            Assert.True(returnThread.Join(TimeSpan.FromSeconds(10)));
            Assert.True(leaseThread.Join(TimeSpan.FromSeconds(10)));
            Assert.Null(returnException);
            Assert.True(leaseException is null or NativeAllocationStateException or NativeAllocationInUseException, leaseException?.ToString());

            if (leaseException is null)
            {
                Pooled<int> lease = pool.Rent(1);
                Assert.Equal(0, lease[0]);
                lease.Dispose();
            }

            pool.Dispose();
        }
    }

    [Fact]
    public void ReLeaseAndDisposeRacePublishesAtMostOneTerminalState()
    {
        for (int iteration = 0; iteration < 16; iteration++)
        {
            NativePool<int> pool = new();
            pool.ReturnToNativeMemory();
            using Barrier barrier = new(3);
            Exception? leaseException = null;
            Exception? disposeException = null;
            Thread leaseThread = new(() =>
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(10));
                try
                {
                    pool.LeaseFromMemory();
                }
                catch (Exception exception)
                {
                    leaseException = exception;
                }
            });
            Thread disposeThread = new(() =>
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(10));
                try
                {
                    pool.Dispose();
                }
                catch (Exception exception)
                {
                    disposeException = exception;
                }
            });

            leaseThread.Start();
            disposeThread.Start();
            barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            Assert.True(leaseThread.Join(TimeSpan.FromSeconds(10)));
            Assert.True(disposeThread.Join(TimeSpan.FromSeconds(10)));
            Assert.True(leaseException is null or NativeAllocationDisposedException, leaseException?.ToString());
            Assert.Null(disposeException);

            if (leaseException is null)
            {
                try
                {
                    Pooled<int> lease = pool.Rent(1);
                    lease.Dispose();
                }
                catch (NativeAllocationDisposedException)
                {
                }

                pool.Dispose();
            }
        }
    }

    [Fact]
    public void LocalCopiedAndFieldOwnerAliasesRemainGenerationSafe()
    {
        NativePool<int> pool = new();
        NativePool<int> localAlias = pool;
        FieldOwner holder = new(pool);
        Pooled<int> localLease = localAlias.Rent(1);
        Pooled<int> copiedLease = localLease;
        Pooled<int> fieldLease = holder.Pool.Rent(1);

        try
        {
            pool.ReturnToNativeMemory();
            Assert.IsType<NativeAllocationReturnedException>(CaptureReturned(copiedLease));
            Assert.IsType<NativeAllocationReturnedException>(CaptureReturned(fieldLease));
        }
        finally
        {
            localLease.Dispose();
            copiedLease.Dispose();
            fieldLease.Dispose();
            holder.Pool.Dispose();
        }
    }

    [Fact]
    public void ReturnAndDisposeRaceLeavesNoReleasableGeneration()
    {
        for (int iteration = 0; iteration < 32; iteration++)
        {
            NativePool<int> pool = new(initialCapacity: 2);
            Pooled<int> stale = pool.Rent(1);
            using Barrier barrier = new(3);
            Exception? returnException = null;
            Exception? disposeException = null;

            Thread returnThread = new(() =>
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(10));
                try
                {
                    pool.ReturnToNativeMemory();
                }
                catch (Exception exception)
                {
                    returnException = exception;
                }
            });
            Thread disposeThread = new(() =>
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(10));
                try
                {
                    pool.Dispose();
                }
                catch (Exception exception)
                {
                    disposeException = exception;
                }
            });

            returnThread.Start();
            disposeThread.Start();
            barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            Assert.True(returnThread.Join(TimeSpan.FromSeconds(10)));
            Assert.True(disposeThread.Join(TimeSpan.FromSeconds(10)));
            Assert.True(returnException is null or NativeAllocationDisposedException or NativeAllocationInUseException, returnException?.ToString());
            Assert.True(disposeException is null or NativeAllocationInUseException, disposeException?.ToString());
            Assert.True(CaptureReturnedOrDisposed(stale));
            stale.Dispose();
            pool.Dispose();
        }
    }

    [Fact]
    public void LocalOperationWinsAndReturnWinsBeforeEntryForBothPolicies()
    {
        foreach (NativeReturn policy in Enum.GetValues<NativeReturn>())
        {
            NativeRegion operationRegion = new(16, policy);
            Local<int> operationLocal = operationRegion.Lease<int>(1);
            NativeAllocationInUseException? inUse = null;
            NativeMemoryTestHooks.SetOperationEnteredWithAllocation((operation, kernel, _, _) =>
            {
                if (operation != nameof(Local<int>.Access))
                {
                    return;
                }

                try
                {
                    ReturnRegion(kernel, policy);
                }
                catch (NativeAllocationInUseException exception)
                {
                    inUse = exception;
                }
            });

            try
            {
                operationLocal.Access(static span => span[0] = 9);
                Assert.NotNull(inUse);
            }
            finally
            {
                NativeMemoryTestHooks.Reset();
                operationRegion.ReturnToNativeMemory();
                operationRegion.Dispose();
            }

            NativeRegion returnRegion = new(16, policy);
            Local<int> returnLocal = returnRegion.Lease<int>(1);
            NativeMemoryTestHooks.SetBeforeOperationEntryWithKernel((operation, kernel) =>
            {
                if (operation == nameof(Local<int>.Access))
                {
                    ReturnRegion(kernel, policy);
                }
            });
            try
            {
                Exception? failure = null;
                try
                {
                    returnLocal.Access(static _ => { });
                }
                catch (Exception exception)
                {
                    failure = exception;
                }

                Assert.IsType<NativeAllocationReturnedException>(failure);
            }
            finally
            {
                NativeMemoryTestHooks.Reset();
                returnRegion.Dispose();
            }
        }
    }

    private static async Task RaceReturnAgainstOperationAsync(
        NativePool<int> pool,
        NativeReturn policy,
        string operation)
    {
        ManualResetEventSlim entered = new();
        ManualResetEventSlim release = new();
        NativeMemoryTestHooks.SetOperationEntered(name =>
        {
            if (name == operation)
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        });

        try
        {
            Task worker = Task.Run(() => ExecuteOperation(pool, operation));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), $"Operation {operation} did not enter.");

            NativeAllocationInUseException exception = policy == NativeReturn.ToNativeMemory
                ? Assert.Throws<NativeAllocationInUseException>(pool.ReturnToNativeMemory)
                : Assert.Throws<NativeAllocationInUseException>(pool.ReturnToGarbageCollector);
            Assert.Contains("No lease was invalidated", exception.Message, StringComparison.OrdinalIgnoreCase);

            release.Set();
            await worker;
        }
        finally
        {
            release.Set();
        }
    }

    private static void ReturnRegion(NativeOwnerKernel kernel, NativeReturn policy)
    {
        if (policy == NativeReturn.ToNativeMemory)
        {
            kernel.ReturnToNativeMemory();
        }
        else
        {
            kernel.ReturnToGarbageCollector();
        }
    }

    private static void Return(NativePool<int> pool, NativeReturn policy)
    {
        if (policy == NativeReturn.ToNativeMemory)
        {
            pool.ReturnToNativeMemory();
        }
        else
        {
            pool.ReturnToGarbageCollector();
        }
    }

    private static bool CaptureReturnedOrDisposed(Pooled<int> lease)
    {
        try
        {
            _ = lease.Length;
            return false;
        }
        catch (NativeAllocationException exception) when (
            exception is NativeAllocationReturnedException or NativeAllocationDisposedException)
        {
            return true;
        }
    }

    private static void ExecuteOperation(NativePool<int> pool, string operation)
    {
        Pooled<int> lease = pool.Rent(2);
        try
        {
            switch (operation)
            {
                case "get_Item":
                    _ = lease[0];
                    break;
                case "set_Item":
                    lease[0] = 2;
                    break;
                case "Clear":
                    lease.Clear();
                    break;
                case "CopyFrom":
                    lease.CopyFrom(new int[2]);
                    break;
                case "CopyTo":
                    lease.CopyTo(new int[2]);
                    break;
                case "Access":
                    lease.Access(static span => span[0] = 1);
                    break;
                case "Read":
                    _ = lease.Read(static span => span[0]);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown test operation.");
            }
        }
        finally
        {
            lease.Dispose();
        }
    }

    private static NativeAllocationReturnedException CaptureReturned(Pooled<int> lease)
    {
        try
        {
            _ = lease.Length;
        }
        catch (NativeAllocationReturnedException exception)
        {
            return exception;
        }

        throw new Xunit.Sdk.XunitException("Expected NativeAllocationReturnedException.");
    }

    private sealed class FieldOwner
    {
        internal FieldOwner(NativePool<int> pool)
        {
            Pool = pool;
        }

        internal NativePool<int> Pool { get; }
    }

    private static async Task RaceReturnWinsBeforeOperationEntryAsync(NativeReturn policy, string operation)
    {
        NativePool<int> pool = new();
        ManualResetEventSlim beforeEntry = new();
        ManualResetEventSlim release = new();
        NativeMemoryTestHooks.SetBeforeOperationEntry(name =>
        {
            if (name == operation)
            {
                beforeEntry.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        });

        try
        {
            Task<Exception?> worker = Task.Run(() =>
            {
                Pooled<int> lease = pool.Rent(2);
                try
                {
                    ExecuteOperation(pool, operation);
                    return (Exception?)null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
                finally
                {
                    lease.Dispose();
                }
            });

            Assert.True(beforeEntry.Wait(TimeSpan.FromSeconds(10)), $"Operation {operation} did not reach its entry barrier.");
            if (policy == NativeReturn.ToNativeMemory)
            {
                pool.ReturnToNativeMemory();
            }
            else
            {
                pool.ReturnToGarbageCollector();
            }

            release.Set();
            Exception? exception = await worker;
            Assert.IsType<NativeAllocationReturnedException>(exception);
        }
        finally
        {
            release.Set();
            NativeMemoryTestHooks.Reset();
            pool.Dispose();
        }
    }
}
