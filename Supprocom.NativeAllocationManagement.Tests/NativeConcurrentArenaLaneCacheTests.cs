namespace Supprocom.NativeAllocationManagement.Tests;

// Scratch from a synchronized arena on a lane thread keeps the lane's thread
// cache, so the lease's operations skip looking it up again. A fault injected
// anywhere in the process turns lanes off, so this runs alone.
[Collection(ProcessCounterGroup.Name)]
public sealed class NativeConcurrentArenaLaneCacheTests
{
    [Fact]
    public void LaneScratchKeepsItsCacheWithOrWithoutAnInitializer()
    {
        NativeConcurrentArena arena = null!;
        Thread maker = new(() => arena = new NativeConcurrentArena(preAllocateBytes: 4096));
        maker.Start();
        maker.Join();
        using (arena)
        {
            // The maker thread was the arena's own thread; this one rents from a lane.
            ConcurrentArenaLease<int> initialized = arena.Scratch<int>(16, static writer => writer.Fill(5));
            ConcurrentArenaLease<int> filled = arena.Scratch(16, 5);
            Assert.True(initialized.HasLaneCacheForTest);
            Assert.True(filled.HasLaneCacheForTest);
            Assert.Equal(80, initialized.Process<int, int, Sum>(0));
            Assert.Equal(5, initialized.Read(static view => view[15]));
            arena.ReleaseLeasesToNativeMemory();
        }
    }

    private readonly struct Sum : INativeSpanProcessor<int, int, int>
    {
        public static int Invoke(scoped Span<int> values, scoped in int state)
        {
            int sum = state;
            foreach (ref readonly int value in values)
            {
                sum += value;
            }

            return sum;
        }
    }
}
