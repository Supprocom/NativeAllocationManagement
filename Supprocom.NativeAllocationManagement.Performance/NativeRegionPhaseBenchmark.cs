using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement.Performance;

internal static class NativeRegionPhaseBenchmark
{
    private const int ConstructionIterations = 60_000;
    private const int LeaseIterations = 1_000_000;
    private const int InitializationIterations = 1_500_000;
    private const int AccessIterations = 6_000_000;
    private const int GrowthIterations = 4_000;
    private const int DisposalIterations = 120_000;
    private const int CombinedIterations = 131_072;
    private const int CombinedReservationBytes = 320;
    private static readonly NativeLeaseInitializer<int>
        FillIntegers = static writer => writer.Fill(7);
    private static readonly NativeLeaseInitializer<long>
        FillGrowth = static writer => writer.Fill(11);
    private static readonly NativeLeaseInitializer<byte>
        FillBytes = static writer => writer.Fill(3);
    private static readonly NativeLeaseInitializer<long>
        FillLongs = static writer => writer.Fill(7);
    private static readonly NativeLeaseInitializer<double>
        FillDoubles = static writer => writer.Fill(11d);
    private static readonly NativeLeaseInitializer<PhaseCell>
        FillCells = static writer => writer.Fill(
            new PhaseCell(13, 17, 19, 23));
    private static readonly NativeLeaseAction<int>
        FillView = static view => view.Fill(7);

    internal static NativeRegionPhaseReport Run()
    {
        Warm();
        RegionPhaseEvidence[] phases =
        [
            MeasureNewConstruction(),
            MeasureLegacyConstruction(),
            MeasureNewLeasePhase(),
            MeasureLegacyLeasePhase(),
            MeasureNewInitializationPhase(),
            MeasureLegacyInitializationPhase(),
            MeasureNewAccessPhase(),
            MeasureLegacyAccessPhase(),
            MeasureNewGrowthPhase(),
            MeasureLegacyGrowthPhase(),
            MeasureNewDisposal(),
            MeasureLegacyDisposal()
        ];

        RegionPhaseEvidence[] combined =
        [
            Measure(
                "combined",
                "NativeRegion",
                CombinedIterations,
                RunNewCombined),
            Measure(
                "combined",
                "general Region kernel",
                CombinedIterations,
                RunLegacyCombined),
            Measure(
                "combined",
                "ArrayPool<T>.Shared",
                CombinedIterations,
                RunArrayPoolCombined),
            Measure(
                "combined",
                "managed monotonic reference",
                CombinedIterations,
                RunManagedMonotonicCombined)
        ];
        bool exactOutput = combined
            .Select(item => item.Checksum)
            .Distinct()
            .Count() == 1;
        if (!exactOutput)
        {
            throw new InvalidDataException(
                "The Region phase outputs do not match.");
        }

        return new NativeRegionPhaseReport(
            ConstructionIterations,
            LeaseIterations,
            InitializationIterations,
            AccessIterations,
            GrowthIterations,
            DisposalIterations,
            CombinedIterations,
            phases,
            combined,
            exactOutput);
    }

    private static RegionPhaseEvidence MeasureNewConstruction()
    {
        NativeRegionKernel[] kernels =
            new NativeRegionKernel[ConstructionIterations];
        Collect();
        int[] collectionsBefore = CollectionCounts();
        long allocatedBefore =
            GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        for (int index = 0; index < kernels.Length; index++)
        {
            kernels[index] = new NativeRegionKernel(
                64,
                NativeMemoryReturn.ToNativeMemory);
        }

        long elapsed = Stopwatch.GetTimestamp() - start;
        long allocated = GC.GetAllocatedBytesForCurrentThread()
            - allocatedBefore;
        int[] collections = CollectionDeltas(collectionsBefore);
        foreach (NativeRegionKernel kernel in kernels)
        {
            kernel.Dispose();
        }

        return Evidence(
            "construction",
            "NativeRegion",
            ConstructionIterations,
            elapsed,
            allocated,
            collections,
            kernels.Length);
    }

    private static RegionPhaseEvidence MeasureLegacyConstruction()
    {
        NativeOwnerKernel[] kernels =
            new NativeOwnerKernel[ConstructionIterations];
        Collect();
        int[] collectionsBefore = CollectionCounts();
        long allocatedBefore =
            GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        for (int index = 0; index < kernels.Length; index++)
        {
            kernels[index] = CreateLegacyKernel(64);
        }

        long elapsed = Stopwatch.GetTimestamp() - start;
        long allocated = GC.GetAllocatedBytesForCurrentThread()
            - allocatedBefore;
        int[] collections = CollectionDeltas(collectionsBefore);
        foreach (NativeOwnerKernel kernel in kernels)
        {
            kernel.Dispose();
        }

        return Evidence(
            "construction",
            "general Region kernel",
            ConstructionIterations,
            elapsed,
            allocated,
            collections,
            kernels.Length);
    }

