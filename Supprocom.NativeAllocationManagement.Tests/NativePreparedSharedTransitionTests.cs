namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativePreparedSharedTransitionTests
{
    [Theory]
    [InlineData(7, 3)]
    [InlineData(4, 1)]
    [InlineData(1, 6)]
    public void SparseTrimMoveAndReleaseKeepOriginalPageIdentityAfterEarlierHoles(int slots, int perPage)
    {
        NativeMemoryBudget budget = new(slots * 3L, traceCapacity: 32);
        using NativePreparedPool<byte> pool = new(new NativePoolPreparation(slots, 3, perPage), budget);
        int pages = 1 + (slots - 1) / perPage;
        int finalSlots = slots - (pages - 1) * perPage;
        PreparedPooled<byte> source = pool.Rent(1, (byte)7);
        bool moved = false;
        try
        {
            using PreparedPooled<byte> destination = PreparedPooled<byte>.Move(ref source);
            moved = true;
            Assert.Equal((nuint)((slots - finalSlots) * 3), pool.TrimRetainedMemory());
            Assert.Equal([(long)pages], pool.CurrentSegmentOrdinalsForTest);
            Assert.Equal(finalSlots * 3L, budget.CaptureStatistics().CommittedBytes);
            Assert.Equal(7, destination.Read(static view => view[0]));
        }
        finally
        {
            if (!moved) source.Dispose();
        }
        Assert.Equal((nuint)(finalSlots * 3), pool.TrimRetainedMemory());
        Assert.Empty(pool.CurrentSegmentOrdinalsForTest);
        Assert.Equal(pages, pool.GetStatistics().FreshSegmentAllocationCount);
        Span<NativeMemoryTraceEvent> events = stackalloc NativeMemoryTraceEvent[32];
        int count = budget.CopyTraceTo(events);
        int moves = 0;
        int trimmed = 0;
        foreach (ref readonly NativeMemoryTraceEvent item in events[..count])
        {
            if (item.Kind == NativeMemoryTraceKind.Moved)
            {
                moves++;
                Assert.Equal(pages, item.AllocationOrdinal);
                Assert.Equal(pool.Id, item.OwnerId);
                Assert.Equal((nuint)3, item.RequestedBytes);
            }
            if (item.Kind != NativeMemoryTraceKind.Trimmed) continue;
            trimmed++;
            Assert.Equal(trimmed, item.AllocationOrdinal);
            Assert.Equal(pool.Id, item.OwnerId);
        }
        Assert.Equal(1, moves);
        Assert.Equal(pages, trimmed);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(pages, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(pages, budget.CaptureStatistics().FreeCount);
    }

}
