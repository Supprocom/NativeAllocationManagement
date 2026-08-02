using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement.Performance;

internal static class AllocatorPerformanceRegression
{
    internal const double RegionMinimumSpeedup = 1.50d;
    private const int RegionIterations = 512;
    private const int RegionBatchWidth = 128;
    private const int SampleCount = 8;
    private const int MaximumSampleAttempts = 3;
    private const int RegionReservationBytesPerIteration = 320;
    private const int RegionLogicalBytesPerIteration = 284;
    private static readonly NativeLeaseFunc<byte, ulong>
        ByteChecksum = static view => Checksum(view.AsSpan());
    private static readonly NativeLeaseFunc<int, ulong>
        IntChecksum = static view => Checksum(view.AsSpan());
    private static readonly NativeLeaseFunc<long, ulong>
        LongChecksum = static view => Checksum(view.AsSpan());
    private static readonly NativeLeaseFunc<double, ulong>
        DoubleChecksum = static view => Checksum(view.AsSpan());
    private static readonly NativeLeaseFunc<MixedCell, ulong>
        CellChecksum = static view => Checksum(view.AsSpan());
    private static long _sink;

    internal static RegionRegressionReport RunRegion()
    {
        WarmRegion();
        WarmArrayPools();
        GC.Collect(
            2,
            GCCollectionMode.Forced,
            blocking: true,
            compacting: true);

        RegionPairEvidence[] pairs =
            new RegionPairEvidence[SampleCount];
        for (int sample = 0; sample < SampleCount; sample++)
        {
            bool arrayPoolFirst = (sample & 1) == 0;
            RegionSampleEvidence first = arrayPoolFirst
                ? MeasureAccepted(MeasureArrayPools)
                : MeasureAccepted(MeasureRegion);
            RegionSampleEvidence second = arrayPoolFirst
                ? MeasureAccepted(MeasureRegion)
                : MeasureAccepted(MeasureArrayPools);
            RegionSampleEvidence arrayPool = arrayPoolFirst
                ? first
                : second;
            RegionSampleEvidence region = arrayPoolFirst
                ? second
                : first;
            if (arrayPool.Checksum != region.Checksum
                || arrayPool.LogicalBytes != region.LogicalBytes)
            {
                throw new InvalidDataException(
                    "The Region regression output does not match the ArrayPool output.");
            }

            pairs[sample] = new RegionPairEvidence(
                sample,
                arrayPoolFirst ? "ArrayPool-Region" : "Region-ArrayPool",
                arrayPool,
                region,
                arrayPool.ElapsedMilliseconds
                    / region.ElapsedMilliseconds);
        }

        double[] ratios = pairs
            .Select(pair => pair.Speedup)
            .Order()
            .ToArray();
        double median = Median(ratios);
        double arrayPoolTotal = pairs.Sum(
            pair => pair.ArrayPool.ElapsedMilliseconds);
        double regionTotal = pairs.Sum(
            pair => pair.Region.ElapsedMilliseconds);
        double aggregate = arrayPoolTotal / regionTotal;
        bool passed = median >= RegionMinimumSpeedup
            && pairs.All(pair =>
                pair.ArrayPool.Gen0Collections == 0
                && pair.ArrayPool.Gen1Collections == 0
                && pair.ArrayPool.Gen2Collections == 0
                && pair.Region.Gen0Collections == 0
                && pair.Region.Gen1Collections == 0
                && pair.Region.Gen2Collections == 0
                && pair.Region.ManagedAllocatedBytes == 0
                && pair.Region.FreshSegmentCount == 0);
        Volatile.Write(
            ref _sink,
            pairs.Aggregate(
                0L,
                (value, pair) => unchecked(
                    value + pair.Region.Checksum)));
        return new RegionRegressionReport(
            RegionIterations,
            SampleCount,
            pairs,
            median,
            aggregate,
            arrayPoolTotal,
            regionTotal,
            RegionMinimumSpeedup,
            passed,
            Environment.GetEnvironmentVariable(
                "DOTNET_TieredCompilation") ?? "unset",
            Environment.GetEnvironmentVariable(
                "DOTNET_TieredPGO") ?? "unset");
    }