    private static RegionPhaseEvidence MeasureNewLeasePhase()
    {
        NativeRegionKernel kernel = new(
            checked((nuint)LeaseIterations * 16u),
            NativeMemoryReturn.ToNativeMemory);
        try
        {
            return Measure(
                "warm lease",
                "NativeRegion",
                LeaseIterations,
                () =>
                {
                    long checksum = 0;
                    for (int index = 0;
                        index < LeaseIterations;
                        index++)
                    {
                        _ = kernel.LeaseInitialized(
                            4,
                            FillIntegers);
                        checksum++;
                    }

                    return checksum;
                });
        }
        finally
        {
            kernel.Dispose();
        }
    }

    private static RegionPhaseEvidence MeasureLegacyLeasePhase()
    {
        NativeOwnerKernel kernel = CreateLegacyKernel(
            checked((nuint)LeaseIterations * 16u));
        try
        {
            return Measure(
                "warm lease",
                "general Region kernel",
                LeaseIterations,
                () =>
                {
                    long checksum = 0;
                    for (int index = 0;
                        index < LeaseIterations;
                        index++)
                    {
                        _ = LeaseLegacy(
                            kernel,
                            4,
                            FillIntegers);
                        checksum++;
                    }

                    return checksum;
                });
        }
        finally
        {
            kernel.Dispose();
        }
    }

    private static RegionPhaseEvidence MeasureNewInitializationPhase()
    {
        NativeRegionKernel kernel = new(
            1_024,
            NativeMemoryReturn.ToNativeMemory);
        Local<int> values = kernel.LeaseInitialized(
            64,
            FillIntegers);
        Collect();
        int[] collectionsBefore = CollectionCounts();
        long allocatedBefore =
            GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        for (int index = 0;
            index < InitializationIterations;
            index++)
        {
            values.Access(FillView);
        }

        long elapsed = Stopwatch.GetTimestamp() - start;
        RegionPhaseEvidence evidence = Evidence(
            "initialization",
            "NativeRegion",
            InitializationIterations,
            elapsed,
            GC.GetAllocatedBytesForCurrentThread()
                - allocatedBefore,
            CollectionDeltas(collectionsBefore),
            values[0]);
        kernel.Dispose();
        return evidence;
    }

    private static RegionPhaseEvidence MeasureLegacyInitializationPhase()
    {
        NativeOwnerKernel kernel = CreateLegacyKernel(1_024);
        try
        {
            NativeRegionAllocation allocation =
                LeaseLegacy(kernel, 64, FillIntegers);
            return Measure(
                "initialization",
                "general Region kernel",
                InitializationIterations,
                () =>
                {
                    for (int index = 0;
                        index < InitializationIterations;
                        index++)
                    {
                        NativeOperationToken token =
                            kernel.EnterOperation(
                                allocation.GenerationState,
                                allocation.AllocationState,
                                allocation.Generation,
                                allocation.AllocationId,
                                "phase initialization");
                        try
                        {
                            token.GetView<int>().Fill(7);
                        }
                        finally
                        {
                            token.Dispose();
                        }
                    }

                    return ReadLegacy<int>(
                        allocation,
                        kernel,
                        0);
                });
        }
        finally
        {
            kernel.Dispose();
        }
    }

    private static RegionPhaseEvidence MeasureNewAccessPhase()
    {
        NativeRegionKernel kernel = new(
            64,
            NativeMemoryReturn.ToNativeMemory);
        Local<int> values = kernel.LeaseInitialized(
            1,
            FillIntegers);
        Collect();
        int[] collectionsBefore = CollectionCounts();
        long allocatedBefore =
            GC.GetAllocatedBytesForCurrentThread();
        long checksum = 0;
        long start = Stopwatch.GetTimestamp();
        for (int index = 0; index < AccessIterations; index++)
        {
            checksum += values[0];
        }

        long elapsed = Stopwatch.GetTimestamp() - start;
        RegionPhaseEvidence evidence = Evidence(
            "bounded access",
            "NativeRegion",
            AccessIterations,
            elapsed,
            GC.GetAllocatedBytesForCurrentThread()
                - allocatedBefore,
            CollectionDeltas(collectionsBefore),
            checksum);
        kernel.Dispose();
        return evidence;
    }

