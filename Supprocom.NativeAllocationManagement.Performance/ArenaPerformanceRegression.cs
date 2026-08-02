using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement.Performance;

internal static class ArenaPerformanceRegression
{
    internal const double MinimumSpeedup = 1.50d;
    private const int Iterations = 1_024;
    private const int BatchWidth = 128;
    private const int SampleCount = 8;
    private const int MaximumSampleAttempts = 3;
    private const int ReservationBytesPerSlot = 80;
    private const int LogicalBytesPerSlot = 71;
    private static readonly NativeLeaseFunc<byte, ulong>
        ByteChecksum = static view => Checksum(view.AsSpan());
    private static readonly NativeLeaseFunc<int, ulong>
        IntChecksum = static view => Checksum(view.AsSpan());
    private static readonly NativeLeaseFunc<long, ulong>
        LongChecksum = static view => Checksum(view.AsSpan());
    private static readonly NativeLeaseFunc<double, ulong>
        DoubleChecksum = static view => Checksum(view.AsSpan());
    private static readonly NativeLeaseFunc<ArenaMixedCell, ulong>
        CellChecksum = static view => Checksum(view.AsSpan());
    private static long _sink;

    internal static ArenaRegressionReport Run()
    {
        WarmArena();
        WarmArrayPools();
        GC.Collect(
            2,
            GCCollectionMode.Forced,
            blocking: true,
            compacting: true);

        ArenaPairEvidence[] pairs =
            new ArenaPairEvidence[SampleCount];
        for (int sample = 0; sample < SampleCount; sample++)
        {
            bool arrayPoolFirst = (sample & 1) == 0;
            ArenaSampleEvidence first = arrayPoolFirst
                ? MeasureAccepted(MeasureArrayPools)
                : MeasureAccepted(MeasureArena);
            ArenaSampleEvidence second = arrayPoolFirst
                ? MeasureAccepted(MeasureArena)
                : MeasureAccepted(MeasureArrayPools);
            ArenaSampleEvidence arrayPool = arrayPoolFirst
                ? first
                : second;
            ArenaSampleEvidence arena = arrayPoolFirst
                ? second
                : first;
            if (arrayPool.Checksum != arena.Checksum
                || arrayPool.LogicalBytes != arena.LogicalBytes)
            {
                throw new InvalidDataException(
                    "The Arena regression output does not match the ArrayPool output.");
            }

            pairs[sample] = new ArenaPairEvidence(
                sample,
                arrayPoolFirst ? "ArrayPool-Arena" : "Arena-ArrayPool",
                arrayPool,
                arena,
                arrayPool.ElapsedMilliseconds
                    / arena.ElapsedMilliseconds);
        }

        double[] ratios = pairs
            .Select(pair => pair.Speedup)
            .Order()
            .ToArray();
        double median = Median(ratios);
        double arrayPoolTotal = pairs.Sum(
            pair => pair.ArrayPool.ElapsedMilliseconds);
        double arenaTotal = pairs.Sum(
            pair => pair.Arena.ElapsedMilliseconds);
        double aggregate = arrayPoolTotal / arenaTotal;
        bool passed = median >= MinimumSpeedup
            && pairs.All(pair =>
                pair.ArrayPool.Accepted
                && pair.Arena.Accepted
                && pair.Arena.FreshSegmentCount == 0);
        Volatile.Write(
            ref _sink,
            pairs.Aggregate(
                0L,
                (value, pair) => unchecked(
                    value + pair.Arena.Checksum)));
        return new ArenaRegressionReport(
            Iterations,
            SampleCount,
            pairs,
            median,
            aggregate,
            arrayPoolTotal,
            arenaTotal,
            MinimumSpeedup,
            passed,
            Environment.GetEnvironmentVariable(
                "DOTNET_TieredCompilation") ?? "unset",
            Environment.GetEnvironmentVariable(
                "DOTNET_TieredPGO") ?? "unset");
    }

