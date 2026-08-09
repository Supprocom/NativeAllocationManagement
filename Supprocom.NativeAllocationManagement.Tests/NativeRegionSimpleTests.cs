using System.Reflection;
using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeRegionSimpleTests
{
    [Fact]
    public void MixedUnmanagedLeasesPreserveValuesAndAlignment()
    {
        using NativeRegion region = new(
            4_096,
            NativeMemoryReturn.ToNativeMemory);

        Local<byte> bytes = region.Lease<byte>(3, FillBytes);
        Local<int> integers = region.Lease<int>(2, FillIntegers);
        Local<long> longs = region.Lease<long>(2, FillLongs);
        Local<double> doubles = region.Lease<double>(2, FillDoubles);
        Local<SampleCell> cells = region.Lease<SampleCell>(2, FillCells);

        Assert.Equal(6, bytes.Read(static view =>
            view[0] + view[1] + view[2]));
        Assert.Equal(30, integers[0] + integers[1]);
        Assert.Equal(90L, longs[0] + longs[1]);
        Assert.Equal(11d, doubles[0] + doubles[1]);
        Assert.Equal(42L, cells[0].Wide + cells[1].Wide);
        Assert.Equal(2, cells.Length);
        Assert.Equal(cells.Length, cells.Capacity);
    }

    [Fact]
    public void WarmLeaseAccessAndGrowthAllocateNoManagedMemory()
    {
        NativeMemoryTestHooks.Reset();
        using NativeRegion region = new(
            65_536,
            NativeMemoryReturn.ToNativeMemory);
        Local<int> warm = region.Lease<int>(4, FillIntegers);
        _ = warm[0];

        NativeMemoryTestMetrics beforeMetrics =
            NativeMemoryTestHooks.Snapshot();
        long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        long checksum = 0;
        for (int iteration = 0; iteration < 1_000; iteration++)
        {
            Local<int> values = region.Lease<int>(4, FillIntegers);
            checksum += values[0];
            values[1] = iteration;
            checksum += values[1];
        }

        long allocatedBytes =
            GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
        NativeMemoryTestMetrics afterMetrics =
            NativeMemoryTestHooks.Snapshot();

        Assert.Equal(0, allocatedBytes);
        Assert.Equal(
            beforeMetrics.AllocationCount,
            afterMetrics.AllocationCount);
        Assert.Equal(0, region.CurrentAllocationRecordCountForTest);
        Assert.Equal(509_500, checksum);

        using NativeRegion growthRegion = new(
            1,
            NativeMemoryReturn.ToNativeMemory);
        _ = growthRegion.Lease<byte>(1, FillBytes);
        NativeLeaseInitializer<long> longInitializer = FillLongs;
        beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        Local<long> grown = growthRegion.Lease<long>(
            1_024,
            longInitializer);
        allocatedBytes =
            GC.GetAllocatedBytesForCurrentThread() - beforeBytes;

        Assert.Equal(0, allocatedBytes);
        Assert.Equal(30L, grown[0]);
        Assert.True(growthRegion.GetStatistics().SegmentCount >= 2);
    }

    [Fact]
    public void FailedInitializationRewindsTheCurrentCursor()
    {
        NativeMemoryTestHooks.Reset();
        using NativeRegion region = new(
            4_096,
            NativeMemoryReturn.ToNativeMemory);
        NativeOwnerStatistics before = region.GetStatistics();

        Exception? failure = null;
        try
        {
            _ = region.Lease<int>(4, FailAfterOneWrite);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Assert.IsType<InvalidOperationException>(failure);

        Local<int> values = region.Lease<int>(4, FillIntegers);
        NativeOwnerStatistics after = region.GetStatistics();

        Assert.Equal(10, values[0]);
        Assert.Equal(4 * sizeof(int), after.RequestedBytes);
        Assert.Equal(
            before.FreshSegmentAllocationCount,
            after.FreshSegmentAllocationCount);
    }

    [Fact]
    public void FailedZeroLengthInitializationPreservesTheCurrentCursor()
    {
        NativeMemoryTestHooks.Reset();
        using NativeRegion region = new(
            64,
            NativeMemoryReturn.ToNativeMemory);
        Local<int> first = region.Lease<int>(1, FillIntegers);
        NativeOwnerStatistics before = region.GetStatistics();

        Exception? failure = null;
        try
        {
            _ = region.Lease<int>(0, FailWithoutWrite);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Local<int> second = region.Lease<int>(1, FillIntegers);
        NativeOwnerStatistics after = region.GetStatistics();

        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(10, first[0]);
        Assert.Equal(10, second[0]);
        Assert.Equal(
            before.FreshSegmentAllocationCount,
            after.FreshSegmentAllocationCount);
    }

    [Fact]
    public void DisposeInvalidatesAllHandlesAndReleasesTheChainOnce()
    {
        NativeMemoryTestHooks.Reset();
        NativeRegion region = new(
            1,
            NativeMemoryReturn.ToNativeMemory);
        Local<int> first = region.Lease<int>(1, FillIntegers);
        Local<long> second = region.Lease<long>(1_024, FillLongs);
        long freeBefore = NativeMemoryTestHooks.Snapshot().FreeCount;

        region.Dispose();
        long freeAfter = NativeMemoryTestHooks.Snapshot().FreeCount;

        Assert.True(freeAfter >= freeBefore + 2);
        Assert.IsType<NativeAllocationDisposedException>(
            ReadAfterDispose(first));
        Assert.IsType<NativeAllocationDisposedException>(
            ReadAfterDispose(second));

        region.Dispose();
        Assert.Equal(
            freeAfter,
            NativeMemoryTestHooks.Snapshot().FreeCount);
    }

    [Fact]
    public void KernelRejectsAccessFromAnotherThread()
    {
        NativeRegionKernel kernel = new(
            64,
            NativeMemoryReturn.ToNativeMemory);
        Exception? failure = null;
        Thread worker = new(() =>
        {
            try
            {
                kernel.ValidateActive("cross-thread test");
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        Assert.IsType<NativeAllocationStateException>(failure);
        kernel.Dispose();
    }

    [Fact]
    public void RegionSourceContainsNoGeneralAllocatorBookkeeping()
    {
        string source = File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "Supprocom.NativeAllocationManagement",
                "NativeRegionKernel.cs"));

        Assert.DoesNotContain("Dictionary<", source, StringComparison.Ordinal);
        Assert.DoesNotContain("lock (", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Interlocked.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Volatile.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("NativeGeneration", source, StringComparison.Ordinal);
        Assert.DoesNotContain("NativeOwnerKernel", source, StringComparison.Ordinal);
        Assert.DoesNotContain("NativeSegment.Allocate", source, StringComparison.Ordinal);
        Assert.DoesNotContain("List<", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RegionPublicSurfaceHasOneLifetimeAndUnmanagedLeases()
    {
        MethodInfo[] methods = typeof(NativeRegion).GetMethods(
            BindingFlags.Public | BindingFlags.Instance);
        MethodInfo lease = Assert.Single(
            methods,
            method => method.Name == nameof(NativeRegion.Lease));
        Type element = lease.GetGenericArguments()[0];

        Assert.Contains(
            element.GetCustomAttributesData(),
            attribute => attribute.AttributeType.Name
                == "IsUnmanagedAttribute");
        Assert.DoesNotContain(
            methods,
            method => method.Name is
                "LeaseScoped"
                or "RecycleScoped"
                or "LeaseFromMemory"
                or "ReturnMemoryToNativeMemory"
                or "ReturnMemoryToGarbageCollector"
                or "ReserveRetainedMemory"
                or "TrimRetainedMemory"
                or "TrimRetainedMemoryByBytes"
                or "TrimRetainedMemoryByLeaseSize");
    }

    private static void FillBytes(NativeLeaseWriter<byte> writer)
    {
        for (int index = 0; index < writer.Length; index++)
        {
            writer.Write((byte)(index + 1));
        }
    }

    private static void FillIntegers(NativeLeaseWriter<int> writer)
    {
        for (int index = 0; index < writer.Length; index++)
        {
            writer.Write((index + 1) * 10);
        }
    }

    private static void FillLongs(NativeLeaseWriter<long> writer)
    {
        for (int index = 0; index < writer.Length; index++)
        {
            writer.Write((index + 1) * 30L);
        }
    }

    private static void FillDoubles(NativeLeaseWriter<double> writer)
    {
        for (int index = 0; index < writer.Length; index++)
        {
            writer.Write((index + 1) * 11d / 3d);
        }
    }

    private static void FillCells(NativeLeaseWriter<SampleCell> writer)
    {
        for (int index = 0; index < writer.Length; index++)
        {
            writer.Write(new SampleCell(
                (index + 1) * 14L,
                index + 1));
        }
    }

    private static void FailAfterOneWrite(
        NativeLeaseWriter<int> writer)
    {
        writer.Write(1);
        throw new InvalidOperationException(
            "The test initializer failed.");
    }

    private static void FailWithoutWrite(
        NativeLeaseWriter<int> writer) =>
        throw new InvalidOperationException(
            "The test initializer failed before a write.");

    private static Exception ReadAfterDispose(Local<int> local)
    {
        try
        {
            _ = local[0];
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new Xunit.Sdk.XunitException(
            "The disposed Local remained active.");
    }

    private static Exception ReadAfterDispose(Local<long> local)
    {
        try
        {
            _ = local[0];
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new Xunit.Sdk.XunitException(
            "The disposed Local remained active.");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(
                directory.FullName,
                "Supprocom.NativeAllocationManagement.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException(
                "The repository root was not found.");
    }

    private readonly record struct SampleCell(
        long Wide,
        int Narrow);
}
