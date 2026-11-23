#if NAM_ABL_FLATSLAB
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

// Measurement only (-p:NamAblation=FlatSlab): the hot slab state in a flat
// struct array indexed by slab id. Slab objects forward to it.
public sealed partial class NativePool<T>
{
    [StructLayout(LayoutKind.Auto)]
    internal struct SlabHot
    {
        internal IntPtr Pointer;
        internal long Token;
        internal int Capacity;
        internal int ClassLow;
        internal int ClassSpan;
        internal int Next;
        internal int BorrowCount;
        internal SlabState State;
    }

    private SlabHot[] _hot = null!;

    internal Slab SlabAt(int index) => _slabs[index];

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal void ReturnFlat(int index, long token, int length)
    {
        if (_lifecycle != NativeOwnerLifecycle.Active)
        {
            ThrowReturnFailure(null!, token);
        }

        ref SlabHot hot = ref _hot[index];
        if (hot.State != SlabState.Leased
            || hot.Token != token
            || hot.BorrowCount != 0)
        {
            ThrowReturnFailure(_slabs[index], token);
        }

        hot.State = SlabState.Free;
        DeferReturn(length);
        int displaced = _returnedSlabIndex;
        _returnedSlabIndex = index;
        _returnedLow = hot.ClassLow;
        _returnedSpan = hot.ClassSpan;
        hot.Next = -1;
        if (displaced >= 0)
        {
            PushFreeList(displaced);
        }
    }
}
#endif
