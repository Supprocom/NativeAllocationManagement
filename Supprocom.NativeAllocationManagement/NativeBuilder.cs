namespace Supprocom.NativeAllocationManagement;

/// <summary>Adds growable unmanaged builders to native owners.</summary>
public static class NativeBuilderOwnerExtensions
{
    /// <summary>Creates one single-writer builder from a typed native pool.</summary>
    /// <param name="pool">The typed owner that supplies native storage.</param>
    /// <param name="preLease">The initial reservation in elements of <typeparamref name="T"/>.</param>
    public static NativeBuilder<T> CreateBuilder<T>(
        this NativePool<T> pool,
        int preLease = 0)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentOutOfRangeException.ThrowIfNegative(
            preLease);
        NativeBuilderSession<T> session = new(
            pool.KernelForTransfer);
        session.BeginPool(preLease);
        return PublishBuilder(
            session,
            "NativePool.CreateBuilder");
    }

    /// <summary>Creates one single-writer builder from a heterogeneous arena.</summary>
    /// <param name="arena">The heterogeneous owner that supplies native storage.</param>
    /// <param name="preLease">The initial reservation in elements of <typeparamref name="T"/>.</param>
    public static NativeBuilder<T> CreateBuilder<T>(
        this NativeArena arena,
        int preLease = 0)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentOutOfRangeException.ThrowIfNegative(
            preLease);
        NativeBuilderSession<T> session = new(
            arena.KernelForInitialization);
        session.BeginArena(preLease);
        return PublishBuilder(
            session,
            "NativeArena.CreateBuilder");
    }

    private static NativeBuilder<T> PublishBuilder<T>(
        NativeBuilderSession<T> session,
        string operation)
        where T : unmanaged
    {
        try
        {
            NativeMemoryTestHooks.CheckManagedPublicationBoundary(
                operation,
                ordinal: 2,
                "NativeBuilder");
            return new NativeBuilder<T>(session);
        }
        catch (Exception failure)
        {
            try
            {
                session.Abort();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(
                    "Native builder creation failed and cleanup also failed.",
                    failure,
                    cleanupFailure);
            }

            throw;
        }
    }
}

