using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeLeaseOperationsTests
{
    [Fact]
    public void EveryCompositeOverloadUsesDirectBoundedStorage()
    {
        NativeMemoryTestHooks.Reset();
        using NativePool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativePool<int> secondPool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);

        Pooled<int> first = pool.Rent(2);
        Pooled<int> second = pool.Rent(2);
        Pooled<int> third = secondPool.Rent(2);
        ArenaLease<int> arenaFirst = arena.Scratch<int>(2);
        ArenaLease<int> arenaSecond = arena.Scratch<int>(2);

        NativeLeaseOperations.Access(first, second, (left, right) =>
        {
            left[0] = 11;
            right[0] = 22;
        });
        NativeLeaseOperations.Access(first, second, third, (left, middle, right) =>
        {
            left[1] = middle[0] + right[0];
        });
        NativeLeaseOperations.Access(first, arenaFirst, (pooled, scratch) =>
        {
            scratch[0] = pooled[1];
        });
        NativeLeaseOperations.Access(
            first,
            second,
            third,
            arenaFirst,
            arenaSecond,
            (faces, vertices, indices, slices, upload) =>
            {
                faces[0] = 31;
                vertices[0] = 32;
                indices[0] = 33;
                slices[0] = 34;
                upload[0] = 35;
            });

        Assert.Equal(31, first[0]);
        Assert.Equal(32, second[0]);
        Assert.Equal(33, third[0]);
        Assert.Equal(22, first[1]);
        Assert.Equal(34, arenaFirst[0]);
        Assert.Equal(35, arenaSecond[0]);
    }

    [Fact]
    public void LaterEntryFailureReleasesEarlierTokensAndCallbackFailureCleansUp()
    {
        NativeMemoryTestHooks.Reset();
        using NativePool<int> goodPool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativePool<int> stalePool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Pooled<int> good = goodPool.Rent(1);
        Pooled<int> stale = stalePool.Rent(1);
        stalePool.ReturnMemoryToNativeMemory();

        NativeAllocationException? entryFailure = null;
        try
        {
            NativeLeaseOperations.Access(good, stale, static (_, _) => { });
        }
        catch (NativeAllocationException exception)
        {
            entryFailure = exception;
        }

        Assert.NotNull(entryFailure);
        good[0] = 41;
        Assert.Equal(41, good[0]);

        bool callbackFailed = false;
        try
        {
            NativeLeaseOperations.Access(good, good, static (_, _) => throw new InvalidOperationException("callback"));
        }
        catch (InvalidOperationException)
        {
            callbackFailed = true;
        }

        Assert.True(callbackFailed);
        good.Dispose();
        stale.Dispose();
        goodPool.ReturnMemoryToNativeMemory();
        stalePool.Dispose();
    }

    [Fact]
    public void SameOwnerAliasAndLifecycleTransitionsAreSafeAroundCompositeEntry()
    {
        NativeMemoryTestHooks.Reset();
        using NativePool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Pooled<int> lease = pool.Rent(1);

        NativeLeaseOperations.Access(lease, lease, (first, second) =>
        {
            first[0] = 7;
            second[0] += 1;
        });
        Assert.Equal(8, lease[0]);

        NativeAllocationException? strictFailure = null;
        NativeLeaseOperations.Access(lease, lease, (_, _) =>
        {
            try
            {
                pool.ReturnMemoryToNativeMemory();
            }
            catch (NativeAllocationException exception)
            {
                strictFailure = exception;
            }
        });

        Assert.IsType<NativeAllocationInUseException>(strictFailure);
        pool.ReturnMemoryToNativeMemory();
        pool.LeaseFromMemory();
        Pooled<int> fresh = pool.Rent(1);
        fresh[0] = 19;
        fresh.Dispose();
    }

    [Fact]
    public void ReferenceSlotsAndStaleHandlesRemainCorrectAfterTolerantTransition()
    {
        NativeMemoryTestHooks.Reset();
        using NativePool<string> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Pooled<string> values = pool.Rent(2);
        values[0] = "first";
        NativeLeaseOperations.Access(values, values, (first, second) =>
        {
            second[1] = first[0] + "-second";
        });

        Assert.Equal("first-second", values[1]);
        pool.ReleaseLeasesToGarbageCollector();
        NativeAllocationException? staleFailure = null;
        try
        {
            _ = values.Length;
        }
        catch (NativeAllocationException exception)
        {
            staleFailure = exception;
        }

        Assert.NotNull(staleFailure);
        Pooled<string> fresh = pool.Rent(1);
        Assert.Null(fresh[0]);
        fresh.Dispose();
    }
}
