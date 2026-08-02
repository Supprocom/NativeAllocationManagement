using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

/// <summary>Adds one persistent typed workspace to a native pool.</summary>
public static class NativeWorkspacePoolExtensions
{
    /// <summary>Reserves one fixed-capacity workspace for repeated bounded work.</summary>
    public static NativeWorkspace<T> CreateWorkspace<T>(
        this NativePool<T> pool,
        int capacity)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        NativeOwnerKernel kernel = pool.KernelForTransfer;
        NativeWorkspaceState<T> state = new(kernel);
        state.Begin(capacity);
        return new NativeWorkspace<T>(state);
    }
}

/// <summary>Reuses one fixed native range for repeated single-writer batches.</summary>
/// <typeparam name="T">The unmanaged element type in the workspace.</typeparam>
public readonly ref struct NativeWorkspace<T>
    where T : unmanaged
{
    private readonly NativeWorkspaceState<T> _state;

    internal NativeWorkspace(
        NativeWorkspaceState<T> state)
    {
        _state = state;
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

    /// <summary>Initializes, reads, and resets one range under one lifetime check.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TResult Process<TResult>(
        int length,
        NativeSpanInitializer<T> initializer,
        NativeSpanReader<T, TResult> reader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initializer);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        cancellationToken.ThrowIfCancellationRequested();
        return _state.Process(
            length,
            initializer,
            reader,
            cancellationToken);
    }

    /// <summary>Removes logical visibility and retains the fixed native capacity.</summary>
    public void Reset() => _state.Reset();

    /// <summary>Returns the persistent native range exactly once.</summary>
    public void Dispose() => _state.Release();

    internal object StateForTest => _state;
}

internal sealed class NativeWorkspaceState<T>
    where T : unmanaged
{
    private const int Uninitialized = -1;
    private const int Active = 0;
    private const int Released = 1;

    private readonly NativeOwnerKernel _kernel;
    private NativeGeneration? _generation;
    private NativeBuilderInitialization _initialization;
    private int _capacity;
    private readonly int _ownerThreadId;
    private int _state = Uninitialized;
    private int _length;
    private int _published;

    internal NativeWorkspaceState(
        NativeOwnerKernel kernel)
    {
        _kernel = kernel;
        _ownerThreadId = Environment.CurrentManagedThreadId;
    }

    internal void Begin(int capacity)
    {
        NativeBuilderInitialization initialization =
            _kernel.BeginPoolBuilder(capacity);
        try
        {
            NativeMemoryTestHooks.CheckManagedPublicationBoundary(
                "NativePool.CreateWorkspace",
                ordinal: 1,
                "NativeWorkspaceState ownership");
            initialization.Allocation.SetBuilderLength(0);
            initialization.Allocation.InitializedLength = 0;
            _generation = initialization.Generation;
            _initialization = initialization;
            _capacity = initialization.Allocation.Capacity;
            Volatile.Write(ref _state, Active);
        }
        catch (Exception failure)
        {
            try
            {
                _kernel.AbortUnpublishedBuilderInitialization(
                    initialization);
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(
                    "Native workspace publication failed and cleanup also failed.",
                    failure,
                    cleanupFailure);
            }
            finally
            {
                Volatile.Write(ref _state, Released);
            }

            throw;
        }
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
        Validate(nameof(NativeWorkspace<T>.Initialize));
        if (length > _capacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                "The logical length exceeds the workspace capacity.");
        }

        NativeAllocation allocation =
            _initialization.Allocation;
        allocation.SetBuilderLength(length);
        allocation.InitializedLength = 0;
        int initializedLength = 0;
        try
        {
            NativeLeaseWriter<T> writer = new(
                allocation,
                ref initializedLength);
            initializer(writer);
            cancellationToken.ThrowIfCancellationRequested();
            if (initializedLength != length)
            {
                throw new InvalidOperationException(
                    $"The workspace initializer wrote {initializedLength} of {length} required elements.");
            }

            allocation.InitializedLength = length;
            Volatile.Write(ref _length, length);
            Volatile.Write(ref _published, 1);
        }
        catch
        {
            allocation.InitializedLength = 0;
            allocation.SetBuilderLength(0);
            Volatile.Write(ref _length, 0);
            Volatile.Write(ref _published, 0);
            throw;
        }
    }

    internal void Access(NativeLeaseAction<T> action)
    {
        ValidateReady(nameof(NativeWorkspace<T>.Access));
        action(new NativeLeaseView<T>(
            _initialization.Allocation));
    }

    internal TResult Read<TResult>(
        NativeLeaseFunc<T, TResult> action)
    {
        ValidateReady(nameof(NativeWorkspace<T>.Read));
        return action(new NativeLeaseView<T>(
            _initialization.Allocation));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal TResult Process<TResult>(
        int length,
        NativeSpanInitializer<T> initializer,
        NativeSpanReader<T, TResult> reader,
        CancellationToken cancellationToken)
    {
        Validate(nameof(NativeWorkspace<T>.Process));
        if (_published != 0)
        {
            throw new InvalidOperationException(
                "NativeWorkspace.Process requires Reset after a published range.");
        }

        if (length > _capacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                "The logical length exceeds the workspace capacity.");
        }

        NativeAllocation allocation = _initialization.Allocation;
        Span<T> values = allocation
            .AsCapacitySpan<T>()
            .Slice(0, length);
        initializer(values);
        cancellationToken.ThrowIfCancellationRequested();
        return reader(values);
    }

    internal void Reset()
    {
        Validate(nameof(NativeWorkspace<T>.Reset));
        NativeAllocation allocation =
            _initialization.Allocation;
        allocation.InitializedLength = 0;
        allocation.SetBuilderLength(0);
        Volatile.Write(ref _length, 0);
        Volatile.Write(ref _published, 0);
    }

    internal void Release()
    {
        ValidateOwnerThread(nameof(NativeWorkspace<T>.Dispose));
        ReleaseCore();
        GC.SuppressFinalize(this);
    }

    private void ReleaseCore()
    {
        if (Interlocked.Exchange(
            ref _state,
            Released) != Active)
        {
            return;
        }

        try
        {
            NativeAllocation allocation =
                _initialization.Allocation;
            allocation.InitializedLength = 0;
            allocation.SetBuilderLength(_capacity);
            _kernel.AbortBuilderInitialization(
                _initialization);
        }
        finally
        {
            NativeGeneration generation = _generation
                ?? throw new InvalidOperationException(
                    "The native workspace has no generation ownership.");
            _kernel.ExitBuilderGeneration(generation);
        }
    }

    private void ValidateReady(string operation)
    {
        Validate(operation);
        if (Volatile.Read(ref _published) == 0)
        {
            throw new InvalidOperationException(
                $"NativeWorkspace.{operation} requires a published range.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Validate(string operation)
    {
        ValidateOwnerThread(operation);

        if (Volatile.Read(ref _state) != Active)
        {
            throw new ObjectDisposedException(
                $"NativeWorkspace<{typeof(T).Name}>");
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
