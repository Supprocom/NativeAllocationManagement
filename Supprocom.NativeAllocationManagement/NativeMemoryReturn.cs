namespace Supprocom.NativeAllocationManagement;

/// <summary>
/// Selects how an owner releases the native storage of its current generation when
/// the owner itself is disposed or its memory is returned.
/// </summary>
public enum NativeMemoryReturn
{
    /// <summary>Keeps storage for reuse by later owners, without forcing a collection.</summary>
    /// <remarks>
    /// The thread's next owner of the same kind, and with the same budget, takes a disposed owner's
    /// storage, up to 1 MiB per owner. Without a budget, blocks of 256 KiB or more may instead wait for
    /// any thread's next owner, until a full garbage collection finds them unused. What a thread kept
    /// returns to native memory after the thread ends, and a budget takes back the storage kept for it
    /// before it refuses an admission. Owners of a budget created with a trace capacity keep nothing: a
    /// region, arena or pool that would otherwise keep its storage frees it on disposal instead. Other
    /// storage that is not kept is detached and freed by finalization.
    /// </remarks>
    Reuse = 0,

    /// <summary>Frees native storage synchronously after the safety gate succeeds.</summary>
    Free = 1,

    /// <summary>Obsolete name of <see cref="Reuse"/>, with the same value.</summary>
    [Obsolete("Renamed to NativeMemoryReturn.Reuse, which has the same value.")]
    ToGarbageCollector = Reuse,

    /// <summary>Obsolete name of <see cref="Free"/>, with the same value.</summary>
    [Obsolete("Renamed to NativeMemoryReturn.Free, which has the same value.")]
    ToNativeMemory = Free
}

internal static class NativeMemoryReturnValidation
{
    internal static void Validate(NativeMemoryReturn value, string parameterName)
    {
        if (value is not NativeMemoryReturn.Reuse and not NativeMemoryReturn.Free)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "The native memory return policy is not recognized.");
        }
    }
}
