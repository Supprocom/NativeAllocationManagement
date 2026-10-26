namespace Supprocom.NativeAllocationManagement;

/// <summary>Defines one compile-time processor for a bounded native span.</summary>
/// <typeparam name="T">The element type in the span.</typeparam>
/// <typeparam name="TState">The explicit caller state type.</typeparam>
/// <typeparam name="TResult">The result type, which cannot contain the borrowed span.</typeparam>
/// <remarks>
/// The owner calls the static member directly, without a delegate or closure, so the
/// JIT can inline the processor into the validated operation.
/// </remarks>
public interface INativeSpanProcessor<T, TState, TResult>
    where TState : allows ref struct
{
    /// <summary>Processes the bounded span; it cannot escape this call.</summary>
    static abstract TResult Invoke(
        scoped Span<T> values,
        scoped in TState state);
}

/// <summary>Defines one compile-time processor for a bounded read-only native span.</summary>
/// <typeparam name="T">The element type in the span.</typeparam>
/// <typeparam name="TState">The explicit caller state type.</typeparam>
/// <typeparam name="TResult">The result type, which cannot contain the borrowed span.</typeparam>
public interface INativeReadOnlySpanProcessor<T, TState, TResult>
    where TState : allows ref struct
{
    /// <summary>Reads the bounded span; it cannot escape this call.</summary>
    static abstract TResult Invoke(
        scoped ReadOnlySpan<T> values,
        scoped in TState state);
}