    private static RegionPhaseEvidence MeasureLegacyAccessPhase()
    {
        NativeOwnerKernel kernel = CreateLegacyKernel(64);
        try
        {
            NativeRegionAllocation allocation =
                LeaseLegacy(kernel, 1, FillIntegers);
            return Measure(
                "bounded access",
                "general Region kernel",
                AccessIterations,
                () =>
                {
                    long checksum = 0;
                    for (int index = 0;
                        index < AccessIterations;
                        index++)
                    {
                        _ = kernel.ValidateHandle(
                            allocation.GenerationState,
                            allocation.AllocationState,
                            allocation.Generation,
                            allocation.AllocationId,
                            "phase get_Item");
                        checksum += ReadLegacy<int>(
                            allocation,
                            kernel,
                            0);
                    }

                    return checksum;
                });
        }
        finally
        {
            kernel.Dispose();
        }
    }

    private static RegionPhaseEvidence MeasureNewGrowthPhase()
    {
        NativeRegionKernel[] kernels =
            new NativeRegionKernel[GrowthIterations];
        for (int index = 0; index < kernels.Length; index++)
        {
            kernels[index] = new NativeRegionKernel(
                1,
                NativeMemoryReturn.ToNativeMemory);
        }

        try
        {
            return Measure(
                "segment growth",
                "NativeRegion",
                GrowthIterations,
                () =>
                {
                    long checksum = 0;
                    foreach (NativeRegionKernel kernel in kernels)
                    {
                        _ = kernel.LeaseInitialized(
                            1_024,
                            FillGrowth);
                        checksum++;
                    }

                    return checksum;
                });
        }
        finally
        {
            foreach (NativeRegionKernel kernel in kernels)
            {
                kernel.Dispose();
            }
        }
    }

    private static RegionPhaseEvidence MeasureLegacyGrowthPhase()
    {
        NativeOwnerKernel[] kernels =
            new NativeOwnerKernel[GrowthIterations];
        for (int index = 0; index < kernels.Length; index++)
        {
            kernels[index] = CreateLegacyKernel(1);
        }

        try
        {
            return Measure(
                "segment growth",
                "general Region kernel",
                GrowthIterations,
                () =>
                {
                    long checksum = 0;
                    foreach (NativeOwnerKernel kernel in kernels)
                    {
                        _ = LeaseLegacy(
                            kernel,
                            1_024,
                            FillGrowth);
                        checksum++;
                    }

                    return checksum;
                });
        }
        finally
        {
            foreach (NativeOwnerKernel kernel in kernels)
            {
                kernel.Dispose();
            }
        }
    }

    private static RegionPhaseEvidence MeasureNewDisposal()
    {
        NativeRegionKernel[] kernels =
            new NativeRegionKernel[DisposalIterations];
        for (int index = 0; index < kernels.Length; index++)
        {
            kernels[index] = new NativeRegionKernel(
                64,
                NativeMemoryReturn.ToNativeMemory);
        }

        return MeasurePreparedDisposal(
            "NativeRegion",
            kernels.Length,
            () =>
            {
                foreach (NativeRegionKernel kernel in kernels)
                {
                    kernel.Dispose();
                }

                return kernels.Length;
            });
    }

    private static RegionPhaseEvidence MeasureLegacyDisposal()
    {
        NativeOwnerKernel[] kernels =
            new NativeOwnerKernel[DisposalIterations];
        for (int index = 0; index < kernels.Length; index++)
        {
            kernels[index] = CreateLegacyKernel(64);
        }

        return MeasurePreparedDisposal(
            "general Region kernel",
            kernels.Length,
            () =>
            {
                foreach (NativeOwnerKernel kernel in kernels)
                {
                    kernel.Dispose();
                }

                return kernels.Length;
            });
    }

    private static RegionPhaseEvidence MeasurePreparedDisposal(
        string implementation,
        int operations,
        Func<long> action)
    {
        Collect();
        int[] collectionsBefore = CollectionCounts();
        long allocatedBefore =
            GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        long checksum = action();
        long elapsed = Stopwatch.GetTimestamp() - start;
        return Evidence(
            "disposal",
            implementation,
            operations,
            elapsed,
            GC.GetAllocatedBytesForCurrentThread()
                - allocatedBefore,
            CollectionDeltas(collectionsBefore),
            checksum);
    }

    private static RegionPhaseEvidence Measure(
        string phase,
        string implementation,
        int operations,
        Func<long> action)
    {
        Collect();
        int[] collectionsBefore = CollectionCounts();
        long allocatedBefore =
            GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        long checksum = action();
        long elapsed = Stopwatch.GetTimestamp() - start;
        return Evidence(
            phase,
            implementation,
            operations,
            elapsed,
            GC.GetAllocatedBytesForCurrentThread()
                - allocatedBefore,
            CollectionDeltas(collectionsBefore),
            checksum);
    }

