namespace Supprocom.NativeAllocationManagement;

/// <summary>
/// Performs a synchronous bounded mutation over a native-backed span.
/// </summary>
public delegate void NativeSpanAction<T>(scoped Span<T> span)
    where T : unmanaged;

/// <summary>
/// Performs a synchronous bounded read over a native-backed span.
/// </summary>
public delegate TResult NativeSpanFunc<T, TResult>(scoped ReadOnlySpan<T> span)
    where T : unmanaged;

