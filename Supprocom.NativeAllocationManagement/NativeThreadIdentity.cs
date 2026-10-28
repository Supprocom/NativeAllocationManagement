using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

// Environment.CurrentManagedThreadId is a runtime call on every owner check; a
// thread-static copy is one TLS load. Managed thread ids start at one, so zero
// marks a thread that has not cached its id yet.
internal static class NativeThreadIdentity
{
    [ThreadStatic]
    private static int t_currentId;

    internal static int Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            int id = t_currentId;
            return id != 0 ? id : Initialize();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Initialize() =>
        t_currentId = Environment.CurrentManagedThreadId;
}
