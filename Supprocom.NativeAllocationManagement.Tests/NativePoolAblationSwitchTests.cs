using System.Reflection;

namespace Supprocom.NativeAllocationManagement.Tests;

// The shared-rent ablation (-p:NamAblation=...) swaps parts of the rent and
// return path for measurement. A normal build defines none of its constants, so
// the shipped path must have exactly the shape and checks it had before.
public sealed class NativePoolAblationSwitchTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly string[] LeaseFields = ["_capacity", "_kernel", "_length", "_slab", "_token"];

    [Fact]
    public void ANormalBuildHasNoAblationMembers()
    {
        Type pool = typeof(NativePool<int>);
        Assert.Null(pool.GetField("s_ablationShared", Any));
        Assert.Null(pool.GetField("_hot", Any));
        Assert.Null(pool.GetNestedType("SlabHot", Any));
        Assert.Null(pool.GetMethod("ReturnFlat", Any));
        Assert.Null(pool.GetMethod("SlabAt", Any));

        Type slab = typeof(NativePool<int>).GetNestedType("Slab", Any)!;
        Assert.Null(slab.GetField("Owner", Any));
        Assert.NotNull(slab.GetField("Index", Any));
        Assert.NotNull(slab.GetField("Token", Any));

        Type lease = typeof(Pooled<int>);
        string[] fields = lease.GetFields(Any).Select(f => f.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(LeaseFields, fields);
    }

    [Fact]
    public void TheSharedRentReturnPathKeepsItsChecksAndAccounting()
    {
        using NativePool<int> pool = new();
        Pooled<int> first = pool.Rent(16, 5);
        Pooled<int> stale = first;
        Assert.Equal(5, first.AsSpan()[15]);
        Assert.Equal(64, pool.GetStatistics().InitializedPayloadBytes);
        first.Dispose();
        Assert.Equal(0, pool.GetStatistics().InitializedPayloadBytes);
        Assert.Equal(64, pool.GetStatistics().PeakInitializedPayloadBytes);

        // A second return of the same lease is refused, not silently accepted.
        bool refused = false;
        try
        {
            stale.Dispose();
        }
        catch (InvalidOperationException)
        {
            refused = true;
        }

        Assert.True(refused);
    }

    [Fact]
    public void ARentOnAnotherThreadsPoolIsRefused()
    {
        using NativePool<int> pool = new();
        Exception? caught = null;
        Thread thread = new(() =>
        {
            try
            {
                pool.Rent(4, 1).Dispose();
            }
            catch (InvalidOperationException e)
            {
                caught = e;
            }
        });
        thread.Start();
        thread.Join();
        Assert.NotNull(caught);
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Rent(-1, 0));
    }
}