    private static long RunNewLeases()
    {
        using NativeRegion region = new(
            checked((nuint)LeaseIterations * 16u),
            NativeMemoryReturn.ToNativeMemory);
        long checksum = 0;
        for (int index = 0; index < LeaseIterations; index++)
        {
            Local<int> values = region.Lease<int>(4, FillIntegers);
            checksum += values[0];
        }

        return checksum;
    }

    private static long RunLegacyLeases()
    {
        NativeOwnerKernel kernel = CreateLegacyKernel(
            checked((nuint)LeaseIterations * 16u));
        long checksum = 0;
        try
        {
            for (int index = 0; index < LeaseIterations; index++)
            {
                NativeRegionAllocation allocation =
                    LeaseLegacy(kernel, 4, FillIntegers);
                checksum += ReadLegacy<int>(allocation, kernel, 0);
            }

            return checksum;
        }
        finally
        {
            kernel.Dispose();
        }
    }

    private static long RunNewCombined()
    {
        using NativeRegion region = new(
            checked(
                (nuint)CombinedIterations
                * CombinedReservationBytes),
            NativeMemoryReturn.ToNativeMemory);
        long checksum = 0;
        for (int index = 0; index < CombinedIterations; index++)
        {
            Local<byte> bytes = region.Lease<byte>(32, FillBytes);
            Local<int> integers = region.Lease<int>(16, FillIntegers);
            Local<long> longs = region.Lease<long>(8, FillLongs);
            Local<double> doubles = region.Lease<double>(8, FillDoubles);
            Local<PhaseCell> cells = region.Lease<PhaseCell>(4, FillCells);
            checksum += bytes[0]
                + integers[0]
                + longs[0]
                + (long)doubles[0]
                + cells[0].Wide;
        }

        return checksum;
    }

    private static long RunLegacyCombined()
    {
        NativeOwnerKernel kernel = CreateLegacyKernel(
            checked(
                (nuint)CombinedIterations
                * CombinedReservationBytes));
        long checksum = 0;
        try
        {
            for (int index = 0;
                index < CombinedIterations;
                index++)
            {
                checksum += ReadLegacy<byte>(
                    LeaseLegacy(kernel, 32, FillBytes),
                    kernel,
                    0);
                checksum += ReadLegacy<int>(
                    LeaseLegacy(kernel, 16, FillIntegers),
                    kernel,
                    0);
                checksum += ReadLegacy<long>(
                    LeaseLegacy(kernel, 8, FillLongs),
                    kernel,
                    0);
                checksum += (long)ReadLegacy<double>(
                    LeaseLegacy(kernel, 8, FillDoubles),
                    kernel,
                    0);
                checksum += ReadLegacy<PhaseCell>(
                    LeaseLegacy(kernel, 4, FillCells),
                    kernel,
                    0).Wide;
            }

            return checksum;
        }
        finally
        {
            kernel.Dispose();
        }
    }

    private static long RunArrayPoolCombined()
    {
        long checksum = 0;
        for (int index = 0; index < CombinedIterations; index++)
        {
            byte[] bytes = ArrayPool<byte>.Shared.Rent(32);
            int[] integers = ArrayPool<int>.Shared.Rent(16);
            long[] longs = ArrayPool<long>.Shared.Rent(8);
            double[] doubles = ArrayPool<double>.Shared.Rent(8);
            PhaseCell[] cells = ArrayPool<PhaseCell>.Shared.Rent(4);
            bytes.AsSpan(0, 32).Fill(3);
            integers.AsSpan(0, 16).Fill(7);
            longs.AsSpan(0, 8).Fill(7);
            doubles.AsSpan(0, 8).Fill(11d);
            cells.AsSpan(0, 4).Fill(
                new PhaseCell(13, 17, 19, 23));
            checksum += bytes[0]
                + integers[0]
                + longs[0]
                + (long)doubles[0]
                + cells[0].Wide;
            ArrayPool<byte>.Shared.Return(bytes);
            ArrayPool<int>.Shared.Return(integers);
            ArrayPool<long>.Shared.Return(longs);
            ArrayPool<double>.Shared.Return(doubles);
            ArrayPool<PhaseCell>.Shared.Return(cells);
        }

        return checksum;
    }

