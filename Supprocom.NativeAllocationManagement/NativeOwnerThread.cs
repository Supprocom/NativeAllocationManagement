using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

// The thread a thread-confined owner belongs to, which every operation of a
// pool, arena or region checks. A [ThreadStatic] read is a TLS call (about
// 1.9 ns on the reference host), so the check first compares the 4 KiB block of
// the caller's stack frame with the block where the owner thread was last
// confirmed, and takes a caller in that block to be the owner thread. Any other
// caller is checked by thread serial (NativeThreadState.Serial), which, unlike
// a managed thread id, no other thread is ever given; when it matches, the
// block moves to the caller's.
//
// Assumption: no two live threads have stack frames in the same 4 KiB block.
// The stacks an operating system allocates for threads are separate whole
// pages, so they cannot share one. A stack a program supplies itself
// (pthread_attr_setstack) may begin or end inside a block that another
// thread's stack also uses, and then both threads pass while their frames are
// in that block. NAM cannot detect such stacks.
//
// After the owner thread exits, its block no longer moves: the serial refuses
// every other thread. A new thread whose stack reuses that memory passes while
// its frame is in the block, as a new thread given the exited thread's
// recycled managed id passed the id check this replaced. Using an owner after
// its thread has exited is a program error either way.
[StructLayout(LayoutKind.Auto)]
internal struct NativeOwnerThread
{
    private const int BlockShift = 12;

    internal readonly long Serial;

    // One word, so a thread that reads it while the owner thread moves it sees
    // one of two blocks, both the owner thread's.
    private nuint _block;

    // On the owner thread.
    internal NativeOwnerThread(NativeThreadState thread)
    {
        Serial = thread.Serial;
        _block = CurrentBlock();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool IsCurrent()
    {
        nuint block = CurrentBlock();
        return block == _block || IsCurrentSlow(block);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool IsCurrentSlow(nuint block)
    {
        NativeThreadState thread = NativeThreadState.Current;
        thread.OwnerThreadSlowChecks++;
        if (thread.Serial != Serial)
        {
            return false;
        }

        _block = block;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static unsafe nuint CurrentBlock()
    {
        byte frame;
        return (nuint)(&frame) >> BlockShift;
    }

    // A block no frame is in, so the next check takes the serial path.
    internal void ForgetBlockForTest() => _block = 0;
}

// Tests simulate a thread whose stack reuses an exited thread's memory by
// naming the stack block NativeOwnerKernel.FindOwnCache looks up. Only a test
// process sets the switch; elsewhere the flag is false, and optimized code
// drops the override.
internal static class NativeStackBlockForTest
{
    internal const string Switch = "Supprocom.NativeAllocationManagement.Testing.StackBlockOverride";

    internal static readonly bool Enabled = AppContext.TryGetSwitch(Switch, out bool enabled) && enabled;

    // The block this thread's lookups use while nonzero.
    [ThreadStatic]
    internal static nuint Override;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static nuint Current() =>
        Enabled && Override != 0 ? Override : NativeOwnerThread.CurrentBlock();
}