    private static ArenaSampleEvidence MeasureAccepted(
        Func<ArenaSampleEvidence> measurement)
    {
        ArenaSampleEvidence last = default;
        for (int attempt = 1;
            attempt <= MaximumSampleAttempts;
            attempt++)
        {
            ArenaSampleEvidence evidence = measurement()
                with { Attempt = attempt };
            last = evidence;
            if (evidence.Gen0Collections == 0
                && evidence.Gen1Collections == 0
                && evidence.Gen2Collections == 0)
            {
                return evidence with { Accepted = true };
            }
        }

        return last with { Accepted = false };
    }

    private static ArenaSampleEvidence MeasureArena()
    {
        nuint reservation = checked(
            (nuint)(Iterations
                * BatchWidth
                * ReservationBytesPerSlot));
        NativeArena arena = new(
            reservation,
            NativeMemoryReturn.ToNativeMemory);
        WarmArena(arena);
        arena.ReleaseLeasesToNativeMemory();
        NativeOwnerStatistics beforeStatistics =
            arena.GetStatistics();
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        long allocatedBefore =
            GC.GetAllocatedBytesForCurrentThread();
        long checksum = 0;
        long start = Stopwatch.GetTimestamp();
        for (int iteration = 0;
            iteration < Iterations;
            iteration++)
        {
            for (int slot = 0; slot < BatchWidth; slot++)
            {
                ArenaLease<byte> bytes = arena.Scratch<byte>(
                    8,
                    FillBytes);
                ArenaLease<int> integers = arena.Scratch<int>(
                    4,
                    FillIntegers);
                ArenaLease<long> longs = arena.Scratch<long>(
                    2,
                    FillLongs);
                ArenaLease<double> doubles = arena.Scratch<double>(
                    2,
                    FillDoubles);
                ArenaLease<ArenaMixedCell> cells =
                    arena.Scratch<ArenaMixedCell>(
                        1,
                        FillCells);
                checksum = unchecked(
                    checksum
                    + (long)bytes.Read(ByteChecksum)
                    + (long)integers.Read(IntChecksum)
                    + (long)longs.Read(LongChecksum)
                    + (long)doubles.Read(DoubleChecksum)
                    + (long)cells.Read(CellChecksum));
            }
        }

        arena.ReleaseLeasesToNativeMemory();
        long elapsedTicks = Stopwatch.GetTimestamp() - start;
        long managedAllocated =
            GC.GetAllocatedBytesForCurrentThread()
            - allocatedBefore;
        NativeOwnerStatistics afterStatistics =
            arena.GetStatistics();
        int gen0 = GC.CollectionCount(0) - gen0Before;
        int gen1 = GC.CollectionCount(1) - gen1Before;
        int gen2 = GC.CollectionCount(2) - gen2Before;
        arena.Dispose();
        return new ArenaSampleEvidence(
            "NativeArena",
            Milliseconds(elapsedTicks),
            managedAllocated,
            gen0,
            gen1,
            gen2,
            afterStatistics.FreshSegmentAllocationCount
                - beforeStatistics.FreshSegmentAllocationCount,
            checksum,
            checked(
                (long)Iterations
                * BatchWidth
                * LogicalBytesPerSlot),
            Attempt: 1,
            Accepted: false);
    }

