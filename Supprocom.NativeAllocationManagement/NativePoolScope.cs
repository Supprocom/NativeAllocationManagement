namespace Supprocom.NativeAllocationManagement;

/// <summary>The callback run by <see cref="NativePoolScope.Run{T, TState, TResult}"/>.</summary>
public delegate TResult NativePoolScopeBody<T, TState, TResult>(scoped ref NativeScopePool<T> pool, TState state)
    where T : unmanaged;

/// <summary>The callback run by <see cref="NativePoolScope.Run{T, TState}"/>.</summary>
public delegate void NativePoolScopeAction<T, TState>(scoped ref NativeScopePool<T> pool, TState state)
    where T : unmanaged;

/// <summary>Runs callbacks with a pool that exists only for the call.</summary>
public static class NativePoolScope
{
    /// <summary>Runs <paramref name="body"/> with a new scope pool and frees all its storage afterwards.</summary>
    public static TResult Run<T, TState, TResult>(TState state, NativePoolScopeBody<T, TState, TResult> body)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(body);
        NativePool<T> kernel = new(0, NativeMemoryReturn.Free);
        try
        {
            NativeScopePool<T> pool = new(kernel);
            return body(ref pool, state);
        }
        finally
        {
            kernel.EndScope();
        }
    }

    /// <summary>Runs <paramref name="body"/> with a new scope pool and frees all its storage afterwards.</summary>
    public static void Run<T, TState>(TState state, NativePoolScopeAction<T, TState> body)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(body);
        NativePool<T> kernel = new(0, NativeMemoryReturn.Free);
        try
        {
            NativeScopePool<T> pool = new(kernel);
            body(ref pool, state);
        }
        finally
        {
            kernel.EndScope();
        }
    }
}

/// <summary>A pool bound to one <see cref="NativePoolScope"/> call.</summary>
/// <typeparam name="T">The unmanaged element type.</typeparam>
public ref struct NativeScopePool<T>
    where T : unmanaged
{
    // A private NativePool<T>. It is reachable only through this ref struct, which the
    // callback receives as a scoped ref parameter: it cannot be stored, captured or
    // carried across an await, so every use is on the thread that called Run.
    private readonly NativePool<T> _kernel;

    internal NativeScopePool(NativePool<T> kernel) => _kernel = kernel;

    /// <summary>Rents <paramref name="length"/> elements, each set to <paramref name="value"/>.</summary>
    [System.Diagnostics.CodeAnalysis.UnscopedRef]
    public Pooled<T> Rent(int length, T value) => _kernel.RentForScope(length, value);

    /// <summary>Rents <paramref name="length"/> elements that may hold old contents.</summary>
    [System.Diagnostics.CodeAnalysis.UnscopedRef]
    public Pooled<T> RentUninitialized(int length) => _kernel.RentUninitializedForScope(length);
}
