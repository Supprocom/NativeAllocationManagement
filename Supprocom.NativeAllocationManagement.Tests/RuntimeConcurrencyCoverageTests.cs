using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class RuntimeConcurrencyCoverageTests
{
    [Fact]
    public async Task EveryNativeTouchRejectsBothWholeGenerationReturnPoliciesWhileEntered()
    {
        string[] operations = ["get_Item", "Clear", "CopyFrom", "CopyTo", "Access", "Read"];
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
    public async Task DisposeRestoresActiveStateAfterAConcurrentOperationWins()
    {
        NativePool<int> pool = new(returnOnDispose: NativeReturn.ToNativeMemory);
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
}
