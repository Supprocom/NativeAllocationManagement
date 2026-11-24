using System.Reflection;

namespace Supprocom.NativeAllocationManagement.Tests;

// The shared-rent ablation (-p:NamAblation=SmallPooled) drops one field from
// Pooled<T> for measurement. A normal build defines no such constant, so the
// shipped path must keep its shape and every check it had. The slab state is a
// flat struct table indexed by the lease; these tests pin the checks whose code
// moved with it.
public sealed class NativePoolAblationSwitchTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly string[] LeaseFields = ["_capacity", "_kernel", "_length", "_slabIndex", "_token"];

    [Fact]
    public void ANormalBuildKeepsTheLeaseShapeAndAFlatSlabTable()
    {
        string[] fields = typeof(Pooled<int>).GetFields(Any).Select(f => f.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(LeaseFields, fields);

        Type slab = typeof(NativePool<int>).GetNestedType("Slab", Any)!;
        Assert.True(slab.IsValueType);
        Assert.True(typeof(NativePool<int>).GetField("_slabs", Any)!.FieldType.GetElementType()!.IsValueType);
        Assert.Null(typeof(NativePool<int>).GetField("s_ablationShared", Any));
    }

    [Fact]
    public void AccountingStaysExactThroughRentAndReturn()
    {
        using NativePool<int> pool = new();
        Pooled<int> first = pool.Rent(16, 5);
        Assert.Equal(5, first.AsSpan()[15]);
        Assert.Equal(64, pool.GetStatistics().InitializedPayloadBytes);
        Assert.Equal(1, pool.CurrentAllocationRecordCountForTest);
        first.Dispose();
        Assert.Equal(0, pool.GetStatistics().InitializedPayloadBytes);
        Assert.Equal(64, pool.GetStatistics().PeakInitializedPayloadBytes);
        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
    }

    [Fact]
    public void ADoubleReturnThroughAStaleCopyIsRefused()
    {
        using NativePool<int> pool = new();
        Pooled<int> first = pool.Rent(16, 5);
        Pooled<int> stale = first;
        first.Dispose();
        Assert.Throws<NativeAllocationReturnedException>(() => pool.Return(0, 1, 16));
        bool refused = false;
        try
        {
            stale.Dispose();
        }
        catch (NativeAllocationReturnedException)
        {
            refused = true;
        }

        Assert.True(refused);
        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
    }

    [Fact]
    public void AStaleCopyIsRefusedAfterItsSlotIsRentedAgain()
    {
        using NativePool<int> pool = new();
        Pooled<int> first = pool.Rent(16, 5);
        Pooled<int> stale = first;
        first.Dispose();
        using Pooled<int> second = pool.Rent(16, 6);
        bool refused = false;
        try
        {
            stale.Dispose();
        }
        catch (NativeAllocationReturnedException)
        {
            refused = true;
        }

        Assert.True(refused);
        Assert.Equal(6, second.AsSpan()[0]);
    }

    [Fact]
    public void AReturnWhileABorrowIsActiveIsRefusedAndKeepsTheLease()
    {
        using NativePool<int> pool = new();
        using Pooled<int> lease = pool.Rent(8, 3);
        lease.Access(view =>
        {
            Assert.Throws<InvalidOperationException>(() => pool.Return(0, 1, 8));
            Assert.Equal(3, view.AsSpan()[7]);
        });
        Assert.Equal(1, pool.CurrentAllocationRecordCountForTest);
    }

    [Fact]
    public void ARentOrReturnAfterDisposeIsRefused()
    {
        NativePool<int> pool = new();
        pool.Dispose();
        Assert.Throws<NativeAllocationDisposedException>(() => pool.Rent(4, 1));
        Assert.Throws<NativeAllocationDisposedException>(() => pool.Return(0, 1, 4));
    }

    [Fact]
    public void ARentOrReturnOnAnotherThreadIsRefused()
    {
        using NativePool<int> pool = new();
        Pooled<int> lease = pool.Rent(4, 1);
        Exception? rent = null;
        Exception? negative = null;
        Thread thread = new(() =>
        {
            try
            {
                pool.Rent(4, 1);
            }
            catch (NativeAllocationStateException e)
            {
                rent = e;
            }

            try
            {
                pool.Rent(-1, 0);
            }
            catch (ArgumentOutOfRangeException e)
            {
                negative = e;
            }
        });
        thread.Start();
        thread.Join();
        Assert.NotNull(rent);
        Assert.NotNull(negative);
        lease.Dispose();
    }

    [Fact]
    public void ASharedRentOnAnotherThreadsPoolIsRefused()
    {
        NativePool<int> mine = NativePool<int>.Shared;
        Exception? caught = null;
        Thread thread = new(() =>
        {
            try
            {
                mine.Rent(4, 1);
            }
            catch (NativeAllocationStateException e)
            {
                caught = e;
            }
        });
        thread.Start();
        thread.Join();
        Assert.NotNull(caught);
        using Pooled<int> own = NativePool<int>.Shared.Rent(4, 2);
        Assert.Equal(2, own.AsSpan()[3]);
    }

    [Fact]
    public void TheSlotTableGrowsWhileLeasesAreLiveAndTheyKeepTheirData()
    {
        using NativePool<int> pool = new();
        int slots = pool.CurrentBankCapacitiesForTest.Slabs;
        Pooled<int> a = pool.Rent(1, 1);
        Pooled<int> b = pool.Rent(3, 2);
        Pooled<int> c = pool.Rent(9, 3);
        Pooled<int> d = pool.Rent(33, 4);
        Pooled<int> e = pool.Rent(129, 5);
        Pooled<int> f = pool.Rent(513, 6);
        Assert.True(pool.CurrentBankCapacitiesForTest.Slabs > slots);
        Assert.Equal(1, a.AsSpan()[0]);
        Assert.Equal(2, b.AsSpan()[2]);
        Assert.Equal(3, c.AsSpan()[8]);
        Assert.Equal(4, d.AsSpan()[32]);
        Assert.Equal(5, e.AsSpan()[128]);
        Assert.Equal(6, f.AsSpan()[512]);
        Assert.Equal(6, pool.CurrentAllocationRecordCountForTest);
        Assert.Equal((1 + 3 + 9 + 33 + 129 + 513) * 4, pool.GetStatistics().InitializedPayloadBytes);
        f.Dispose();
        e.Dispose();
        d.Dispose();
        c.Dispose();
        b.Dispose();
        a.Dispose();
        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
        Assert.Equal(0, pool.GetStatistics().InitializedPayloadBytes);
    }

    [Fact]
    public void AGrowthInsideAnInitializerLeavesTheOuterLeaseValid()
    {
        using NativePool<int> pool = new();
        Pooled<int> outer = pool.Rent(8, writer =>
        {
            Pooled<int> n1 = pool.Rent(3, 1);
            Pooled<int> n2 = pool.Rent(17, 1);
            Pooled<int> n3 = pool.Rent(65, 1);
            Pooled<int> n4 = pool.Rent(257, 1);
            n4.Dispose();
            n3.Dispose();
            n2.Dispose();
            n1.Dispose();
            writer.Fill(9);
        });
        Assert.Equal(9, outer.AsSpan()[7]);
        outer.Dispose();
        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
    }

    [Fact]
    public void ANewPoolAdoptsTheSlabsAnEarlierOneKept()
    {
        NativePool<int> first = new();
        first.Rent(64, 1).Dispose();
        long[] ordinals = first.CurrentSegmentOrdinalsForTest;
        first.Dispose();
        using NativePool<int> second = new();
        long allocations = NativeMemoryTestHooks.Snapshot().AllocationCount;
        using (Pooled<int> lease = second.Rent(64, 7))
        {
            Assert.Equal(7, lease.AsSpan()[63]);
            Assert.Equal(1, second.CurrentAllocationRecordCountForTest);
        }

        Assert.Equal(allocations, NativeMemoryTestHooks.Snapshot().AllocationCount);
        Assert.Equal(ordinals, second.CurrentSegmentOrdinalsForTest);
        Assert.Equal(0, second.CurrentAllocationRecordCountForTest);
    }
}