    private static ArenaSampleEvidence MeasureArrayPools()
    {
        ArrayPool<byte> bytePool = ArrayPool<byte>.Shared;
        ArrayPool<int> intPool = ArrayPool<int>.Shared;
        ArrayPool<long> longPool = ArrayPool<long>.Shared;
        ArrayPool<double> doublePool = ArrayPool<double>.Shared;
        ArrayPool<ArenaMixedCell> cellPool =
            ArrayPool<ArenaMixedCell>.Shared;
        byte[][] byteArrays = new byte[BatchWidth][];
        int[][] intArrays = new int[BatchWidth][];
        long[][] longArrays = new long[BatchWidth][];
        double[][] doubleArrays = new double[BatchWidth][];
        ArenaMixedCell[][] cellArrays =
            new ArenaMixedCell[BatchWidth][];
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        long allocatedBefore =
            GC.GetAllocatedBytesForCurrentThread();
        long checksum = 0;
        long start = Stopwatch.GetTimestamp();
        for (int iteration = 0;
            iteration < Iterations;
            iteration++)
        {
            for (int slot = 0; slot < BatchWidth; slot++)
            {
                byte[] byteArray = bytePool.Rent(8);
                int[] intArray = intPool.Rent(4);
                long[] longArray = longPool.Rent(2);
                double[] doubleArray = doublePool.Rent(2);
                ArenaMixedCell[] cellArray = cellPool.Rent(1);
                byteArrays[slot] = byteArray;
                intArrays[slot] = intArray;
                longArrays[slot] = longArray;
                doubleArrays[slot] = doubleArray;
                cellArrays[slot] = cellArray;
                Span<byte> bytes = byteArray.AsSpan(0, 8);
                Span<int> integers = intArray.AsSpan(0, 4);
                Span<long> longs = longArray.AsSpan(0, 2);
                Span<double> doubles = doubleArray.AsSpan(0, 2);
                Span<ArenaMixedCell> cells =
                    cellArray.AsSpan(0, 1);
                bytes.Fill(3);
                integers.Fill(5);
                longs.Fill(7);
                doubles.Fill(11.5d);
                cells.Fill(new ArenaMixedCell(
                    13,
                    17,
                    19,
                    23));
                checksum = unchecked(
                    checksum
                    + (long)Checksum(bytes)
                    + (long)Checksum(integers)
                    + (long)Checksum(longs)
                    + (long)Checksum(doubles)
                    + (long)Checksum(cells));
            }

            for (int slot = 0; slot < BatchWidth; slot++)
            {
                bytePool.Return(
                    byteArrays[slot],
                    clearArray: false);
                intPool.Return(
                    intArrays[slot],
                    clearArray: false);
                longPool.Return(
                    longArrays[slot],
                    clearArray: false);
                doublePool.Return(
                    doubleArrays[slot],
                    clearArray: false);
                cellPool.Return(
                    cellArrays[slot],
                    clearArray: false);
            }
        }

        long elapsedTicks = Stopwatch.GetTimestamp() - start;
        int gen0 = GC.CollectionCount(0) - gen0Before;
        int gen1 = GC.CollectionCount(1) - gen1Before;
        int gen2 = GC.CollectionCount(2) - gen2Before;
        return new ArenaSampleEvidence(
            "ArrayPool",
            Milliseconds(elapsedTicks),
            GC.GetAllocatedBytesForCurrentThread()
                - allocatedBefore,
            gen0,
            gen1,
            gen2,
            FreshSegmentCount: 0,
            checksum,
            checked(
                (long)Iterations
                * BatchWidth
                * LogicalBytesPerSlot),
            Attempt: 1,
            Accepted: gen0 == 0 && gen1 == 0 && gen2 == 0);
    }

    private static void WarmArena()
    {
        using NativeArena arena = new(
            32_000,
            NativeMemoryReturn.ToNativeMemory);
        WarmArena(arena);
    }

    private static void WarmArena(NativeArena arena)
    {
        for (int iteration = 0; iteration < 32; iteration++)
        {
            ArenaLease<byte> bytes =
                arena.Scratch<byte>(8, FillBytes);
            ArenaLease<int> integers =
                arena.Scratch<int>(4, FillIntegers);
            ArenaLease<long> longs =
                arena.Scratch<long>(2, FillLongs);
            ArenaLease<double> doubles =
                arena.Scratch<double>(2, FillDoubles);
            ArenaLease<ArenaMixedCell> cells =
                arena.Scratch<ArenaMixedCell>(1, FillCells);
            _ = bytes.Read(ByteChecksum);
            _ = integers.Read(IntChecksum);
            _ = longs.Read(LongChecksum);
            _ = doubles.Read(DoubleChecksum);
            _ = cells.Read(CellChecksum);
        }
    }

