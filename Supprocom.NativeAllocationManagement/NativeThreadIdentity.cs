using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

// Environment.CurrentManagedThreadId is a runtime call on every owner check;
// the thread state keeps a copy behind one TLS load.
internal static class NativeThreadIdentity
{
    internal static int Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => NativeThreadState.Current.ManagedThreadId;
    }
}
