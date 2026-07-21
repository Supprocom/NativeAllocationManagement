namespace Supprocom.NativeAllocationManagement;

/// <summary>
/// Selects how an owner releases the native storage of its current generation when
/// the owner itself is disposed or its memory is returned.
/// </summary>
public enum NativeMemoryReturn
{
    /// <summary>Detaches storage for finalizable cleanup without forcing collection.</summary>
    ToGarbageCollector = 0,

    /// <summary>Frees native storage synchronously after the safety gate succeeds.</summary>
    ToNativeMemory = 1
}

internal static class NativeMemoryReturnValidation
{
    internal static void Validate(NativeMemoryReturn value, string parameterName)
    {
        if (value is not NativeMemoryReturn.ToGarbageCollector and not NativeMemoryReturn.ToNativeMemory)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "The native memory return policy is not recognized.");
        }
    }
}