    private static void WarmArrayPools()
    {
        for (int iteration = 0; iteration < 16; iteration++)
        {
            WarmArrayPoolBatch();
        }
    }

    private static void WarmArrayPoolBatch()
    {
        byte[][] bytes = new byte[BatchWidth][];
        int[][] integers = new int[BatchWidth][];
        long[][] longs = new long[BatchWidth][];
        double[][] doubles = new double[BatchWidth][];
        ArenaMixedCell[][] cells =
            new ArenaMixedCell[BatchWidth][];
        for (int slot = 0; slot < BatchWidth; slot++)
        {
            bytes[slot] = ArrayPool<byte>.Shared.Rent(8);
            integers[slot] = ArrayPool<int>.Shared.Rent(4);
            longs[slot] = ArrayPool<long>.Shared.Rent(2);
            doubles[slot] = ArrayPool<double>.Shared.Rent(2);
            cells[slot] =
                ArrayPool<ArenaMixedCell>.Shared.Rent(1);
        }

        for (int slot = 0; slot < BatchWidth; slot++)
        {
            ArrayPool<byte>.Shared.Return(
                bytes[slot],
                clearArray: false);
            ArrayPool<int>.Shared.Return(
                integers[slot],
                clearArray: false);
            ArrayPool<long>.Shared.Return(
                longs[slot],
                clearArray: false);
            ArrayPool<double>.Shared.Return(
                doubles[slot],
                clearArray: false);
            ArrayPool<ArenaMixedCell>.Shared.Return(
                cells[slot],
                clearArray: false);
        }
    }

    private static void FillBytes(
        NativeLeaseWriter<byte> writer) => writer.Fill(3);

    private static void FillIntegers(
        NativeLeaseWriter<int> writer) => writer.Fill(5);

    private static void FillLongs(
        NativeLeaseWriter<long> writer) => writer.Fill(7);

    private static void FillDoubles(
        NativeLeaseWriter<double> writer) => writer.Fill(11.5d);

    private static void FillCells(
        NativeLeaseWriter<ArenaMixedCell> writer) =>
        writer.Fill(new ArenaMixedCell(
            13,
            17,
            19,
            23));

    private static ulong Checksum<T>(ReadOnlySpan<T> values)
        where T : unmanaged
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(values);
        ulong hash = 14_695_981_039_346_656_037UL;
        foreach (byte value in bytes)
        {
            hash ^= value;
            hash *= 1_099_511_628_211UL;
        }

        return hash;
    }

    private static double Median(double[] sortedValues)
    {
        int middle = sortedValues.Length / 2;
        return (sortedValues.Length & 1) == 0
            ? (sortedValues[middle - 1]
                + sortedValues[middle]) / 2d
            : sortedValues[middle];
    }

    private static double Milliseconds(long ticks) =>
        ticks * 1_000d / Stopwatch.Frequency;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly record struct ArenaMixedCell(
    long Wide,
    int Narrow,
    short Small,
    byte Flag);

internal sealed record ArenaRegressionReport(
    int Iterations,
    int SampleCount,
    ArenaPairEvidence[] Pairs,
    double MedianSpeedup,
    double AggregateSpeedup,
    double ArrayPoolElapsedMilliseconds,
    double ArenaElapsedMilliseconds,
    double MinimumSpeedup,
    bool Passed,
    string TieredCompilation,
    string TieredPgo);

internal sealed record ArenaPairEvidence(
    int Sample,
    string Order,
    ArenaSampleEvidence ArrayPool,
    ArenaSampleEvidence Arena,
    double Speedup);

internal readonly record struct ArenaSampleEvidence(
    string Implementation,
    double ElapsedMilliseconds,
    long ManagedAllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    long FreshSegmentCount,
    long Checksum,
    long LogicalBytes,
    int Attempt,
    bool Accepted);
