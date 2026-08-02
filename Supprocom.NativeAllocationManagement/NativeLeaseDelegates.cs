namespace Supprocom.NativeAllocationManagement;

/// <summary>Performs a bounded mutation while a NAM operation token is held.</summary>
public delegate void NativeLeaseAction<T>(scoped NativeLeaseView<T> view);

/// <summary>Performs a bounded read while a NAM operation token is held.</summary>
public delegate TResult NativeLeaseFunc<T, TResult>(scoped NativeLeaseView<T> view);

/// <summary>Initializes one bounded direct native span before publication.</summary>
public delegate void NativeSpanInitializer<T>(scoped Span<T> values);

/// <summary>Reads one bounded direct native span before its owner resets it.</summary>
public delegate TResult NativeSpanReader<T, TResult>(
    scoped ReadOnlySpan<T> values);

/// <summary>Initializes all elements before NAM publishes a readable lease.</summary>
public delegate void NativeLeaseInitializer<T>(
    scoped NativeLeaseWriter<T> writer);
