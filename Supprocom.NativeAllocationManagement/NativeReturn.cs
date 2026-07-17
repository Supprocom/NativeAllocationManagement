namespace Supprocom.NativeAllocationManagement;

/// <summary>
/// Selects how an owner releases the native segments of its current generation.
/// </summary>
public enum NativeReturn
{
    /// <summary>
    /// Detaches the generation for finalizable cleanup without forcing collection.
    /// </summary>
    ToGarbageCollector = 0,

    /// <summary>
    /// Frees the generation's native segments synchronously after the safety gate succeeds.
    /// </summary>
    ToNativeMemory = 1
}

internal static class NativeReturnValidation
{
    internal static void Validate(NativeReturn value, string parameterName)
    {
        if (value is not NativeReturn.ToGarbageCollector and not NativeReturn.ToNativeMemory)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "The native return policy is not recognized.");
        }
    }
}

