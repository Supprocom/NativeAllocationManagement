using System.Diagnostics.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeOwnerHistoryArithmeticTests
{
    [Fact]
    public void CompletedHistoryAdditionMatchesWideArithmeticAtEveryBoundary()
    {
        long[] values = [0, 1, 2, int.MaxValue, (long)int.MaxValue + 1,
            long.MaxValue / 2, (long.MaxValue / 2) + 1, long.MaxValue - 2,
            long.MaxValue - 1, long.MaxValue];
        foreach (long initial in values)
        {
            foreach (long delta in values)
            {
                VerifyAddition(initial, delta, overflowed: false);
                VerifyAddition(initial, delta, overflowed: true);
            }
        }
    }

    [Fact]
    [SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "A fixed seed reproduces arithmetic test inputs; no security decision or secret depends on them.")]
    public void SeededCompletedHistoriesKeepExactMaximumAndStickyOverflow()
    {
        Random random = new(154018);
        for (int index = 0; index < 10_000; index++)
        {
            long initial = random.NextInt64();
            long delta = random.NextInt64();
            VerifyAddition(initial, delta, overflowed: false);
            VerifyAddition(initial, delta, overflowed: true);
            VerifyAddition(initial, long.MaxValue - initial, overflowed: false);
        }
    }

    private static void VerifyAddition(long initial, long delta, bool overflowed)
    {
        decimal sum = (decimal)initial + delta;
        long expected = sum > long.MaxValue ? long.MaxValue : (long)sum;
        bool expectedOverflow = overflowed || sum > long.MaxValue;
        long actual = initial;
        NativeOwnerHistory.Add(ref actual, delta, ref overflowed);
        Assert.Equal(expected, actual);
        Assert.Equal(expectedOverflow, overflowed);
    }
}
