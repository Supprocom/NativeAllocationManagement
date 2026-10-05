using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

// Only immutable sharing can move an entered token into heap-storable lifetime
// admission. Ordinary bounded operations keep their existing ref-struct path.
[StructLayout(LayoutKind.Sequential)]
internal struct NativeStorageLifetimePin
{
    private NativeOwnerKernel? _kernel;
    private NativeGeneration? _generation;
    private NativeGenerationOwner? _generationOwner;
    private NativeAllocation? _allocation;
    private readonly bool _allocationEntered;
    private readonly bool _generationEntered;
    private string? _operation;

    internal NativeStorageLifetimePin(NativeOwnerKernel kernel, NativeGeneration generation,
        NativeAllocation allocation, bool allocationEntered, bool generationEntered, string operation)
    {
        _kernel = kernel;
        _generation = generation;
        _generationOwner = generation.Owner;
        _allocation = allocation;
        _allocationEntered = allocationEntered;
        _generationEntered = generationEntered;
        _operation = operation;
    }

    internal void Dispose()
    {
        NativeOwnerKernel? kernel = Interlocked.Exchange(ref _kernel, null);
        if (kernel is not null)
        {
            NativeGeneration generation = _generation!;
            NativeAllocation allocation = _allocation!;
            NativeGenerationOwner generationOwner = _generationOwner!;
            string operation = _operation!;
            _generation = null;
            _allocation = null;
            _generationOwner = null;
            _operation = null;
            try { kernel.ExitOperation(generation, allocation, _allocationEntered, _generationEntered, operation); }
            finally { GC.KeepAlive(generationOwner); }
        }
    }
}
