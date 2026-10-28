namespace Supprocom.NativeAllocationManagement.Tests;

// NativeConcurrentPool<T>.Process rents, fills, processes and returns in one
// call, through the renting thread's cache when it can.
public sealed class NativeConcurrentPoolOneShotTests
{
    [Fact]
    public void ProcessFillsRunsAndReturnsTheRental()
    {
        using NativeConcurrentPool<int> pool = new();
        Assert.Equal(22, pool.Process<int, int, SumPlus>(4, 3, 10));
        Assert.Equal(0, pool.GetStatistics().RequestedBytes);
        long fresh = pool.GetStatistics().FreshSegmentAllocationCount;
        Assert.Equal(6, pool.Process<int, int, SumPlus>(4, 1, 2));
        Assert.Equal(fresh, pool.GetStatistics().FreshSegmentAllocationCount);
        Assert.Equal(0, pool.GetStatistics().RequestedBytes);
    }

    [Fact]
    public void TheDelegateOverloadSeesTheFilledSpan()
    {
        using NativeConcurrentPool<int> pool = new();
        Assert.Equal(7, pool.Process(4, 2, 1, static (span, state) => span.Length + span[0] + state));
        Assert.Equal(0, pool.GetStatistics().RequestedBytes);
    }

    [Fact]
    public void AFailedProcessorStillReturnsTheRental()
    {
        using NativeConcurrentPool<int> pool = new();
        Assert.Equal(4, pool.Process<int, int, SumPlus>(4, 1, 0));
        Assert.Throws<FormatException>(() => pool.Process<int, int, Thrower>(4, 0, 0));
        Assert.Equal(0, pool.GetStatistics().RequestedBytes);
        Assert.Throws<FormatException>(() => pool.Process<int, int>(4, 0, 0, static (_, _) => throw new FormatException()));
        Assert.Equal(0, pool.GetStatistics().RequestedBytes);
        Assert.Equal(8, pool.Process<int, int, SumPlus>(4, 2, 0));
    }

    [Fact]
    public void AProcessorCanRentFromTheSamePool()
    {
        using NativeConcurrentPool<int> pool = new();
        Assert.Equal(11, pool.Process<NativeConcurrentPool<int>, int, Nested>(4, 1, pool));
        Assert.Equal(0, pool.GetStatistics().RequestedBytes);
    }

    [Fact]
    public void ARevocationDuringTheProcessorLeavesNothingOutstanding()
    {
        using NativeConcurrentPool<int> pool = new();
        _ = pool.Process<int, int, SumPlus>(4, 1, 0);
        using Handshake handshake = new();
        Thread revoker = new(() =>
        {
            handshake.Entered.Wait();
            _ = pool.GetStatistics();
            handshake.Release.Set();
        });
        revoker.Start();
        Assert.Equal(5, pool.Process<Handshake, int, WaitForRevocation>(4, 5, handshake));
        Assert.True(revoker.Join(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, pool.GetStatistics().RequestedBytes);
        Assert.Equal(4, pool.Process<int, int, SumPlus>(4, 1, 0));
    }

    private sealed class Handshake : IDisposable
    {
        internal ManualResetEventSlim Entered { get; } = new();

        internal ManualResetEventSlim Release { get; } = new();

        public void Dispose()
        {
            Entered.Dispose();
            Release.Dispose();
        }
    }

    private readonly struct SumPlus : INativeSpanProcessor<int, int, int>
    {
        public static int Invoke(Span<int> span, in int state)
        {
            int sum = state;
            foreach (ref readonly int value in span)
            {
                sum += value;
            }

            return sum;
        }
    }

    private readonly struct Thrower : INativeSpanProcessor<int, int, int>
    {
        public static int Invoke(Span<int> span, in int state) => throw new FormatException();
    }

    private readonly struct Nested : INativeSpanProcessor<int, NativeConcurrentPool<int>, int>
    {
        public static int Invoke(Span<int> span, in NativeConcurrentPool<int> state) =>
            state.Process<int, int, SumPlus>(2, 5, 0) + span[0];
    }

    private readonly struct WaitForRevocation : INativeSpanProcessor<int, Handshake, int>
    {
        public static int Invoke(Span<int> span, in Handshake state)
        {
            state.Entered.Set();
            Assert.True(state.Release.Wait(TimeSpan.FromSeconds(10)));
            return span[3];
        }
    }
}
