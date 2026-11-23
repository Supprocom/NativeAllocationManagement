using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement.Performance;

// Code-layout averaging. On CPUs with the JCC-erratum microcode a jump (or fused
// cmp+jcc) that crosses or ends on a 32-byte boundary is slow, so a hot loop's
// speed swings by 1-2 ns with where the JIT happens to place it. A measured loop
// is therefore run as eight copies: copy k is the same generic method
// instantiated over LayoutPad<k>, whose inlined prelude makes k non-inlined
// calls before the loop. The copies are distinct methods (one instantiation per
// pad type), and each pad shifts the loop start by k call instructions, so the
// loops start at different offsets. Report every copy and the average.
//
// Use: implement ILayoutBody as a struct whose Invoke is one measured operation
// (mark it AggressiveInlining), then call LayoutLoop.Warm and LayoutLoop.Measure.
// Run the process with DOTNET_TC_CallCountingDelayMs=0 so the copies tier up.
internal interface ILayoutPad
{
    static abstract void Pad();
}

internal interface ILayoutBody
{
    void Invoke();
}

internal static class LayoutLoop
{
    internal const int Layouts = 8;

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Dummy() { }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static long Run<TPad, TBody>(TBody body, int iterations)
        where TPad : struct, ILayoutPad
        where TBody : struct, ILayoutBody
    {
        TPad.Pad();
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; i++)
        {
            body.Invoke();
        }

        return Stopwatch.GetTimestamp() - start;
    }

    // Ticks for one run of the given copy.
    internal static long RunCopy<TBody>(int copy, TBody body, int iterations)
        where TBody : struct, ILayoutBody =>
        copy switch
        {
            0 => Run<LayoutPad0, TBody>(body, iterations),
            1 => Run<LayoutPad1, TBody>(body, iterations),
            2 => Run<LayoutPad2, TBody>(body, iterations),
            3 => Run<LayoutPad3, TBody>(body, iterations),
            4 => Run<LayoutPad4, TBody>(body, iterations),
            5 => Run<LayoutPad5, TBody>(body, iterations),
            6 => Run<LayoutPad6, TBody>(body, iterations),
            7 => Run<LayoutPad7, TBody>(body, iterations),
            _ => throw new ArgumentOutOfRangeException(nameof(copy))
        };

    // Gets every copy (and what the body calls) to tier 1: rounds of short
    // calls with a pause, so the background tier-up of each stage (with PGO,
    // tier 0, instrumented tier 1, tier 1) can finish between rounds.
    internal static void Warm<TBody>(TBody body, int rounds = 5, int callsPerRound = 40, int iterations = 500, int pauseMilliseconds = 50)
        where TBody : struct, ILayoutBody
    {
        for (int round = 0; round < rounds; round++)
        {
            for (int copy = 0; copy < Layouts; copy++)
            {
                for (int call = 0; call < callsPerRound; call++)
                {
                    RunCopy(copy, body, iterations);
                }
            }

            Thread.Sleep(pauseMilliseconds);
        }
    }

    // Nanoseconds per operation of each copy: the fastest of `repetitions`
    // timed runs, taken in round-robin order so drift touches every copy alike.
    internal static double[] Measure<TBody>(TBody body, int iterations, int repetitions = 3)
        where TBody : struct, ILayoutBody
    {
        double[] best = new double[Layouts];
        Array.Fill(best, double.MaxValue);
        for (int repetition = 0; repetition < repetitions; repetition++)
        {
            for (int copy = 0; copy < Layouts; copy++)
            {
                long ticks = RunCopy(copy, body, iterations);
                best[copy] = Math.Min(best[copy], ticks * 1e9 / Stopwatch.Frequency / iterations);
            }
        }

        return best;
    }
}

internal struct LayoutPad0 : ILayoutPad
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Pad() { }
}

internal struct LayoutPad1 : ILayoutPad
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Pad() { LayoutLoop.Dummy(); }
}

internal struct LayoutPad2 : ILayoutPad
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Pad() { LayoutLoop.Dummy(); LayoutLoop.Dummy(); }
}

internal struct LayoutPad3 : ILayoutPad
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Pad() { LayoutLoop.Dummy(); LayoutLoop.Dummy(); LayoutLoop.Dummy(); }
}

internal struct LayoutPad4 : ILayoutPad
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Pad()
    {
        LayoutLoop.Dummy(); LayoutLoop.Dummy(); LayoutLoop.Dummy(); LayoutLoop.Dummy();
    }
}

internal struct LayoutPad5 : ILayoutPad
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Pad()
    {
        LayoutLoop.Dummy(); LayoutLoop.Dummy(); LayoutLoop.Dummy(); LayoutLoop.Dummy();
        LayoutLoop.Dummy();
    }
}

internal struct LayoutPad6 : ILayoutPad
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Pad()
    {
        LayoutLoop.Dummy(); LayoutLoop.Dummy(); LayoutLoop.Dummy(); LayoutLoop.Dummy();
        LayoutLoop.Dummy(); LayoutLoop.Dummy();
    }
}

internal struct LayoutPad7 : ILayoutPad
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Pad()
    {
        LayoutLoop.Dummy(); LayoutLoop.Dummy(); LayoutLoop.Dummy(); LayoutLoop.Dummy();
        LayoutLoop.Dummy(); LayoutLoop.Dummy(); LayoutLoop.Dummy();
    }
}
