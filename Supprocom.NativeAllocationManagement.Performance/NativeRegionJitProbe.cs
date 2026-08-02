using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement.Performance;

internal static class NativeRegionJitProbe
{
    private static readonly NativeLeaseInitializer<int>
        Initializer = static writer => writer.Fill(7);

    internal static int Run()
    {
        NativeRegionKernel region = new(
            4_096,
            NativeMemoryReturn.ToNativeMemory);
        NativeOwnerKernel legacy =
            NativeOwnerKernel.CreateRegion(
                4_096,
                "general Region kernel",
                NativeMemoryReturn.ToNativeMemory,
                containsReferences: false,
                doNotLeaseOnDeclaration: false);
        try
        {
            Local<int> local = region.LeaseInitialized(
                1,
                Initializer);
            NativeRegionAllocation allocation =
                legacy.LeaseBumpInitialized(
                    1,
                    sizeof(int),
                    4,
                    scoped: false,
                    containsReferences: false,
                    Initializer);
            return ProbeNewAccess(local)
                + ProbeLegacyAccess(legacy, allocation)
                + ProbeNewLease(region)
                + ProbeLegacyLease(legacy);
        }
        finally
        {
            region.Dispose();
            legacy.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ProbeNewAccess(scoped Local<int> local) =>
        local[0];

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ProbeLegacyAccess(
        NativeOwnerKernel kernel,
        NativeRegionAllocation allocation)
    {
        _ = kernel.ValidateHandle(
            allocation.GenerationState,
            allocation.AllocationState,
            allocation.Generation,
            allocation.AllocationId,
            "JIT access probe");
        NativeOperationToken token = kernel.EnterOperation(
            allocation.GenerationState,
            allocation.AllocationState,
            allocation.Generation,
            allocation.AllocationId,
            "JIT access probe");
        try
        {
            return token.GetValue<int>(0);
        }
        finally
        {
            token.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ProbeNewLease(NativeRegionKernel kernel)
    {
        Local<int> local = kernel.LeaseInitialized(
            1,
            Initializer);
        return local[0];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ProbeLegacyLease(NativeOwnerKernel kernel)
    {
        NativeRegionAllocation allocation =
            kernel.LeaseBumpInitialized(
                1,
                sizeof(int),
                4,
                scoped: false,
                containsReferences: false,
                Initializer);
        return ProbeLegacyAccess(kernel, allocation);
    }
}