/// <summary>Builds one growable unmanaged sequence directly in native storage.</summary>
/// <typeparam name="T">The unmanaged element type.</typeparam>
public sealed class NativeBuilder<T> : IDisposable
    where T : unmanaged
{
    private const int Active = 0;
    private const int Completing = 1;
    private const int Completed = 2;
    private const int Disposing = 3;
    private const int Disposed = 4;
    private const int Finalized = 5;

    private readonly NativeBuilderSession<T> _session;
    private int _state;
    private int _writerGate;
    private int _operationAdmission;
    private int _count;

    internal NativeBuilder(
        NativeBuilderSession<T> session)
    {
        _session = session;
    }

    /// <summary>Gets the initialized element count.</summary>
    public int Count => ReadState(
        nameof(Count),
        readCapacity: false);

    /// <summary>Gets the current native element capacity.</summary>
    public int Capacity => ReadState(
        nameof(Capacity),
        readCapacity: true);

    /// <summary>Appends one value directly to native storage.</summary>
    public void Append(
        T value,
        CancellationToken cancellationToken = default)
    {
        EnterOperation(nameof(Append));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _session.Append(value, _count);
            _count++;
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception failure)
        {
            FailOperation(failure);
            throw;
        }
        finally
        {
            ExitOperation();
            GC.KeepAlive(this);
        }
    }

    /// <summary>Appends one range directly to native storage.</summary>
    public void Append(
        scoped ReadOnlySpan<T> source,
        CancellationToken cancellationToken = default)
    {
        EnterOperation(nameof(Append));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            int required = checked(_count + source.Length);
            _session.Append(source, _count, required);
            _count = required;
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception failure)
        {
            FailOperation(failure);
            throw;
        }
        finally
        {
            ExitOperation();
            GC.KeepAlive(this);
        }
    }

    /// <summary>Publishes one exact logical range and invalidates this builder.</summary>
    public NativeTransfer<T> Complete(
        CancellationToken cancellationToken = default)
    {
        EnterTerminalOperation(
            nameof(Complete),
            Completing);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            NativeTransfer<T> transfer =
                _session.Complete(_count);
            Volatile.Write(ref _state, Completed);
            GC.SuppressFinalize(this);
            return transfer;
        }
        catch (Exception failure)
        {
            Exception? cleanupFailure = null;
            try
            {
                _session.Abort();
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }

            Volatile.Write(ref _state, Disposed);
            GC.SuppressFinalize(this);
            if (cleanupFailure is not null)
            {
                throw new AggregateException(
                    "NativeBuilder.Complete failed and cleanup also failed.",
                    failure,
                    cleanupFailure);
            }

            throw;
        }
        finally
        {
            Volatile.Write(ref _writerGate, 0);
            GC.KeepAlive(this);
        }
    }

    /// <summary>Returns unpublished builder storage exactly once.</summary>
    public void Dispose()
    {
        int state = Volatile.Read(ref _state);
        if (state is Completed or Disposed or Finalized)
        {
            return;
        }

        EnterTerminalOperation(
            nameof(Dispose),
            Disposing);
        try
        {
            _session.Abort();
            Volatile.Write(ref _state, Disposed);
            GC.SuppressFinalize(this);
        }
        catch
        {
            Volatile.Write(ref _state, Disposed);
            GC.SuppressFinalize(this);
            throw;
        }
        finally
        {
            Volatile.Write(ref _writerGate, 0);
            GC.KeepAlive(this);
        }
    }

    private int ReadState(
        string operation,
        bool readCapacity)
    {
        EnterOperation(operation);
        try
        {
            _session.Validate(operation);
            return readCapacity
                ? _session.Capacity
                : _count;
        }
        catch (Exception failure)
        {
            FailOperation(failure);
            throw;
        }
        finally
        {
            ExitOperation();
            GC.KeepAlive(this);
        }
    }

    private void EnterOperation(string operation)
    {
        EnsureActive(operation);
        if (Interlocked.CompareExchange(
            ref _writerGate,
            1,
            0) != 0)
        {
            throw new InvalidOperationException(
                $"NativeBuilder.{operation} cannot run during another builder operation.");
        }

        try
        {
            _session.NotifyBeforeOperation(operation);
            if (!NativeOperationAdmission.TryEnter(
                ref _operationAdmission))
            {
                ThrowInactive(
                    operation,
                    Volatile.Read(ref _state));
            }

            int state = Volatile.Read(ref _state);
            if (state == Active)
            {
                return;
            }

            NativeOperationAdmission.Exit(
                ref _operationAdmission);
            ThrowInactive(operation, state);
        }
        catch
        {
            Volatile.Write(ref _writerGate, 0);
            throw;
        }
    }

    private void ExitOperation()
    {
        NativeOperationAdmission.Exit(
            ref _operationAdmission);
        Volatile.Write(ref _writerGate, 0);
    }

    private void EnterTerminalOperation(
        string operation,
        int terminalState)
    {
        EnsureActive(operation);
        if (Interlocked.CompareExchange(
            ref _writerGate,
            1,
            0) != 0)
        {
            throw new InvalidOperationException(
                $"NativeBuilder.{operation} cannot run during another builder operation.");
        }

        int observed = Interlocked.CompareExchange(
            ref _state,
            terminalState,
            Active);
        if (observed != Active)
        {
            Volatile.Write(ref _writerGate, 0);
            ThrowInactive(operation, observed);
        }

        int activeOperations =
            NativeOperationAdmission.Close(
                ref _operationAdmission);
        if (activeOperations == 0)
        {
            return;
        }

        NativeOperationAdmission.Open(
            ref _operationAdmission);
        Volatile.Write(ref _state, Active);
        Volatile.Write(ref _writerGate, 0);
        throw new InvalidOperationException(
            $"NativeBuilder.{operation} found an active builder operation.");
    }

    private void FailOperation(Exception failure)
    {
        if (Interlocked.CompareExchange(
            ref _state,
            Disposing,
            Active) != Active)
        {
            return;
        }

        NativeOperationAdmission.Close(
            ref _operationAdmission);
        try
        {
            _session.Abort();
            Volatile.Write(ref _state, Disposed);
            GC.SuppressFinalize(this);
        }
        catch (Exception cleanupFailure)
        {
            Volatile.Write(ref _state, Disposed);
            GC.SuppressFinalize(this);
            throw new AggregateException(
                "A native builder operation failed and cleanup also failed.",
                failure,
                cleanupFailure);
        }
    }

    private void EnsureActive(string operation)
    {
        int state = Volatile.Read(ref _state);
        if (state != Active)
        {
            ThrowInactive(operation, state);
        }
    }

    private static void ThrowInactive(
        string operation,
        int state)
    {
        if (state is Disposed or Finalized)
        {
            throw new ObjectDisposedException(
                $"NativeBuilder<{typeof(T).Name}>",
                $"NativeBuilder.{operation} cannot run after disposal.");
        }

        if (state == Completed)
        {
            throw new InvalidOperationException(
                $"NativeBuilder.{operation} cannot run after completion.");
        }

        throw new InvalidOperationException(
            $"NativeBuilder.{operation} cannot run during a lifetime transition.");
    }

    private void FinalizeBuilder()
    {
        if (Interlocked.CompareExchange(
            ref _state,
            Finalized,
            Active) != Active)
        {
            return;
        }

        NativeOperationAdmission.Close(
            ref _operationAdmission);
        _session.Abort();
    }

    /// <summary>Returns storage when an application abandons an active builder.</summary>
    ~NativeBuilder()
    {
        try
        {
            FinalizeBuilder();
        }
        catch
        {
        }
    }
}

