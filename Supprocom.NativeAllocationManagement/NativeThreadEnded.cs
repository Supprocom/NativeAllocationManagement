using System.Globalization;
using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

// The runtime's mark that a thread ended, which NativeThreadState.Current needs
// to refuse an ended thread's state. CoreCLR keeps it in a private field,
// Thread._isDead, and in release 10.0 sets it on the dying thread itself, in
// Thread::CooperativeCleanup: from OnThreadTerminate for a thread the runtime
// started, and from the thread-exit hook (a thread_local destructor on Unix,
// the fiber-local callback on Windows) for a native thread that entered the
// runtime. That is before the thread exits, so before its stack can serve
// another thread, and the exit and the next thread's creation order the store
// before that thread's first read. The runtime skips that cleanup only once the
// process is exiting.
//
// A runtime could keep the field and set it later, or not on some exit path,
// and a new thread could then take an ended thread's state while
// NativeEndedThreads releases it. So the lookup is on only under CoreCLR with
// runtime major version 10, whose source this was checked against, and only
// when the fields are there; NativeAOT and Mono have neither. Every other
// runtime reads the thread-static, as before.
internal static class NativeThreadEnded
{
    // Tests force the runtime major version the gate sees, to run the suite on
    // the thread-static path.
    internal const string RuntimeMajorVersionSwitch = "Supprocom.NativeAllocationManagement.Testing.RuntimeMajorVersion";

    internal static readonly bool CanRead = Probe();

    internal static int RuntimeMajorVersion =>
        AppContext.GetData(RuntimeMajorVersionSwitch) is string forced
        && int.TryParse(forced, NumberStyles.Integer, CultureInfo.InvariantCulture, out int major)
            ? major
            : Environment.Version.Major;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool Read(Thread thread) => EndedField(thread);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_isDead")]
    private static extern ref bool EndedField(Thread thread);

    // CoreCLR's pointer to its native thread object. NativeAOT and Mono have no
    // such field, so finding it tells CoreCLR apart from them.
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_DONT_USE_InternalThread")]
    private static extern ref nint NativeThreadField(Thread thread);

    private static bool Probe()
    {
        if (RuntimeMajorVersion != 10)
        {
            return false;
        }

        try
        {
            Thread thread = Thread.CurrentThread;
            return NativeThreadField(thread) != 0 && !EndedField(thread);
        }
        catch (MissingMemberException)
        {
            return false;
        }
    }
}