    private static RegionSampleEvidence MeasureAccepted(
        Func<RegionSampleEvidence> measurement)
    {
        for (int attempt = 1;
            attempt <= MaximumSampleAttempts;
            attempt++)
        {
            RegionSampleEvidence evidence = measurement();
            if (evidence.Gen0Collections == 0
                && evidence.Gen1Collections == 0
                && evidence.Gen2Collections == 0)
            {
                return evidence with { Attempt = attempt };
            }
        }

        throw new InvalidOperationException(
            "The Region regression could not get a sample without garbage collection.");
    }

    private static RegionSampleEvidence MeasureRegion()
    {
        nuint reservation = checked(
            (nuint)(RegionIterations
                * RegionBatchWidth
                * RegionReservationBytesPerIteration));
        NativeRegion region = new(
            reservation,
            NativeMemoryReturn.ToNativeMemory);
        NativeOwnerStatistics beforeStatistics =
            region.GetStatistics();
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        long allocatedBefore =
            GC.GetAllocatedBytesForCurrentThread();
        long checksum = 0;
        long start = Stopwatch.GetTimestamp();
        for (int iteration = 0;
            iteration < RegionIterations;
            iteration++)
        {
            for (int slot = 0; slot < RegionBatchWidth; slot++)
            {
                Local<byte> bytes = region.Lease<byte>(
                    32,
                    FillBytes);
                Local<int> integers = region.Lease<int>(
                    16,
                    FillIntegers);
                Local<long> longs = region.Lease<long>(
                    8,
                    FillLongs);
                Local<double> doubles = region.Lease<double>(
                    8,
                    FillDoubles);
                Local<MixedCell> cells = region.Lease<MixedCell>(
                    4,
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

        long elapsedTicks = Stopwatch.GetTimestamp() - start;
        long managedAllocated =
            GC.GetAllocatedBytesForCurrentThread()
            - allocatedBefore;
        NativeOwnerStatistics afterStatistics =
            region.GetStatistics();
        int gen0 = GC.CollectionCount(0) - gen0Before;
        int gen1 = GC.CollectionCount(1) - gen1Before;
        int gen2 = GC.CollectionCount(2) - gen2Before;
        region.Dispose();
        return new RegionSampleEvidence(
            "NativeRegion",
            Milliseconds(elapsedTicks),
            managedAllocated,
            gen0,
            gen1,
            gen2,
            afterStatistics.FreshSegmentAllocationCount
                - beforeStatistics.FreshSegmentAllocationCount,
            checksum,
            checked(
                (long)RegionIterations
                * RegionBatchWidth
                * RegionLogicalBytesPerIteration),
            Attempt: 1);
    }

    private static RegionSampleEvidence MeasureArrayPools()
    {
        ArrayPool<byte> bytePool = ArrayPool<byte>.Shared;
        ArrayPool<int> intPool = ArrayPool<int>.Shared;
        ArrayPool<long> longPool = ArrayPool<long>.Shared;
        ArrayPool<double> doublePool = ArrayPool<double>.Shared;
        ArrayPool<MixedCell> cellPool = ArrayPool<MixedCell>.Shared;
        byte[][] byteArrays = new byte[RegionBatchWidth][];
        int[][] intArrays = new int[RegionBatchWidth][];
        long[][] longArrays = new long[RegionBatchWidth][];
        double[][] doubleArrays = new double[RegionBatchWidth][];
        MixedCell[][] cellArrays = new MixedCell[RegionBatchWidth][];
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        long allocatedBefore =
            GC.GetAllocatedBytesForCurrentThread();
        long checksum = 0;
        long start = Stopwatch.GetTimestamp();
        for (int iteration = 0;
            iteration < RegionIterations;
            iteration++)
        {
            for (int slot = 0; slot < RegionBatchWidth; slot++)
            {
                byte[] byteArray = bytePool.Rent(32);
                int[] intArray = intPool.Rent(16);
                long[] longArray = longPool.Rent(8);
                double[] doubleArray = doublePool.Rent(8);
                MixedCell[] cellArray = cellPool.Rent(4);
                byteArrays[slot] = byteArray;
                intArrays[slot] = intArray;
                longArrays[slot] = longArray;
                doubleArrays[slot] = doubleArray;
                cellArrays[slot] = cellArray;
                Span<byte> bytes = byteArray.AsSpan(0, 32);
                Span<int> integers = intArray.AsSpan(0, 16);
                Span<long> longs = longArray.AsSpan(0, 8);
                Span<double> doubles = doubleArray.AsSpan(0, 8);
                Span<MixedCell> cells = cellArray.AsSpan(0, 4);
                bytes.Fill(3);
                integers.Fill(5);
                longs.Fill(7);
                doubles.Fill(11.5d);
                cells.Fill(new MixedCell(
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

            for (int slot = 0; slot < RegionBatchWidth; slot++)
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
        return new RegionSampleEvidence(
            "ArrayPool",
            Milliseconds(elapsedTicks),
            GC.GetAllocatedBytesForCurrentThread()
                - allocatedBefore,
            GC.CollectionCount(0) - gen0Before,
            GC.CollectionCount(1) - gen1Before,
            GC.CollectionCount(2) - gen2Before,
            FreshSegmentCount: 0,
            checksum,
            checked(
                (long)RegionIterations
                * RegionBatchWidth
                * RegionLogicalBytesPerIteration),
            Attempt: 1);
    }

    private static void WarmRegion()
    {
        NativeRegion region = new(
            32_000,
            NativeMemoryReturn.ToNativeMemory);
        for (int iteration = 0; iteration < 32; iteration++)
        {
            Local<byte> bytes = region.Lease<byte>(32, FillBytes);
            Local<int> integers = region.Lease<int>(16, FillIntegers);
            Local<long> longs = region.Lease<long>(8, FillLongs);
            Local<double> doubles = region.Lease<double>(8, FillDoubles);
            Local<MixedCell> cells = region.Lease<MixedCell>(4, FillCells);
            _ = bytes.Read(ByteChecksum);
            _ = integers.Read(IntChecksum);
            _ = longs.Read(LongChecksum);
            _ = doubles.Read(DoubleChecksum);
            _ = cells.Read(CellChecksum);
        }

        region.Dispose();
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
        byte[][] bytes = new byte[RegionBatchWidth][];
        int[][] integers = new int[RegionBatchWidth][];
        long[][] longs = new long[RegionBatchWidth][];
        double[][] doubles = new double[RegionBatchWidth][];
        MixedCell[][] cells = new MixedCell[RegionBatchWidth][];
        for (int slot = 0; slot < RegionBatchWidth; slot++)
        {
            bytes[slot] = ArrayPool<byte>.Shared.Rent(32);
            integers[slot] = ArrayPool<int>.Shared.Rent(16);
            longs[slot] = ArrayPool<long>.Shared.Rent(8);
            doubles[slot] = ArrayPool<double>.Shared.Rent(8);
            cells[slot] = ArrayPool<MixedCell>.Shared.Rent(4);
        }

        for (int slot = 0; slot < RegionBatchWidth; slot++)
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
            ArrayPool<MixedCell>.Shared.Return(
                cells[slot],
                clearArray: false);
        }
    }

    private static void FillBytes(NativeLeaseWriter<byte> writer) =>
        writer.Fill(3);

    private static void FillIntegers(
        NativeLeaseWriter<int> writer) =>
        writer.Fill(5);

    private static void FillLongs(NativeLeaseWriter<long> writer) =>
        writer.Fill(7);

    private static void FillDoubles(
        NativeLeaseWriter<double> writer) =>
        writer.Fill(11.5d);

    private static void FillCells(
        NativeLeaseWriter<MixedCell> writer) =>
        writer.Fill(new MixedCell(
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

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly record struct MixedCell(
        long Wide,
        int Narrow,
        short Small,
        byte Flag);
}

internal sealed record RegionRegressionReport(
    int Iterations,
    int SampleCount,
    RegionPairEvidence[] Pairs,
    double MedianSpeedup,
    double AggregateSpeedup,
    double ArrayPoolElapsedMilliseconds,
    double RegionElapsedMilliseconds,
    double MinimumSpeedup,
    bool Passed,
    string TieredCompilation,
    string TieredPgo);

internal sealed record RegionPairEvidence(
    int Sample,
    string Order,
    RegionSampleEvidence ArrayPool,
    RegionSampleEvidence Region,
    double Speedup);

internal sealed record RegionSampleEvidence(
    string Implementation,
    double ElapsedMilliseconds,
    long ManagedAllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    long FreshSegmentCount,
    long Checksum,
    long LogicalBytes,
    int Attempt);