    private static long RunManagedMonotonicCombined()
    {
        byte[] storage = new byte[
            checked(CombinedIterations * CombinedReservationBytes)];
        int cursor = 0;
        long checksum = 0;
        for (int index = 0; index < CombinedIterations; index++)
        {
            Span<byte> bytes = LeaseManaged<byte>(
                storage,
                ref cursor,
                32);
            Span<int> integers = LeaseManaged<int>(
                storage,
                ref cursor,
                16);
            Span<long> longs = LeaseManaged<long>(
                storage,
                ref cursor,
                8);
            Span<double> doubles = LeaseManaged<double>(
                storage,
                ref cursor,
                8);
            Span<PhaseCell> cells = LeaseManaged<PhaseCell>(
                storage,
                ref cursor,
                4);
            bytes.Fill(3);
            integers.Fill(7);
            longs.Fill(7);
            doubles.Fill(11d);
            cells.Fill(new PhaseCell(13, 17, 19, 23));
            checksum += bytes[0]
                + integers[0]
                + longs[0]
                + (long)doubles[0]
                + cells[0].Wide;
        }

        return checksum;
    }

    private static Span<T> LeaseManaged<T>(
        byte[] storage,
        ref int cursor,
        int length)
        where T : unmanaged
    {
        int alignment = checked((int)NativeTypeLayout.Alignment<T>());
        int aligned = checked(
            (cursor + alignment - 1) & -alignment);
        int byteLength = checked(length * Unsafe.SizeOf<T>());
        cursor = checked(aligned + byteLength);
        return MemoryMarshal.Cast<byte, T>(
            storage.AsSpan(aligned, byteLength));
    }

    private static NativeOwnerKernel CreateLegacyKernel(
        nuint preAllocateBytes) =>
        NativeOwnerKernel.CreateRegion(
            preAllocateBytes,
            "general Region kernel",
            NativeMemoryReturn.ToNativeMemory,
            containsReferences: false,
            doNotLeaseOnDeclaration: false);

    private static NativeRegionAllocation LeaseLegacy<T>(
        NativeOwnerKernel kernel,
        int length,
        NativeLeaseInitializer<T> initializer)
        where T : unmanaged =>
        kernel.LeaseBumpInitialized(
            length,
            Unsafe.SizeOf<T>(),
            NativeTypeLayout.Alignment<T>(),
            scoped: false,
            containsReferences: false,
            initializer);

    private static T ReadLegacy<T>(
        NativeRegionAllocation allocation,
        NativeOwnerKernel kernel,
        int index)
    {
        NativeOperationToken token = kernel.EnterOperation(
            allocation.GenerationState,
            allocation.AllocationState,
            allocation.Generation,
            allocation.AllocationId,
            "phase read");
        try
        {
            return token.GetValue<T>(index);
        }
        finally
        {
            token.Dispose();
        }
    }

    private static RegionPhaseEvidence Evidence(
        string phase,
        string implementation,
        int operations,
        long elapsedTicks,
        long allocatedBytes,
        int[] collections,
        long checksum) =>
        new(
            phase,
            implementation,
            operations,
            elapsedTicks * 1_000d / Stopwatch.Frequency,
            allocatedBytes,
            collections[0],
            collections[1],
            collections[2],
            checksum);

    private static int[] CollectionCounts() =>
    [
        GC.CollectionCount(0),
        GC.CollectionCount(1),
        GC.CollectionCount(2)
    ];

    private static int[] CollectionDeltas(int[] before) =>
    [
        GC.CollectionCount(0) - before[0],
        GC.CollectionCount(1) - before[1],
        GC.CollectionCount(2) - before[2]
    ];

    private static void Collect() => GC.Collect(
        2,
        GCCollectionMode.Forced,
        blocking: true,
        compacting: true);

    private static void Warm()
    {
        _ = RunNewLeases();
        _ = RunLegacyLeases();
        _ = RunNewCombined();
        _ = RunLegacyCombined();
        _ = RunArrayPoolCombined();
        _ = RunManagedMonotonicCombined();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly record struct PhaseCell(
        long Wide,
        int Narrow,
        short Small,
        byte Flag);
}

internal sealed record NativeRegionPhaseReport(
    int ConstructionIterations,
    int LeaseIterations,
    int InitializationIterations,
    int AccessIterations,
    int GrowthIterations,
    int DisposalIterations,
    int CombinedIterations,
    RegionPhaseEvidence[] Phases,
    RegionPhaseEvidence[] Combined,
    bool ExactOutput);

internal sealed record RegionPhaseEvidence(
    string Phase,
    string Implementation,
    int Operations,
    double ElapsedMilliseconds,
    long ManagedAllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    long Checksum);
