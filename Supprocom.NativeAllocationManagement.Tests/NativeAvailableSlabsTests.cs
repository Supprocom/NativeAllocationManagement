namespace Supprocom.NativeAllocationManagement.Tests;

// The sorted set of a generation's idle slabs chooses exactly as the
// ordinal-ordered list it replaced: the smallest slab that holds the length,
// the oldest of those.
public sealed class NativeAvailableSlabsTests
{
    [Fact]
    public void TakesTheSmallestSlabThatHoldsTheLengthAndTheOldestOfThose()
    {
        NativeAvailableSlabs slabs = new();
        NativeSlab large = Slab(64, 1);
        NativeSlab newerSmall = Slab(16, 4);
        NativeSlab olderSmall = Slab(16, 2);
        NativeSlab tiny = Slab(8, 3);
        slabs.Add(large);
        slabs.Add(newerSmall);
        slabs.Add(olderSmall);
        slabs.Add(tiny);

        Assert.True(slabs.HasCapacity(64));
        Assert.False(slabs.HasCapacity(65));
        Assert.Same(olderSmall, slabs.TakeSmallest(10));
        Assert.Same(newerSmall, slabs.TakeSmallest(16));
        Assert.Same(large, slabs.TakeSmallest(16));
        Assert.Null(slabs.TakeSmallest(9));
        Assert.Same(tiny, slabs.TakeSmallest(1));
        Assert.Equal(0, slabs.Count);
    }

    [Fact]
    public void RemovesOnlyASlabItHolds()
    {
        NativeAvailableSlabs slabs = new();
        NativeSlab held = Slab(32, 7);
        NativeSlab next = Slab(32, 8);
        slabs.Add(held);
        slabs.Add(next);

        Assert.False(slabs.Remove(Slab(32, 7)));
        Assert.True(slabs.Remove(held));
        Assert.False(slabs.Remove(held));
        Assert.Equal(1, slabs.Count);
        Assert.Same(next, slabs[0]);
    }

    [Fact]
    public void ReservedCapacityAddsWithoutGrowing()
    {
        NativeAvailableSlabs slabs = new();
        slabs.EnsureCapacity(10);
        int capacity = slabs.Capacity;
        for (int index = 0; index < 10; index++)
        {
            slabs.Add(Slab(16, index + 1));
        }

        Assert.Equal(capacity, slabs.Capacity);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "A fixed seed reproduces the operation sequence; no security decision depends on it.")]
    public void ChoosesAsTheOrdinalOrderedListDid()
    {
        Random random = new(20261029);
        NativeAvailableSlabs slabs = new();
        List<NativeSlab> reference = [];
        long ordinal = 0;
        for (int step = 0; step < 20_000; step++)
        {
            int action = random.Next(3);
            if (action == 0 || reference.Count == 0)
            {
                NativeSlab slab = Slab(random.Next(1, 9) * 8, ++ordinal);
                slabs.Add(slab);
                int index = 0;
                while (index < reference.Count && reference[index].AllocationOrdinal < slab.AllocationOrdinal)
                {
                    index++;
                }

                reference.Insert(index, slab);
            }
            else if (action == 1)
            {
                int length = random.Next(1, 72);
                Assert.Same(TakeAsTheListDid(reference, length), slabs.TakeSmallest(length));
            }
            else
            {
                NativeSlab slab = reference[random.Next(reference.Count)];
                Assert.True(reference.Remove(slab));
                Assert.True(slabs.Remove(slab));
            }

            Assert.Equal(reference.Count, slabs.Count);
        }
    }

    private static NativeSlab? TakeAsTheListDid(List<NativeSlab> slabs, int length)
    {
        NativeSlab? best = null;
        int bestIndex = -1;
        for (int index = 0; index < slabs.Count; index++)
        {
            NativeSlab candidate = slabs[index];
            if (candidate.Capacity < length || (best is not null && candidate.Capacity >= best.Capacity))
            {
                continue;
            }

            best = candidate;
            bestIndex = index;
        }

        if (bestIndex >= 0)
        {
            slabs.RemoveAt(bestIndex);
        }

        return best;
    }

    private static NativeSlab Slab(int capacity, long ordinal) =>
        new(null!, capacity, containsReferences: false, ordinal);
}