internal sealed class NativeBuilderSession<T>
    where T : unmanaged
{
    private const int Uninitialized = -1;
    private const int Active = 0;
    private const int Completing = 1;
    private const int Completed = 2;
    private const int Releasing = 3;
    private const int Released = 4;

    private readonly NativeOwnerKernel _kernel;
    private NativeGeneration? _generation;
    private NativeBuilderInitialization _initialization;
    private int _state = Uninitialized;
    private int _generationExited;

    internal NativeBuilderSession(
        NativeOwnerKernel kernel)
    {
        _kernel = kernel;
    }

    internal void BeginPool(int preLease) =>
        Attach(
            _kernel.BeginPoolBuilder(preLease),
            "NativePool.CreateBuilder");

    internal void BeginArena(int preLease) =>
        Attach(
            _kernel.BeginArenaBuilder<T>(preLease),
            "NativeArena.CreateBuilder");

    internal int Capacity =>
        _initialization.Allocation.Capacity;

    internal void NotifyBeforeOperation(string operation) =>
        NativeMemoryTestHooks.NotifyBeforeOperationEntry(
            "NativeBuilder." + operation,
            _kernel);

    internal void Validate(string operation)
    {
        EnsureActive();
        _kernel.ValidateBuilderAccess(
            _initialization,
            "NativeBuilder." + operation);
    }

    internal void Append(T value, int index)
    {
        int required = checked(index + 1);
        EnsureCapacity(required, index);
        Validate(nameof(NativeBuilder<T>.Append));
        _initialization.Allocation.SetValue(index, value);
        _initialization.Allocation.InitializedLength = required;
    }

    internal void Append(
        scoped ReadOnlySpan<T> source,
        int start,
        int required)
    {
        EnsureCapacity(required, start);
        Validate(nameof(NativeBuilder<T>.Append));
        source.CopyTo(
            _initialization.Allocation.AsSpan<T>()
                .Slice(start, source.Length));
        _initialization.Allocation.InitializedLength = required;
    }

    internal NativeTransfer<T> Complete(int length)
    {
        if (Interlocked.CompareExchange(
            ref _state,
            Completing,
            Active) != Active)
        {
            throw new InvalidOperationException(
                "The native builder session is not active.");
        }

        try
        {
            _kernel.ValidateBuilderAccess(
                _initialization,
                "NativeBuilder.Complete");
            NativeBuilderCompletion completion =
                _kernel.CompleteBuilder(
                    _initialization,
                    length);
            NativeTransfer<T> transfer = completion.IsBump
                ? NativeTransfer<T>.Create(
                    _kernel,
                    new NativeRegionAllocation(
                        completion.Allocation.Id,
                        completion.Allocation),
                    "NativeBuilder.Complete")
                : NativeTransfer<T>.Create(
                    _kernel,
                    new NativePoolLease(
                        completion.Allocation.Id,
                        completion.Allocation),
                    "NativeBuilder.Complete");
            Volatile.Write(ref _state, Completed);
            ExitGenerationOnce();
            return transfer;
        }
        catch
        {
            try
            {
                _kernel.AbortBuilderInitialization(
                    _initialization);
            }
            finally
            {
                Volatile.Write(ref _state, Released);
                ExitGenerationOnce();
            }

            throw;
        }
    }

    internal void Abort()
    {
        if (Interlocked.CompareExchange(
            ref _state,
            Releasing,
            Active) != Active)
        {
            return;
        }

        try
        {
            _kernel.AbortBuilderInitialization(
                _initialization);
        }
        finally
        {
            Volatile.Write(ref _state, Released);
            ExitGenerationOnce();
        }
    }

    private void EnsureCapacity(
        int required,
        int initializedLength)
    {
        EnsureActive();
        int capacity = Capacity;
        if (required <= capacity)
        {
            return;
        }

        int next = capacity == 0 ? 4 : capacity;
        while (next < required)
        {
            next = next > int.MaxValue / 2
                ? required
                : checked(next * 2);
        }

        _initialization = _kernel.GrowBuilder<T>(
            _initialization,
            next,
            initializedLength);
    }

    private void EnsureActive()
    {
        if (Volatile.Read(ref _state) != Active)
        {
            throw new InvalidOperationException(
                "The native builder session is not active.");
        }
    }

    private void Attach(
        NativeBuilderInitialization initialization,
        string operation)
    {
        try
        {
            NativeMemoryTestHooks.CheckManagedPublicationBoundary(
                operation,
                ordinal: 1,
                "NativeBuilderSession ownership");
            _initialization = initialization;
            _generation = initialization.Generation;
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
                    "Native builder session publication failed and cleanup also failed.",
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

    private void ExitGenerationOnce()
    {
        NativeGeneration generation = _generation
            ?? throw new InvalidOperationException(
                "The native builder session has no generation ownership.");
        if (Interlocked.Exchange(
            ref _generationExited,
            1) == 0)
        {
            _kernel.ExitBuilderGeneration(generation);
        }
    }
}
