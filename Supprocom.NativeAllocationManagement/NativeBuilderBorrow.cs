using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

/// <summary>Provides one exclusive callback-bounded builder authority.</summary>
/// <typeparam name="T">The unmanaged element type.</typeparam>
public ref struct NativeBuilderBorrow<T>
    where T : unmanaged
{
    private readonly NativeBuilder<T> _builder;
    private ref IntPtr _address;
    private ref int _count;
    private ref int _capacity;

    internal NativeBuilderBorrow(
        NativeBuilder<T> builder,
        ref IntPtr address,
        ref int count,
        ref int capacity)
    {
        _builder = builder;
        _address = ref address;
        _count = ref count;
        _capacity = ref capacity;
    }

    /// <summary>Gets the initialized element count.</summary>
    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            EnsureUsable();
            return _count;
        }
    }

    /// <summary>Gets the current native element capacity.</summary>
    public int Capacity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            EnsureUsable();
            return _capacity;
        }
    }

    /// <summary>Appends one value through the exclusive borrow.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(
        T value,
        CancellationToken cancellationToken = default)
    {
        int start = BeginOperation();
        bool canCancel = cancellationToken.CanBeCanceled;
        if (canCancel)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        Span<T> destination = _builder.PrepareBorrowedRange(
            start,
            1,
            ref _address,
            ref _capacity);
        destination[0] = value;
        if (canCancel)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        EndOperation(checked(start + 1));
    }

    /// <summary>Appends one range through the exclusive borrow.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(
        scoped ReadOnlySpan<T> source,
        CancellationToken cancellationToken = default)
    {
        int start = BeginOperation();
        bool canCancel = cancellationToken.CanBeCanceled;
        if (canCancel)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        Span<T> destination = _builder.PrepareBorrowedRange(
            start,
            source.Length,
            ref _address,
            ref _capacity);
        source.CopyTo(destination);
        if (canCancel)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        EndOperation(checked(start + source.Length));
    }

    /// <summary>Writes one bounded range through the exclusive borrow.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Write(
        int maximumAdditionalCount,
        NativeBuilderWriteAction<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        WriteCore<IgnoreCancellation>(
            maximumAdditionalCount,
            action,
            default);
    }

    /// <summary>Writes one cancelable bounded range through the borrow.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Write(
        int maximumAdditionalCount,
        NativeBuilderWriteAction<T> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        WriteCore<ObserveCancellation>(
            maximumAdditionalCount,
            action,
            cancellationToken);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void WriteCore<TCancellation>(
        int maximumAdditionalCount,
        NativeBuilderWriteAction<T> action,
        CancellationToken cancellationToken)
        where TCancellation : struct, ICancellationPolicy
    {
        int start = BeginOperation();
        ArgumentOutOfRangeException.ThrowIfNegative(
            maximumAdditionalCount);
        TCancellation.Check(cancellationToken);

        Span<T> values = _builder.PrepareBorrowedRange(
            start,
            maximumAdditionalCount,
            ref _address,
            ref _capacity);
        TCancellation.Check(cancellationToken);

        int committedCount = -1;
        action(new NativeBuilderWriter<T>(
            values,
            ref committedCount));
        CompleteWrite(
            start,
            committedCount,
            cancellationToken,
            default(TCancellation));
    }

    /// <summary>Writes one bounded range with explicit callback state.</summary>
    /// <typeparam name="TState">The callback state type.</typeparam>
    /// <param name="maximumAdditionalCount">The maximum number of elements for this write.</param>
    /// <param name="state">The callback state.</param>
    /// <param name="action">The static callback that writes and commits the range.</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Write<TState>(
        int maximumAdditionalCount,
        scoped in TState state,
        NativeBuilderWriteStateAction<T, TState> action)
        where TState : allows ref struct
    {
        ArgumentNullException.ThrowIfNull(action);
        WriteStateCore<
            TState,
            DelegateStateWriter<TState>,
            IgnoreCancellation>(
            maximumAdditionalCount,
            in state,
            action,
            default);
    }

    /// <summary>Writes one cancelable bounded range with explicit state.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Write<TState>(
        int maximumAdditionalCount,
        scoped in TState state,
        NativeBuilderWriteStateAction<T, TState> action,
        CancellationToken cancellationToken)
        where TState : allows ref struct
    {
        ArgumentNullException.ThrowIfNull(action);
        WriteStateCore<
            TState,
            DelegateStateWriter<TState>,
            ObserveCancellation>(
            maximumAdditionalCount,
            in state,
            action,
            cancellationToken);
    }

    /// <summary>Writes one bounded range with a compile-time callback.</summary>
    /// <typeparam name="TState">The callback state type.</typeparam>
    /// <typeparam name="TAction">The compile-time callback type.</typeparam>
    /// <param name="maximumAdditionalCount">The maximum element count for this write.</param>
    /// <param name="state">The callback state.</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Write<TState, TAction>(
        int maximumAdditionalCount,
        scoped in TState state)
        where TState : allows ref struct
        where TAction : struct, INativeBuilderWriteAction<T, TState>
    {
        WriteStateCore<
            TState,
            CompileTimeStateWriter<TState, TAction>,
            IgnoreCancellation>(
            maximumAdditionalCount,
            in state,
            action: null,
            default);
    }

    /// <summary>Writes one cancelable range with a compile-time callback.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Write<TState, TAction>(
        int maximumAdditionalCount,
        scoped in TState state,
        CancellationToken cancellationToken)
        where TState : allows ref struct
        where TAction : struct, INativeBuilderWriteAction<T, TState>
    {
        WriteStateCore<
            TState,
            CompileTimeStateWriter<TState, TAction>,
            ObserveCancellation>(
            maximumAdditionalCount,
            in state,
            action: null,
            cancellationToken);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void WriteStateCore<TState, TInvoker, TCancellation>(
        int maximumAdditionalCount,
        scoped in TState state,
        NativeBuilderWriteStateAction<T, TState>? action,
        CancellationToken cancellationToken)
        where TState : allows ref struct
        where TInvoker : struct, IStateWriter<TState>
        where TCancellation : struct, ICancellationPolicy
    {
        int start = BeginOperation();
        ArgumentOutOfRangeException.ThrowIfNegative(
            maximumAdditionalCount);
        TCancellation.Check(cancellationToken);

        Span<T> values = _builder.PrepareBorrowedRange(
            start,
            maximumAdditionalCount,
            ref _address,
            ref _capacity);
        TCancellation.Check(cancellationToken);

        int committedCount = -1;
        TInvoker.Invoke(
            action,
            new NativeBuilderWriter<T>(
                values,
                ref committedCount),
            in state);
        CompleteWrite(
            start,
            committedCount,
            cancellationToken,
            default(TCancellation));
    }

    private interface IStateWriter<TState>
        where TState : allows ref struct
    {
        static abstract void Invoke(
            NativeBuilderWriteStateAction<T, TState>? action,
            scoped NativeBuilderWriter<T> writer,
            scoped in TState state);
    }

    private readonly struct DelegateStateWriter<TState>
        : IStateWriter<TState>
        where TState : allows ref struct
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Invoke(
            NativeBuilderWriteStateAction<T, TState>? action,
            scoped NativeBuilderWriter<T> writer,
            scoped in TState state)
        {
            action!(writer, in state);
        }
    }

    private readonly struct CompileTimeStateWriter<TState, TAction>
        : IStateWriter<TState>
        where TState : allows ref struct
        where TAction : struct, INativeBuilderWriteAction<T, TState>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Invoke(
            NativeBuilderWriteStateAction<T, TState>? action,
            scoped NativeBuilderWriter<T> writer,
            scoped in TState state)
        {
            TAction.Invoke(writer, in state);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CompleteWrite<TCancellation>(
        int start,
        int committedCount,
        CancellationToken cancellationToken,
        TCancellation cancellationPolicy)
        where TCancellation : struct, ICancellationPolicy
    {
        TCancellation.Check(cancellationToken);

        if (committedCount < 0)
        {
            throw new InvalidOperationException(
                "The bounded builder write did not commit an initialized prefix.");
        }

        EndOperation(checked(start + committedCount));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int BeginOperation()
    {
        int start = _count;
        if (start < 0)
        {
            ThrowFailedOperation();
        }

        _count = ~start;
        return start;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EndOperation(int count)
    {
        _count = count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly void EnsureUsable()
    {
        if (_count < 0)
        {
            ThrowFailedOperation();
        }
    }

    private static void ThrowFailedOperation()
    {
        throw new InvalidOperationException(
            "The builder borrow cannot continue after an operation failure.");
    }

    private interface ICancellationPolicy
    {
        static abstract void Check(CancellationToken cancellationToken);
    }

    private readonly struct IgnoreCancellation : ICancellationPolicy
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Check(CancellationToken cancellationToken)
        {
        }
    }

    private readonly struct ObserveCancellation : ICancellationPolicy
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Check(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}

/// <summary>Uses one exclusive builder borrow during a bounded callback.</summary>
/// <typeparam name="T">The unmanaged element type.</typeparam>
public delegate void NativeBuilderBorrowAction<T>(
    scoped ref NativeBuilderBorrow<T> builder)
    where T : unmanaged;

/// <summary>Uses two exclusive builder borrows during one bounded callback.</summary>
/// <typeparam name="T">The unmanaged element type.</typeparam>
public delegate void NativeBuilderPairBorrowAction<T>(
    scoped ref NativeBuilderBorrow<T> first,
    scoped ref NativeBuilderBorrow<T> second)
    where T : unmanaged;

/// <summary>Uses one exclusive builder borrow with explicit callback state.</summary>
/// <typeparam name="T">The unmanaged element type.</typeparam>
/// <typeparam name="TState">The callback state type.</typeparam>
public delegate void NativeBuilderBorrowStateAction<T, TState>(
    scoped ref NativeBuilderBorrow<T> builder,
    scoped in TState state)
    where T : unmanaged
    where TState : allows ref struct;
