using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

/// <summary>Owns one thread-confined fixed native workspace.</summary>
/// <typeparam name="T">The unmanaged element type in the workspace.</typeparam>
public sealed class NativeWorkspace<T> : IDisposable
    where T : unmanaged
{
    private const int Active = 0;
    private const int Released = 1;

    private NativeBlock _block;
    private readonly int _capacity;
    private readonly int _ownerThreadId;
    private readonly long _id = NativeOwnerIdentity.Next();
    private int _state;
    private int _activeUse;
    private int _length;
    private int _published;

    /// <summary>Creates one workspace with a fixed element reservation.</summary>
    /// <param name="preLease">The fixed reservation in elements of <typeparamref name="T"/>.</param>
    public NativeWorkspace(int preLease)
        : this(preLease, budget: null)
    {
    }

    /// <summary>Creates a fixed workspace charged to the shared native extent ceiling.</summary>
    /// <param name="budget">The shared admission domain.</param>
    /// <param name="preLease">The fixed element capacity.</param>
    public NativeWorkspace(NativeMemoryBudget budget, int preLease)
        : this(preLease, budget ?? throw new ArgumentNullException(nameof(budget)))
    {
    }

    private NativeWorkspace(int preLease, NativeMemoryBudget? budget)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(preLease);
        _ownerThreadId = Environment.CurrentManagedThreadId;
        _block = NativeBlockAllocator.Allocate<T>(
            preLease,
            nameof(NativeWorkspace<T>),
            "NativeWorkspace.Constructor",
            budget,
            ownerId: _id);
        _capacity = preLease;
        ClearBlock();
    }

    /// <summary>Gets the stable process-local backing owner identity.</summary>
    public long Id => _id;

    /// <summary>Gets the fixed physical element capacity.</summary>
    public int Capacity
    {
        get
        {
            ValidateAvailable(nameof(Capacity));
            return _capacity;
        }
    }

    /// <summary>Gets the published logical element count.</summary>
    public int Length
    {
        get
        {
            ValidateAvailable(nameof(Length));
            return _length;
        }
    }

    /// <summary>Initializes and publishes one logical range.</summary>
    public void Initialize(
        int length,
        NativeLeaseInitializer<T> initializer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initializer);
        EnterUse(nameof(Initialize));
        try
        {
            ValidateLength(length);
            cancellationToken.ThrowIfCancellationRequested();
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
            ExitUse();
        }
    }

    /// <summary>Runs one bounded mutation callback on the published range.</summary>
    public void Access(NativeLeaseAction<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnterUse(nameof(Access));
        try
        {
            ValidatePublished();
            action(new NativeLeaseView<T>(
                _block.Pointer,
                _length));
        }
        finally
        {
            ExitUse();
        }
    }

    /// <summary>Runs one bounded read callback on the published range.</summary>
    public TResult Read<TResult>(NativeLeaseFunc<T, TResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnterUse(nameof(Read));
        try
        {
            ValidatePublished();
            return action(new NativeLeaseView<T>(
                _block.Pointer,
                _length));
        }
        finally
        {
            ExitUse();
        }
    }

    /// <summary>Processes one fixed native range through two bounded callbacks.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TResult Process<TResult>(
        int length,
        NativeSpanInitializer<T> initializer,
        NativeSpanReader<T, TResult> reader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initializer);
        ArgumentNullException.ThrowIfNull(reader);
        EnterUse(nameof(Process));
        try
        {
            ValidateProcessLength(length);
            cancellationToken.ThrowIfCancellationRequested();
            Span<T> values = CreateSpan(length);
            initializer(values);
            cancellationToken.ThrowIfCancellationRequested();
            return reader(values);
        }
        finally
        {
            ExitUse();
        }
    }

    /// <summary>Processes one fixed native range with explicit callback state.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TResult Process<TState, TResult>(
        int length,
        scoped in TState state,
        NativeSpanStateProcessor<T, TState, TResult> processor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processor);
        EnterUse(nameof(Process));
        try
        {
            ValidateProcessLength(length);
            cancellationToken.ThrowIfCancellationRequested();
            TResult result = processor(CreateSpan(length), state);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            ExitUse();
        }
    }

    /// <summary>Removes logical visibility and retains the fixed native block.</summary>
    public void Reset()
    {
        ValidateAvailable(nameof(Reset));
        _length = 0;
        _published = 0;
    }

    /// <summary>Returns the fixed native block on the owner thread.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1065", Justification = "Disposal must reject active bounded use before freeing native memory.")]
    public void Dispose()
    {
        ValidateOwnerThread(nameof(Dispose));
        if (_activeUse != 0)
        {
            throw new InvalidOperationException(
                "NativeWorkspace.Dispose cannot run during a callback.");
        }

        ReleaseCore();
        GC.SuppressFinalize(this);
    }

    internal object StateForTest => this;

    // Teardown-only attribution. An owner-thread Dispose failure propagates to
    // the caller; a successful release leaves no native backing representation.
    internal static bool IsBackingReleasedForDiagnostics(object state)
    {
        var workspace = (NativeWorkspace<T>)state;
        return workspace._state == Released
            && workspace._block.Pointer == IntPtr.Zero
            && workspace._block.ByteLength == 0
            && workspace._activeUse == 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnterUse(string operation)
    {
        ValidateOwnerThread(operation);
        if (_state != Active)
        {
            ThrowDisposed();
        }

        if (_activeUse != 0)
        {
            throw new InvalidOperationException(
                $"NativeWorkspace.{operation} cannot nest inside an active callback.");
        }

        _activeUse = 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ExitUse()
    {
        _activeUse = 0;
        GC.KeepAlive(this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ValidateAvailable(string operation)
    {
        ValidateOwnerThread(operation);
        if (_state != Active)
        {
            ThrowDisposed();
        }

        if (_activeUse != 0)
        {
            throw new InvalidOperationException(
                $"NativeWorkspace.{operation} cannot run during a callback.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ValidateLength(int length)
    {
        if ((uint)length > (uint)_capacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                "The logical length exceeds the workspace capacity.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ValidateProcessLength(int length)
    {
        ValidateLength(length);
        if (_published != 0)
        {
            throw new InvalidOperationException(
                "NativeWorkspace.Process requires Reset after a published range.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ValidatePublished()
    {
        if (_published == 0)
        {
            throw new InvalidOperationException(
                "NativeWorkspace requires a published range.");
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe Span<T> CreateSpan(int length) =>
        new((void*)_block.Pointer, length);

    private unsafe void ClearBlock()
    {
        new Span<T>((void*)_block.Pointer, _capacity).Clear();
        if (_capacity != 0)
        {
            NativeMemoryAccounting.RecordStorageClear(_block.ByteLength, _block.ByteLength);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowDisposed()
    {
        throw new ObjectDisposedException(
            $"NativeWorkspace<{typeof(T).Name}>");
    }

    /// <summary>Releases abandoned native storage as an emergency action.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "MA0055", Justification = "Emergency native-memory cleanup supplements mandatory deterministic disposal.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "An emergency finalizer must never let cleanup exceptions terminate the process.")]
    ~NativeWorkspace()
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
