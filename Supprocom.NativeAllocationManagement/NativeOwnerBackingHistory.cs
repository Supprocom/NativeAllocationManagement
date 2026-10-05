namespace Supprocom.NativeAllocationManagement;

// Only the owner's existing gate serializes acquisitions and peak updates.
// Independent segment finalizers may decrease gauges. This numeric record
// has no references to kernels, generations, segments or native payloads.
internal sealed class NativeOwnerBackingHistory
{
    private long _outstandingBytes;
    private long _detachedBytes;
    private long _peakOutstandingBytes;

    internal void ValidateAcquisition(nuint byteLength)
    {
        _ = checked(Volatile.Read(ref _outstandingBytes) + checked((long)byteLength));
    }

    internal void RecordAcquisition(nuint byteLength)
    {
        long outstanding = Interlocked.Add(ref _outstandingBytes, checked((long)byteLength));
        _peakOutstandingBytes = Math.Max(_peakOutstandingBytes, outstanding);
    }

    internal void RecordDetached(nuint byteLength) =>
        Interlocked.Add(ref _detachedBytes, checked((long)byteLength));

    internal void RecordFree(nuint byteLength, bool detached)
    {
        long bytes = checked((long)byteLength);
        if (detached)
        {
            // Read outstanding first in Capture; a finalizer only decreases.
            Interlocked.Add(ref _detachedBytes, -bytes);
        }
        Interlocked.Add(ref _outstandingBytes, -bytes);
    }

    internal (long Outstanding, long Detached, long Peak) Capture() =>
        (Volatile.Read(ref _outstandingBytes), Volatile.Read(ref _detachedBytes),
            Volatile.Read(ref _peakOutstandingBytes));
}
