namespace Supprocom.NativeAllocationManagement;

// An owner whose native storage the guard returns if the owner is dropped
// without disposal.
internal interface INativeFinalizationTarget
{
    void FinalizeNativeStorage();
}

// The emergency finalizer of an owner that is not itself finalizable. A
// finalizable allocation costs about five times a plain one (80-90 ns against
// 14-17 ns on the reference host), which dominated creating an arena or pool
// per scope. Owners take a guard from their thread instead, and a disposed
// owner hands it back still registered, so only the first owner on a thread
// allocates one.
//
// The guard references its owner and only the owner references the guard: if
// both become unreachable without disposal, the guard's finalizer returns the
// owner's native storage, as the owner's own finalizer did.
internal sealed class NativeFinalizationGuard
{
    internal INativeFinalizationTarget? Target;

    // Takes this thread's spare guard, or a new one, for an owner being
    // created; set before the owner acquires any native storage.
    internal static NativeFinalizationGuard Rent(NativeThreadState thread, INativeFinalizationTarget target)
    {
        NativeFinalizationGuard guard = thread.SpareFinalizationGuard ?? new NativeFinalizationGuard();
        thread.SpareFinalizationGuard = null;
        guard.Target = target;
        return guard;
    }

    // The owner's storage is returned: the guard, still registered, waits on
    // this thread for the next owner, or is disarmed when one already waits.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1816", Justification = "The guard is disarmed only after its owner returned its native storage.")]
    internal void Release(NativeThreadState thread)
    {
        Target = null;
        if (thread.SpareFinalizationGuard is null)
        {
            thread.SpareFinalizationGuard = this;
            return;
        }

        GC.SuppressFinalize(this);
    }

    // The owner's storage is returned for a thread that ended, which needs no
    // spare guard.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1816", Justification = "The guard is disarmed only after its owner returned its native storage.")]
    internal void Disarm()
    {
        Target = null;
        GC.SuppressFinalize(this);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "An emergency finalizer must never let cleanup exceptions terminate the process.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "MA0055", Justification = "Emergency native-memory cleanup supplements mandatory deterministic disposal.")]
    ~NativeFinalizationGuard()
    {
        try
        {
            Target?.FinalizeNativeStorage();
        }
        catch
        {
        }
    }
}
