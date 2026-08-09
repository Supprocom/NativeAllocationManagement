using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

/// <summary>Reuses one fixed native block for repeated single-writer batches.</summary>
/// <typeparam name="T">The unmanaged element type in the workspace.</typeparam>
public readonly ref struct NativeWorkspace<T>
    where T : unmanaged
{
    private readonly NativeWorkspaceState<T> _state;

    /// <summary>Creates one workspace with a fixed element reservation.</summary>
    /// <param name="preLease">The fixed reservation in elements of <typeparamref name="T"/>.</param>
    public NativeWorkspace(int preLease)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(preLease);
        _state = new NativeWorkspaceState<T>(preLease);
    }

    /// <summary>Gets the fixed physical element capacity.</summary>
    public int Capacity => _state.GetCapacity();

    /// <summary>Gets the published logical element count.</summary>
    public int Length => _state.GetLength();

    /// <summary>Initializes and publishes one logical range.</summary>
    public void Initialize(
        int length,
        NativeLeaseInitializer<T> initializer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initializer);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        cancellationToken.ThrowIfCancellationRequested();
        _state.Initialize(length, initializer, cancellationToken);
    }

    /// <summary>Runs one bounded mutation callback on the published range.</summary>
    public void Access(NativeLeaseAction<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _state.Access(action);
    }

    /// <summary>Runs one bounded read callback on the published range.</summary>
    public TResult Read<TResult>(
        NativeLeaseFunc<T, TResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return _state.Read(action);
    }

    /// <summary>Processes one range with cancellation checks before and after its callbacks.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TResult Process<TResult>(
        int length,
        NativeSpanInitializer<T> initializer,
        NativeSpanReader<T, TResult> reader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initializer);
        ArgumentNullException.ThrowIfNull(reader);
        return _state.Process(
            length,
            initializer,
            reader,
            cancellationToken);
    }

    /// <summary>Processes one stateful range with cancellation checks around its callback.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TResult Process<TState, TResult>(
        int length,
        scoped in TState state,
        NativeSpanStateProcessor<T, TState, TResult> processor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processor);
        return _state.Process(
            length,
            state,
            processor,
            cancellationToken);
    }

    /// <summary>Removes logical visibility and retains the fixed native capacity.</summary>
    public void Reset() => _state.Reset();

    /// <summary>Returns the fixed native block exactly once.</summary>
    public void Dispose() => _state.Release();

    internal object StateForTest => _state;
}

internal sealed class NativeWorkspaceState<T>
    where T : unmanaged
{
    private const int Active = 0;
    private const int Released = 1;

    private NativeBlock _block;
    private readonly int _capacity;
    private readonly int _ownerThreadId;
    private int _state;
    private int _length;
    private int _published;

    internal NativeWorkspaceState(int preLease)
    {
        _ownerThreadId = Environment.CurrentManagedThreadId;
        _block = NativeBlockAllocator.Allocate<T>(
            preLease,
            nameof(NativeWorkspace<T>),
            "NativeWorkspace.Constructor");
        _capacity = preLease;
    }

    internal int GetCapacity()
    {
        Validate(nameof(NativeWorkspace<T>.Capacity));
        return _capacity;
    }

    internal int GetLength()
    {
        Validate(nameof(NativeWorkspace<T>.Length));
        return _length;
    }

    internal void Initialize(
        int length,
        NativeLeaseInitializer<T> initializer,
        CancellationToken cancellationToken)
    {
        try
        {
            ValidateLength(
                length,
                nameof(NativeWorkspace<T>.Initialize));
            int initializedLength = 0;
            NativeLeaseWriter<T> writer = new(
                _block.Pointer,
                length,
                ref initializedLength);
            initializer(writer);
            cancellationToken.ThrowIfCancellationRequested();
            if (initializedLength != length)
            {
                throw new InvalidOperationException(
                    $"The workspace initializer wrote {initializedLength} of {length} required elements.");
            }

            _length = length;
            _published = 1;
        }
        catch
        {
            _length = 0;
            _published = 0;
            throw;
        }
        finally
        {
            GC.KeepAlive(this);
        }
    }

    internal void Access(NativeLeaseAction<T> action)
    {
        try
        {
            ValidateReady(nameof(NativeWorkspace<T>.Access));
            action(new NativeLeaseView<T>(
                _block.Pointer,
                _length));
        }
        finally
        {
            GC.KeepAlive(this);
        }
    }

    internal TResult Read<TResult>(
        NativeLeaseFunc<T, TResult> action)
    {
        try
        {
            ValidateReady(nameof(NativeWorkspace<T>.Read));
            return action(new NativeLeaseView<T>(
                _block.Pointer,
                _length));
        }
        finally
        {
            GC.KeepAlive(this);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal TResult Process<TResult>(
        int length,
        NativeSpanInitializer<T> initializer,
        NativeSpanReader<T, TResult> reader,
        CancellationToken cancellationToken)
    {
        try
        {
            ValidateProcess(length);
            cancellationToken.ThrowIfCancellationRequested();
            Span<T> values = CreateSpan(length);
            initializer(values);
            cancellationToken.ThrowIfCancellationRequested();
            return reader(values);
        }
        finally
        {
            GC.KeepAlive(this);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal TResult Process<TState, TResult>(
        int length,
        scoped in TState state,
        NativeSpanStateProcessor<T, TState, TResult> processor,
        CancellationToken cancellationToken)
    {
        try
        {
            ValidateProcess(length);
            cancellationToken.ThrowIfCancellationRequested();
            Span<T> values = CreateSpan(length);
            TResult result = processor(values, state);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            GC.KeepAlive(this);
        }
    }

    internal void Reset()
    {
        Validate(nameof(NativeWorkspace<T>.Reset));
        _length = 0;
        _published = 0;
    }

    internal void Release()
    {
        ValidateOwnerThread(nameof(NativeWorkspace<T>.Dispose));
        ReleaseCore();
        GC.SuppressFinalize(this);
    }

    private void ReleaseCore()
    {
        if (Interlocked.Exchange(ref _state, Released) != Active)
        {
            return;
        }

        NativeBlock block = _block;
        _block = default;
        _length = 0;
        _published = 0;
        NativeBlockAllocator.Free(block);
    }

    private void ValidateReady(string operation)
    {
        Validate(operation);
        if (_published == 0)
        {
            throw new InvalidOperationException(
                $"NativeWorkspace.{operation} requires a published range.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Validate(string operation)
    {
        ValidateOwnerThread(operation);
        if (_state != Active)
        {
            throw new ObjectDisposedException(
                $"NativeWorkspace<{typeof(T).Name}>");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ValidateLength(int length, string operation)
    {
        Validate(operation);
        if (length > _capacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                "The logical length exceeds the workspace capacity.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe Span<T> CreateSpan(int length) =>
        new((void*)_block.Pointer, length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ValidateProcess(int length)
    {
        Validate(nameof(NativeWorkspace<T>.Process));
        if ((uint)length > (uint)_capacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                "The logical length exceeds the workspace capacity.");
        }

        if (_published != 0)
        {
            throw new InvalidOperationException(
                "NativeWorkspace.Process requires Reset after a published range.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ValidateOwnerThread(string operation)
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException(
                $"NativeWorkspace.{operation} requires its owning thread.");
        }
    }

    ~NativeWorkspaceState()
    {
        try
        {
            ReleaseCore();
        }
        catch
        {
        }
    }
}
