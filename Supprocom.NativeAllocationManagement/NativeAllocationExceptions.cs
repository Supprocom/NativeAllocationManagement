namespace Supprocom.NativeAllocationManagement;

/// <summary>
/// Base exception for a native owner or derived handle lifecycle failure.
/// </summary>
public class NativeAllocationException : InvalidOperationException
{
    internal NativeAllocationException(
        string message,
        string ownerKind,
        long generation,
        long currentGeneration,
        string operation,
        int activeOperationCount,
        long allocationId,
        NativeOwnerLifecycle currentLifecycle,
        Exception? innerException = null)
        : base(message, innerException)
    {
        OwnerKind = ownerKind;
        Generation = generation;
        CurrentGeneration = currentGeneration;
        Operation = operation;
        ActiveOperationCount = activeOperationCount;
        AllocationId = allocationId;
        CurrentLifecycle = currentLifecycle;
    }

    /// <summary>Gets the owner kind and element description.</summary>
    public string OwnerKind { get; }

    /// <summary>Gets the generation associated with the attempted operation.</summary>
    public long Generation { get; }

    /// <summary>Gets the owner's currently published generation.</summary>
    public long CurrentGeneration { get; }

    /// <summary>Gets the operation that failed.</summary>
    public string Operation { get; }

    /// <summary>Gets the number of native operations active when the failure was observed.</summary>
    public int ActiveOperationCount { get; }

    /// <summary>Gets the derived allocation identity, or zero for owner-only failures.</summary>
    public long AllocationId { get; }

    /// <summary>Gets the owner lifecycle observed when the operation failed.</summary>
    public NativeOwnerLifecycle CurrentLifecycle { get; }
}

/// <summary>Raised when a default-initialized owner-shaped value is used.</summary>
public sealed class NativeAllocationUninitializedException : NativeAllocationException
{
    internal NativeAllocationUninitializedException(string valueKind, string operation)
        : base(
            $"{valueKind}.{operation} cannot run because the value is uninitialized. Construct the owner or derived handle before use.",
            valueKind,
            generation: 0,
            currentGeneration: 0,
            operation,
            activeOperationCount: 0,
            allocationId: 0,
            currentLifecycle: NativeOwnerLifecycle.Uninitialized)
    {
    }
}

/// <summary>Raised when an owner or derived handle belongs to a returned generation.</summary>
public sealed class NativeAllocationReturnedException : NativeAllocationException
{
    internal NativeAllocationReturnedException(
        string message,
        string ownerKind,
        long generation,
        long currentGeneration,
        string operation,
        int activeOperationCount,
        long allocationId,
        NativeOwnerLifecycle currentLifecycle)
        : base(message, ownerKind, generation, currentGeneration, operation, activeOperationCount, allocationId, currentLifecycle: currentLifecycle)
    {
    }
}

/// <summary>Raised when an owner is permanently disposed.</summary>
public sealed class NativeAllocationDisposedException : NativeAllocationException
{
    internal NativeAllocationDisposedException(
        string message,
        string ownerKind,
        long generation,
        long currentGeneration,
        string operation,
        int activeOperationCount,
        long allocationId,
        NativeOwnerLifecycle currentLifecycle)
        : base(message, ownerKind, generation, currentGeneration, operation, activeOperationCount, allocationId, currentLifecycle: currentLifecycle)
    {
    }
}

/// <summary>Raised when a lifecycle transition loses the active-operation safety gate.</summary>
public sealed class NativeAllocationInUseException : NativeAllocationException
{
    internal NativeAllocationInUseException(
        string message,
        string ownerKind,
        long generation,
        long currentGeneration,
        string operation,
        int activeOperationCount,
        long allocationId,
        NativeOwnerLifecycle currentLifecycle)
        : base(message, ownerKind, generation, currentGeneration, operation, activeOperationCount, allocationId, currentLifecycle: currentLifecycle)
    {
    }
}

/// <summary>
/// Raised when cleanup of an ended generation cannot safely rejoin its native
/// storage with the active owner. The storage remains quarantined and cannot be
/// reused until its owner is disposed or finalized.
/// </summary>
public sealed class NativeAllocationQuarantinedException : NativeAllocationException
{
    internal NativeAllocationQuarantinedException(
        string message,
        string ownerKind,
        long generation,
        long currentGeneration,
        string operation,
        int activeOperationCount,
        long allocationId,
        long segmentOrdinal,
        string boundary,
        NativeOwnerLifecycle currentLifecycle,
        Exception? innerException = null)
        : base(
            message,
            ownerKind,
            generation,
            currentGeneration,
            operation,
            activeOperationCount,
            allocationId,
            currentLifecycle,
            innerException)
    {
        SegmentOrdinal = segmentOrdinal;
        Boundary = boundary;
    }

    /// <summary>Gets the immutable physical segment ordinal that was quarantined.</summary>
    public long SegmentOrdinal { get; }

    /// <summary>Gets the cleanup boundary at which quarantine was required.</summary>
    public string Boundary { get; }
}

/// <summary>Raised when an operation is incompatible with the owner's current lifecycle state.</summary>
public sealed class NativeAllocationStateException : NativeAllocationException
{
    internal NativeAllocationStateException(
        string message,
        string ownerKind,
        long generation,
        long currentGeneration,
        string operation,
        int activeOperationCount,
        long allocationId,
        NativeOwnerLifecycle currentLifecycle)
        : base(message, ownerKind, generation, currentGeneration, operation, activeOperationCount, allocationId, currentLifecycle: currentLifecycle)
    {
    }
}

/// <summary>Raised when a native segment cannot be allocated.</summary>
public sealed class NativeAllocationFailedException : NativeAllocationException
{
    internal NativeAllocationFailedException(
        nuint requestedBytes,
        string ownerKind,
        long generation,
        string operation,
        NativeOwnerLifecycle currentLifecycle,
        Exception? innerException = null)
        : base(
            $"Native allocation of {requestedBytes} bytes failed during {operation} (lifecycle {currentLifecycle}). Earlier valid segments remain owned; no partially usable handle was returned.",
            ownerKind,
            generation,
            generation,
            operation,
            activeOperationCount: 0,
            allocationId: 0,
            currentLifecycle,
            innerException)
    {
        RequestedBytes = requestedBytes;
    }

    /// <summary>Gets the requested native byte count.</summary>
    public nuint RequestedBytes { get; }
}
