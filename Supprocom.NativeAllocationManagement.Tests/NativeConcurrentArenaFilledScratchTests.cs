namespace Supprocom.NativeAllocationManagement.Tests;

// Filled and uninitialized scratch from a synchronized arena, and processors
// over its leases, on the arena's own thread and on lane threads.
public sealed class NativeConcurrentArenaFilledScratchTests
{
    [Fact]
    public void TheOwnThreadFillsAndProcessesWithoutCallbacks()
    {
        using NativeConcurrentArena arena = new(preAllocateBytes: 4096);
        ConcurrentArenaLease<int> filled = arena.Scratch(16, 7);
        Assert.Equal(16 * 7, filled.Process<int, int, Sum>(0));
        ConcurrentArenaLease<long> uninitialized = arena.ScratchUninitialized<long>(8);
        Assert.Equal(8, uninitialized.Process<long, int, Fill>(3));
        Assert.Equal(24L, uninitialized.Read(static view =>
        {
            long total = 0;
            for (int index = 0; index < view.Length; index++)
            {
                total += view[index];
            }

            return total;
        }));
        Assert.Equal(64 + 64, arena.GetStatistics().InitializedPayloadBytes);
        arena.ReleaseLeasesToNativeMemory();
        bool refused = false;
        try
        {
            _ = filled.Process<int, int, Sum>(0);
        }
        catch (InvalidOperationException)
        {
            refused = true;
        }

        Assert.True(refused);
        Assert.Throws<ArgumentOutOfRangeException>(() => arena.Scratch(-1, 0));
    }

    [Fact]
    public void LaneThreadsFillProcessAndKeepTheirOwnData()
    {
        NativeConcurrentArena arena = null!;
        Thread maker = new(() => arena = new NativeConcurrentArena(preAllocateBytes: 4096));
        maker.Start();
        maker.Join();
        using (arena)
        {
            const int workers = 4;
            InvalidOperationException?[] failures = new InvalidOperationException?[workers];
            Thread[] threads = new Thread[workers];
            for (int worker = 0; worker < workers; worker++)
            {
                int marker = worker + 1;
                threads[worker] = new Thread(() =>
                {
                    try
                    {
                        for (int iteration = 0; iteration < 2000; iteration++)
                        {
                            int length = 1 + ((iteration * 3 + marker) & 63);
                            ConcurrentArenaLease<int> filled = arena.Scratch(length, marker);
                            ConcurrentArenaLease<int> written = arena.ScratchUninitialized<int>(length);
                            written.Process<int, int, FillInt>(marker);
                            int seen = filled.Process<int, int, CountEqual>(marker) + written.Process<int, int, CountEqual>(marker);
                            if (seen != 2 * length)
                            {
                                throw new InvalidOperationException($"Thread {marker} saw {seen} of {2 * length} own values.");
                            }
                        }
                    }
                    catch (InvalidOperationException exception)
                    {
                        failures[marker - 1] = exception;
                    }
                });
                threads[worker].Start();
            }

            foreach (Thread thread in threads)
            {
                thread.Join();
            }

            Assert.All(failures, Assert.Null);
            arena.ReleaseLeasesToNativeMemory();
            Assert.Equal(0, arena.GetStatistics().InitializedPayloadBytes);
        }
    }

    [Fact]
    public void ReferenceElementsHaveNoSpanForAProcessor()
    {
        using NativeConcurrentArena arena = new(preAllocateBytes: 4096);
        ConcurrentArenaLease<string> lease = arena.Scratch<string>(2, static writer => writer.Fill("x"));
        bool refused = false;
        try
        {
            _ = lease.Process<int, int, Count<string>>(0);
        }
        catch (NotSupportedException)
        {
            refused = true;
        }

        Assert.True(refused);
        Assert.Equal("xx", lease.Read(static view => view[0] + view[1]));
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

    private readonly struct Fill : INativeSpanProcessor<long, long, int>
    {
        public static int Invoke(scoped Span<long> values, scoped in long state)
        {
            values.Fill(state);
            return values.Length;
        }
    }

    private readonly struct FillInt : INativeSpanProcessor<int, int, int>
    {
        public static int Invoke(scoped Span<int> values, scoped in int state)
        {
            values.Fill(state);
            return values.Length;
        }
    }

    private readonly struct CountEqual : INativeSpanProcessor<int, int, int>
    {
        public static int Invoke(scoped Span<int> values, scoped in int state)
        {
            int count = 0;
            foreach (ref readonly int value in values)
            {
                if (value == state)
                {
                    count++;
                }
            }

            return count;
        }
    }

    private readonly struct Count<T> : INativeSpanProcessor<T, int, int>
    {
        public static int Invoke(scoped Span<T> values, scoped in int state) => values.Length + state;
    }
}
