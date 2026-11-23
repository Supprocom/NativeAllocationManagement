using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

// A native buffer at a chosen alignment, and the address of any span, for the --memory-cost
// diagnostic that tests whether read speed depends on where a destination buffer sits.
internal sealed unsafe class NativeAlignedBuffer : IDisposable
{
    private void* _memory;
    private readonly int _length;

    private NativeAlignedBuffer(void* memory, int length)
    {
        _memory = memory;
        _length = length;
    }

    internal static NativeAlignedBuffer Allocate(int length, nuint alignment)
    {
        void* memory = NativeMemory.AlignedAlloc((nuint)length, alignment);
        if (memory == null)
        {
#pragma warning disable CA2201
            throw new OutOfMemoryException();
#pragma warning restore CA2201
        }

        return new NativeAlignedBuffer(memory, length);
    }

    internal Span<byte> Span => _memory == null
        ? throw new ObjectDisposedException(nameof(NativeAlignedBuffer))
        : new Span<byte>(_memory, _length);

    // The address the span starts at, for reporting. A managed array's address is only momentary.
    internal static nint AddressOf(Span<byte> span) =>
        (nint)Unsafe.AsPointer(ref MemoryMarshal.GetReference(span));

    public void Dispose()
    {
        void* memory = _memory;
        _memory = null;
        if (memory != null)
        {
            NativeMemory.AlignedFree(memory);
        }

        GC.SuppressFinalize(this);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "MA0055", Justification = "Emergency native-memory cleanup supplements mandatory deterministic disposal.")]
    ~NativeAlignedBuffer() => Dispose();
}
