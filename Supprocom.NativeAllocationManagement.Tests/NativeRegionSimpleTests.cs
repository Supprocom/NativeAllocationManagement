using System.Reflection;
using System.Runtime.InteropServices;
using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeRegionSimpleTests
{
    private static readonly NativeSpanStateProcessor<int, int, int>
        ProcessIteration = static (view, value) =>
        {
            int result = view[0];
            view[1] = value;
            return result + view[1];
        };

    [Fact]
    public void MixedUnmanagedLeasesPreserveValuesAndAlignment()
    {
        using NativeRegion region = new(
            4_096,
            NativeMemoryReturn.Free);

        Local<byte> bytes = region.Lease<byte>(3, FillBytes);
        Local<int> integers = region.Lease<int>(2, FillIntegers);
        Local<long> longs = region.Lease<long>(2, FillLongs);
        Local<double> doubles = region.Lease<double>(2, FillDoubles);
        Local<SampleCell> cells = region.Lease<SampleCell>(2, FillCells);

        Assert.Equal(6, bytes.Read(static view =>
            view[0] + view[1] + view[2]));
        Assert.Equal(30, integers.Read(static view =>
            view[0] + view[1]));
        Assert.Equal(90L, longs.Read(static view =>
            view[0] + view[1]));
        Assert.Equal(11d, doubles.Read(static view =>
            view[0] + view[1]));
        Assert.Equal(42L, cells.Read(static view =>
            view[0].Wide + view[1].Wide));
        Assert.Equal(2, cells.Length);
        Assert.Equal(cells.Length, cells.Capacity);
    }

    [Fact]
    public void WarmLeaseAccessAndGrowthAllocateNoManagedMemory()
    {
        NativeMemoryTestHooks.Reset();
        using NativeRegion region = new(
            65_536,
            NativeMemoryReturn.Free);
        Local<int> warm = region.Lease<int>(4, FillIntegers);
        _ = warm.Read(static view => view[0]);
        _ = warm.Process(0, ProcessIteration);

        NativeMemoryTestMetrics beforeMetrics =
            NativeMemoryTestHooks.Snapshot();
        long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        long checksum = 0;
        for (int iteration = 0; iteration < 1_000; iteration++)
        {
            Local<int> values = region.Lease<int>(4, FillIntegers);
            checksum += values.Process(
                iteration,
                ProcessIteration);
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
            NativeMemoryReturn.Free);
        _ = growthRegion.Lease<byte>(1, FillBytes);
        NativeLeaseInitializer<long> longInitializer = FillLongs;
        beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        Local<long> grown = growthRegion.Lease<long>(
            1_024,
            longInitializer);
        allocatedBytes =
            GC.GetAllocatedBytesForCurrentThread() - beforeBytes;

        Assert.Equal(0, allocatedBytes);
        Assert.Equal(30L, grown.Read(static view => view[0]));
        Assert.True(growthRegion.GetStatistics().SegmentCount >= 2);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "The test intentionally captures arbitrary callback or worker failures for lifecycle assertions.")]
    public void FailedInitializationRewindsTheCurrentCursor()
    {
        NativeMemoryTestHooks.Reset();
        using NativeRegion region = new(
            4_096,
            NativeMemoryReturn.Free);
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

        Assert.Equal(10, values.Read(static view => view[0]));
        Assert.Equal(4 * sizeof(int), after.RequestedBytes);
        Assert.Equal(
            before.FreshSegmentAllocationCount,
            after.FreshSegmentAllocationCount);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "The test intentionally captures arbitrary callback or worker failures for lifecycle assertions.")]
    public void FailedZeroLengthInitializationPreservesTheCurrentCursor()
    {
        NativeMemoryTestHooks.Reset();
        using NativeRegion region = new(
            64,
            NativeMemoryReturn.Free);
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
        Assert.Equal(10, first.Read(static view => view[0]));
        Assert.Equal(10, second.Read(static view => view[0]));
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
            NativeMemoryReturn.Free);
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
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "The test intentionally captures arbitrary callback or worker failures for lifecycle assertions.")]
    public void KernelRejectsAccessFromAnotherThread()
    {
        NativeRegionKernel kernel = new(
            64,
            NativeMemoryReturn.Free);
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
    public void ActiveCallbackRejectsKernelDisposal()
    {
        NativeRegionKernel kernel = new(
            64,
            NativeMemoryReturn.Free);
        Local<int> local = kernel.LeaseInitialized<int>(
            1,
            FillIntegers);

        local.Access(view =>
        {
            NativeAllocationInUseException failure = Assert.Throws<NativeAllocationInUseException>(kernel.Dispose);
            Assert.Equal(1, failure.ActiveOperationCount);
            Assert.Equal(10, view[0]);
        });

        kernel.Dispose();
    }

    [Fact]
    public void CallbackFailureReleasesTheRegionBorrow()
    {
        NativeRegionKernel kernel = new(
            64,
            NativeMemoryReturn.Free);
        Local<int> local = kernel.LeaseInitialized<int>(
            1,
            FillIntegers);

        try
        {
            local.Access(static _ =>
                throw new InvalidOperationException(
                    "The test callback failed."));
            Assert.Fail("The callback failure was not returned.");
        }
        catch (InvalidOperationException)
        {
        }

        kernel.Dispose();
    }

    [Fact]
    public void AccessKeepsKernelAliveDuringForcedCollection()
    {
        Local<int> local = CreateOrphanedLocal(
            out WeakReference<NativeRegionKernel> weak);

        local.Access(view =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Assert.True(weak.TryGetTarget(out _));
            Assert.Equal(10, view[0]);
        });

        Assert.True(weak.TryGetTarget(out NativeRegionKernel? kernel));
        kernel.Dispose();
    }

    [Fact]
    public void ReadKeepsKernelAliveDuringForcedCollection()
    {
        Local<int> local = CreateOrphanedLocal(
            out WeakReference<NativeRegionKernel> weak);

        int value = local.Read(view =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Assert.True(weak.TryGetTarget(out _));
            return view[0];
        });

        Assert.Equal(10, value);
        Assert.True(weak.TryGetTarget(out NativeRegionKernel? kernel));
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
        MethodInfo[] leases = methods
            .Where(method => string.Equals(method.Name, nameof(NativeRegion.Lease), StringComparison.Ordinal))
            .ToArray();

        // The initializer and value-filled acquisitions are the only Lease forms.
        Assert.Equal(2, leases.Length);
        Assert.All(leases, static lease => Assert.Contains(
            lease.GetGenericArguments()[0].GetCustomAttributesData(),
            attribute => string.Equals(attribute.AttributeType.Name
, "IsUnmanagedAttribute", StringComparison.Ordinal)));
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

    private static Local<int> CreateOrphanedLocal(
        out WeakReference<NativeRegionKernel> weak)
    {
        NativeRegionKernel kernel = new(
            64,
            NativeMemoryReturn.Free);
        Local<int> local = kernel.LeaseInitialized<int>(
            1,
            FillIntegers);
        weak = new WeakReference<NativeRegionKernel>(kernel);
        return local;
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

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "The test intentionally captures arbitrary callback or worker failures for lifecycle assertions.")]
    private static Exception ReadAfterDispose(Local<int> local)
    {
        try
        {
            _ = local.Read(static view => view[0]);
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new Xunit.Sdk.XunitException(
            "The disposed Local remained active.");
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "The test intentionally captures arbitrary callback or worker failures for lifecycle assertions.")]
    private static Exception ReadAfterDispose(Local<long> local)
    {
        try
        {
            _ = local.Read(static view => view[0]);
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new Xunit.Sdk.XunitException(
            "The disposed Local remained active.");
    }

    [Fact]
    public void DisposedDefaultRegionKernelServesTheNextRegionWithoutReopeningOldHandles()
    {
        RunOnFreshThread(static () =>
        {
            NativeRegion first = new(preAllocateBytes: 256);
            Local<int> stale = first.Lease<int>(2, FillIntegers);
            long firstId = first.Id;
            first.Dispose();
            Assert.True(NativeRegionKernel.HasReusableKernelForTest);
            Assert.Equal(NativeOwnerLifecycle.Disposed, first.CurrentLifecycle);

            NativeRegion second = new(preAllocateBytes: 128);
            Assert.False(NativeRegionKernel.HasReusableKernelForTest);
            Assert.NotEqual(firstId, second.Id);
            Assert.Equal(0, second.GetStatistics().FreshSegmentAllocationCount);
            Assert.Equal(1, second.GetStatistics().SegmentCount);
            Assert.Equal(0, second.GetStatistics().RequestedBytes);
            Local<int> current = second.Lease<int>(2, FillIntegers);

            // Copies of the earlier lifetime stay disposed after the kernel is reused.
            Assert.IsType<NativeAllocationDisposedException>(ReadAfterDispose(stale));
            Assert.Equal(NativeOwnerLifecycle.Disposed, first.CurrentLifecycle);
            Assert.IsType<NativeAllocationDisposedException>(StatisticsAfterDispose(first));
            first.Dispose();
            Assert.Equal(NativeOwnerLifecycle.Active, second.CurrentLifecycle);
            Assert.Equal(30, current.Read(static view => view[0] + view[1]));
            second.Dispose();
            Assert.True(NativeRegionKernel.HasReusableKernelForTest);
        });
    }

    [Fact]
    public void NativeReturnAndBudgetedRegionsAreNotRetainedForReuse()
    {
        RunOnFreshThread(static () =>
        {
            NativeRegion freed = new(256, NativeMemoryReturn.Free);
            freed.Dispose();
            Assert.False(NativeRegionKernel.HasReusableKernelForTest);

            NativeRegion budgeted = new(new NativeMemoryBudget(65_536), 256, NativeMemoryReturn.Reuse);
            budgeted.Dispose();
            Assert.False(NativeRegionKernel.HasReusableKernelForTest);
        });
    }

    [Fact]
    public void ALargeOrEmptyRegionIsRetainedWithoutStorage()
    {
        RunOnFreshThread(static () =>
        {
            NativeRegion large = new(preAllocateBytes: 2 * 1024 * 1024);
            large.Dispose();
            Assert.True(NativeRegionKernel.HasReusableKernelForTest);
            NativeRegion afterLarge = new();
            Assert.Equal(0, afterLarge.GetStatistics().SegmentCount);
            Assert.Equal(0, afterLarge.GetStatistics().RetainedBytes);
            Assert.Equal(7, afterLarge.Lease<int>(4, 7).Read(static view => view[3]));
            afterLarge.Dispose();

            NativeRegion empty = new();
            NativeRegion nested = new();
            nested.Dispose();
            empty.Dispose();
            Assert.True(NativeRegionKernel.HasReusableKernelForTest);
            NativeRegion afterEmpty = new(preAllocateBytes: 256);
            Assert.Equal(1, afterEmpty.GetStatistics().SegmentCount);
            Assert.Equal(9, afterEmpty.Lease<int>(4, 9).Read(static view => view[3]));
            afterEmpty.Dispose();
        });
    }

    [Fact]
    public void AGrownRegionKeepsItsLargestSegmentForTheNextRegion()
    {
        RunOnFreshThread(static () =>
        {
            NativeRegion grown = new(preAllocateBytes: 64);
            _ = grown.Lease<long>(64, static writer => writer.Fill(1));
            _ = grown.Lease<long>(600, static writer => writer.Fill(2));
            Assert.Equal(3, grown.GetStatistics().SegmentCount);
            grown.Dispose();
            Assert.True(NativeRegionKernel.HasReusableKernelForTest);

            NativeRegion next = new();
            Assert.Equal(1, next.GetStatistics().SegmentCount);
            Local<long> fits = next.Lease<long>(600, static writer => writer.Fill(3));
            Assert.Equal(1, next.GetStatistics().SegmentCount);
            Assert.Equal(0, next.GetStatistics().FreshSegmentAllocationCount);
            Assert.Equal(3, fits.Read(static view => view[599]));
            next.Dispose();
            Assert.True(NativeRegionKernel.HasReusableKernelForTest);

            // A large segment within the limit serves the next region without
            // a fresh allocation.
            NativeRegion large = new(preAllocateBytes: 64);
            _ = large.Lease<long>(64 * 1024, static writer => writer.Fill(4));
            large.Dispose();
            Assert.True(NativeRegionKernel.HasReusableKernelForTest);
            NativeRegion reused = new();
            Local<long> big = reused.Lease<long>(64 * 1024, static writer => writer.Fill(5));
            Assert.Equal(0, reused.GetStatistics().FreshSegmentAllocationCount);
            Assert.Equal(5, big.Read(static view => view[^1]));
            reused.Dispose();

            NativeRegion past = new(preAllocateBytes: 64);
            _ = past.Lease<long>(256 * 1024, static writer => writer.Fill(6));
            past.Dispose();
            Assert.True(NativeRegionKernel.HasReusableKernelForTest);
            NativeRegion after = new();
            Assert.True(after.GetStatistics().RetainedBytes <= 1024 * 1024);
            after.Dispose();
        });
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "The worker failure is rethrown on the test thread.")]
    private static void RunOnFreshThread(Action action)
    {
        // A new thread starts with an empty region reuse slot.
        Exception? failure = null;
        Thread worker = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "The test intentionally captures the stale region failure for assertions.")]
    private static Exception StatisticsAfterDispose(NativeRegion region)
    {
        try
        {
            _ = region.GetStatistics();
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new Xunit.Sdk.XunitException(
            "The disposed region remained observable.");
    }

    private static string FindRepositoryRoot()
        => RepositoryTestPaths.Root;

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct SampleCell(
        long Wide,
        int Narrow);
}
