using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

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

    private int _state;
    private int _writerGate;
    private int _count;
    private int _capacity;
    private NativeBlock _block;
    private readonly long _id = NativeOwnerIdentity.Next();

    /// <summary>Creates an empty direct native builder.</summary>
    public NativeBuilder()
        : this(0, budget: null)
    {
    }

    /// <summary>Creates a direct native builder with the specified element reservation.</summary>
    /// <param name="preLease">The initial element capacity.</param>
    public NativeBuilder(int preLease)
        : this(preLease, budget: null)
    {
    }

    /// <summary>Creates a builder with an initial capacity charged to one admission ceiling.</summary>
    /// <param name="budget">The shared native extent domain.</param>
    /// <param name="preLease">The initial element capacity.</param>
    public NativeBuilder(NativeMemoryBudget budget, int preLease)
        : this(preLease, budget ?? throw new ArgumentNullException(nameof(budget)))
    {
    }

    private NativeBuilder(int preLease, NativeMemoryBudget? budget)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(preLease);
        _block = NativeBlockAllocator.Allocate(
            checked((nuint)preLease * (nuint)Unsafe.SizeOf<T>()),
            nameof(NativeBuilder<T>),
            "NativeBuilder.Constructor",
            budget,
            ownerId: _id);
        _capacity = preLease;
    }

    /// <summary>Gets the stable backing-ownership lineage, retained through completion and move.</summary>
    public long Id => _id;

    /// <summary>Captures real direct-block ownership and initialized-prefix history, including after completion or disposal.</summary>
    /// <remarks>Capture rejects an entered builder operation, grants no payload authority and does not change the lifetime. The growable block is one storage segment: its initial acquisition counts once; subsequent reallocation is not another segment acquisition. Peaks cover known acquired extents, not opaque backend old/new coexistence or RSS.</remarks>
    public NativeOwnerStatistics GetStatistics()
    {
        EnterObservation();
        try
        {
            return CaptureDirectStatistics();
        }
        finally
        {
            ExitOperation();
            GC.KeepAlive(this);
        }
    }

    /// <summary>Captures the direct block control and numeric history without reopening completed ownership.</summary>
    /// <remarks>A builder has no generations, scope epochs, bump traversal, retired banks, managed roots or quarantine. Its direct block is its one possible retained/active control.</remarks>
    public NativeOwnerDiagnosticSnapshot CaptureDiagnosticSnapshot()
    {
        EnterObservation();
        try
        {
            NativeOwnerStatistics statistics = CaptureDirectStatistics();
            return new NativeOwnerDiagnosticSnapshot(statistics.Lifecycle, 0, 0,
                NativeMemoryAccounting.CurrentMetricsEpoch, statistics.SegmentCount, 0, 0,
                -1, -1, statistics.SegmentCount, statistics.AvailableSegmentCount,
                0, 0, 0, 0, 0, false)
            {
                OwnerId = _id,
                Model = NativeOwnerModel.SingleWriterBuilder,
                OutstandingNativeBytes = statistics.OutstandingNativeBytes,
                PeakOutstandingNativeBytes = statistics.PeakOutstandingNativeBytes,
                InitializedPayloadBytes = statistics.InitializedPayloadBytes,
                PeakInitializedPayloadBytes = statistics.PeakInitializedPayloadBytes
            };
        }
        finally
        {
            ExitOperation();
            GC.KeepAlive(this);
        }
    }

    private void EnterObservation()
    {
        if (Interlocked.CompareExchange(ref _writerGate, 1, 0) != 0)
        {
            throw new InvalidOperationException("A builder observation cannot run during another builder operation.");
        }
    }

    private NativeOwnerStatistics CaptureDirectStatistics()
    {
        int state = Volatile.Read(ref _state);
        NativeOwnerLifecycle lifecycle = state == Active ? NativeOwnerLifecycle.Active
            : state == Completed ? NativeOwnerLifecycle.Returned : NativeOwnerLifecycle.Disposed;
        long initializedBytes = (long)_count * Unsafe.SizeOf<T>();
        long backingBytes = checked((long)_block.ByteLength);
        int blockCount = _block.Pointer == IntPtr.Zero ? 0 : 1;
        return new NativeOwnerStatistics(lifecycle, 0, state == Active ? initializedBytes : 0,
            backingBytes, 0, blockCount, state == Active && _count == 0 ? blockCount : 0,
            0, 0, 0, _capacity == 0 ? 0 : 1)
        {
            OwnerId = _id,
            Model = NativeOwnerModel.SingleWriterBuilder,
            UsableCapacityBytes = backingBytes,
            OutstandingNativeBytes = backingBytes,
            PeakOutstandingNativeBytes = (long)_capacity * Unsafe.SizeOf<T>(),
            InitializedPayloadBytes = state == Active ? initializedBytes : 0,
            PeakInitializedPayloadBytes = initializedBytes
        };
    }

    /// <summary>Gets the initialized element count.</summary>
    public int Count => ReadState(
        nameof(Count),
        readCapacity: false);

    /// <summary>Gets the current native element capacity.</summary>
    public int Capacity => ReadState(
        nameof(Capacity),
        readCapacity: true);

    /// <summary>Reserves total element capacity before production, returning false on budget exhaustion.</summary>
    /// <remarks>
    /// A budget refusal allocates nothing and preserves the builder's prefix and backing.
    /// Actual allocation failure retains the existing terminal-failure policy.
    /// Preferred geometric growth falls back to exact capacity near the ceiling.
    /// </remarks>
    /// <param name="capacity">The required total element capacity, not additional elements.</param>
    /// <returns>True when the requested capacity is ready; false only for budget refusal.</returns>
    public bool TryEnsureCapacity(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        EnterOperation(nameof(TryEnsureCapacity));
        try
        {
            return capacity <= _capacity || Grow(capacity, throwOnBudgetFailure: false);
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

    /// <summary>Appends one value directly to native storage.</summary>
    public void Append(
        T value,
        CancellationToken cancellationToken = default)
    {
        EnterOperation(nameof(Append));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendDirect(value, _count);
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
            AppendDirect(source, _count, required);
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

    /// <summary>Writes one bounded native range and publishes its committed prefix.</summary>
    /// <param name="maximumAdditionalCount">The maximum number of elements for this write.</param>
    /// <param name="action">The callback that writes and commits the initialized prefix.</param>
    /// <param name="cancellationToken">The token that can cancel the complete builder.</param>
    public void Write(
        int maximumAdditionalCount,
        NativeBuilderWriteAction<T> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(
            maximumAdditionalCount);
        ArgumentNullException.ThrowIfNull(action);
        EnterOperation(nameof(Write));
        try
        {
            WriteCore(
                maximumAdditionalCount,
                action,
                cancellationToken);
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

    /// <summary>Writes one bounded native range with explicit callback state.</summary>
    /// <typeparam name="TState">The callback state type.</typeparam>
    /// <param name="maximumAdditionalCount">The maximum number of elements for this write.</param>
    /// <param name="state">The callback state.</param>
    /// <param name="action">The static callback that writes and commits the initialized prefix.</param>
    /// <param name="cancellationToken">The token that can cancel the complete builder.</param>
    public void Write<TState>(
        int maximumAdditionalCount,
        scoped in TState state,
        NativeBuilderWriteStateAction<T, TState> action,
        CancellationToken cancellationToken = default)
        where TState : allows ref struct
    {
        ArgumentOutOfRangeException.ThrowIfNegative(
            maximumAdditionalCount);
        ArgumentNullException.ThrowIfNull(action);
        EnterOperation(nameof(Write));
        try
        {
            IntPtr address = _block.Pointer;
            int count = _count;
            int capacity = _capacity;
            NativeBuilderBorrow<T> borrow = new(
                this,
                ref address,
                ref count,
                ref capacity);
            if (cancellationToken.CanBeCanceled)
            {
                borrow.Write(
                    maximumAdditionalCount,
                    in state,
                    action,
                    cancellationToken);
            }
            else
            {
                borrow.Write(
                    maximumAdditionalCount,
                    in state,
                    action);
            }

            ThrowIfBorrowOperationFailed(count);
            _count = count;
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

    /// <summary>Writes one bounded native range with a compile-time callback.</summary>
    /// <typeparam name="TState">The callback state type.</typeparam>
    /// <typeparam name="TAction">The compile-time callback type.</typeparam>
    /// <param name="maximumAdditionalCount">The maximum element count for this write.</param>
    /// <param name="state">The callback state.</param>
    /// <param name="cancellationToken">The token that can cancel the complete builder.</param>
    public void Write<TState, TAction>(
        int maximumAdditionalCount,
        scoped in TState state,
        CancellationToken cancellationToken = default)
        where TState : allows ref struct
        where TAction : struct, INativeBuilderWriteAction<T, TState>
    {
        ArgumentOutOfRangeException.ThrowIfNegative(
            maximumAdditionalCount);
        EnterOperation(nameof(Write));
        try
        {
            IntPtr address = _block.Pointer;
            int count = _count;
            int capacity = _capacity;
            NativeBuilderBorrow<T> borrow = new(
                this,
                ref address,
                ref count,
                ref capacity);
            if (cancellationToken.CanBeCanceled)
            {
                borrow.Write<TState, TAction>(
                    maximumAdditionalCount,
                    in state,
                    cancellationToken);
            }
            else
            {
                borrow.Write<TState, TAction>(
                    maximumAdditionalCount,
                    in state);
            }

            ThrowIfBorrowOperationFailed(count);
            _count = count;
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

    /// <summary>Provides one exclusive builder borrow to a bounded callback.</summary>
    /// <param name="action">The callback that receives exclusive builder authority.</param>
    /// <param name="cancellationToken">The token that can cancel the complete builder.</param>
    public void Borrow(
        NativeBuilderBorrowAction<T> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnterOperation(nameof(Borrow));
        try
        {
            bool canCancel = cancellationToken.CanBeCanceled;
            if (canCancel)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            IntPtr address = _block.Pointer;
            int count = _count;
            int capacity = _capacity;
            NativeBuilderBorrow<T> borrow = new(
                this,
                ref address,
                ref count,
                ref capacity);
            action(ref borrow);
            if (canCancel)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            ThrowIfBorrowOperationFailed(count);
            _count = count;
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

    /// <summary>Provides one exclusive builder borrow with explicit callback state.</summary>
    /// <typeparam name="TState">The callback state type.</typeparam>
    /// <param name="state">The callback state.</param>
    /// <param name="action">The static callback that receives builder authority.</param>
    /// <param name="cancellationToken">The token that can cancel the complete builder.</param>
    public void Borrow<TState>(
        scoped in TState state,
        NativeBuilderBorrowStateAction<T, TState> action,
        CancellationToken cancellationToken = default)
        where TState : allows ref struct
    {
        ArgumentNullException.ThrowIfNull(action);
        EnterOperation(nameof(Borrow));
        try
        {
            bool canCancel = cancellationToken.CanBeCanceled;
            if (canCancel)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            IntPtr address = _block.Pointer;
            int count = _count;
            int capacity = _capacity;
            NativeBuilderBorrow<T> borrow = new(
                this,
                ref address,
                ref count,
                ref capacity);
            action(ref borrow, in state);
            if (canCancel)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            ThrowIfBorrowOperationFailed(count);
            _count = count;
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

    /// <summary>Provides two exclusive builder borrows to one bounded callback.</summary>
    /// <param name="second">The second builder owner.</param>
    /// <param name="action">The callback that receives both builder authorities.</param>
    /// <param name="cancellationToken">The token that can cancel both builders.</param>
    public void Borrow(
        NativeBuilder<T> second,
        NativeBuilderPairBorrowAction<T> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(second);
        ArgumentNullException.ThrowIfNull(action);
        if (ReferenceEquals(this, second))
        {
            throw new ArgumentException(
                "A composite builder borrow requires two different owners.",
                nameof(second));
        }

        EnterOperation(nameof(Borrow));
        try
        {
            second.EnterOperation(nameof(Borrow));
        }
        catch
        {
            ExitOperation();
            GC.KeepAlive(second);
            GC.KeepAlive(this);
            throw;
        }

        try
        {
            bool canCancel = cancellationToken.CanBeCanceled;
            if (canCancel)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            IntPtr firstAddress = _block.Pointer;
            int firstCount = _count;
            int firstCapacity = _capacity;
            IntPtr secondAddress = second._block.Pointer;
            int secondCount = second._count;
            int secondCapacity = second._capacity;
            NativeBuilderBorrow<T> firstBorrow = new(
                this,
                ref firstAddress,
                ref firstCount,
                ref firstCapacity);
            NativeBuilderBorrow<T> secondBorrow = new(
                second,
                ref secondAddress,
                ref secondCount,
                ref secondCapacity);
            action(ref firstBorrow, ref secondBorrow);
            if (canCancel)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            ThrowIfBorrowOperationFailed(firstCount);
            ThrowIfBorrowOperationFailed(secondCount);
            _count = firstCount;
            second._count = secondCount;
        }
        catch (Exception failure)
        {
            FailPair(this, second, failure);
            throw;
        }
        finally
        {
            second.ExitOperation();
            ExitOperation();
            GC.KeepAlive(second);
            GC.KeepAlive(this);
        }
    }

    /// <summary>Publishes one exact logical range and invalidates this builder.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1816", Justification = "Completion transfers native ownership and disarms the builder finalizer.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "This boundary captures any failure to preserve cleanup and report the original error.")]
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
                CompleteDirect(_count);
            Volatile.Write(ref _state, Completed);
            GC.SuppressFinalize(this);
            return transfer;
        }
        catch (Exception failure)
        {
            Exception? cleanupFailure = null;
            try
            {
                ReleaseBlock();
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
            ReleaseBlock();
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
            return readCapacity
                ? _capacity
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal unsafe Span<T> PrepareBorrowedRange(
        int start,
        int additionalCount,
        ref IntPtr address,
        ref int capacity)
    {
        int required = checked(start + additionalCount);
        if (required > capacity)
        {
            EnsureCapacity(required);
            address = _block.Pointer;
            capacity = _capacity;
        }

        return new Span<T>(
            ((T*)address) + start,
            additionalCount);
    }

    private void WriteCore(
        int maximumAdditionalCount,
        NativeBuilderWriteAction<T> action,
        CancellationToken cancellationToken)
    {
        bool canCancel = cancellationToken.CanBeCanceled;
        if (canCancel)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        int required = checked(
            _count + maximumAdditionalCount);
        Span<T> values = PrepareWriteDirect(
            _count,
            required,
            maximumAdditionalCount);
        if (canCancel)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        int committedCount = -1;
        action(new NativeBuilderWriter<T>(
            values,
            ref committedCount));
        if (canCancel)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        if (committedCount < 0)
        {
            throw new InvalidOperationException(
                "The bounded builder write did not commit an initialized prefix.");
        }

        _count = checked(_count + committedCount);
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
            if (NativeMemoryTestHooks.OperationHooksEnabled)
            {
                NativeMemoryTestHooks.NotifyBeforeOperationEntry(
                    "NativeBuilder." + operation);
            }
            int state = Volatile.Read(ref _state);
            if (state == Active)
            {
                return;
            }

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
        Volatile.Write(ref _writerGate, 0);
    }

    private static void ThrowIfBorrowOperationFailed(
        int count)
    {
        if (count < 0)
        {
            throw new InvalidOperationException(
                "The builder borrow contains a failed operation.");
        }
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

    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1816", Justification = "Failure cleanup releases native storage and disarms the builder finalizer.")]
    private void FailOperation(Exception failure)
    {
        if (Interlocked.CompareExchange(
            ref _state,
            Disposing,
            Active) != Active)
        {
            return;
        }

        try
        {
            ReleaseBlock();
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

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "This boundary captures any failure to preserve cleanup and report the original error.")]
    private static void FailPair(
        NativeBuilder<T> first,
        NativeBuilder<T> second,
        Exception failure)
    {
        List<Exception>? cleanupFailures = null;
        try
        {
            first.FailOperation(failure);
        }
        catch (Exception cleanupFailure)
        {
            cleanupFailures = [cleanupFailure];
        }

        try
        {
            second.FailOperation(failure);
        }
        catch (Exception cleanupFailure)
        {
            (cleanupFailures ??= []).Add(cleanupFailure);
        }

        // Either native owner cleanup can throw; preserve the original failure when neither does.
#pragma warning disable CA1508
        if (cleanupFailures is null)
#pragma warning restore CA1508
        {
            return;
        }

        cleanupFailures.Insert(0, failure);
        throw new AggregateException(
            "A composite native builder operation failed during cleanup.",
            cleanupFailures);
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

        ReleaseBlock();
    }

    private unsafe void AppendDirect(T value, int index)
    {
        int required = checked(index + 1);
        EnsureCapacity(required);
        new Span<T>((void*)_block.Pointer, _capacity)[index] = value;
    }

    private unsafe void AppendDirect(
        scoped ReadOnlySpan<T> source,
        int start,
        int required)
    {
        EnsureCapacity(required);
        Span<T> copyTarget = new Span<T>((void*)_block.Pointer, _capacity)
            .Slice(start, source.Length);
        source.CopyTo(copyTarget);
        NativeMemoryAccounting.RecordCopiedRange(source, copyTarget);
    }

    private unsafe Span<T> PrepareWriteDirect(
        int start,
        int required,
        int maximumAdditionalCount)
    {
        EnsureCapacity(required);
        return new Span<T>((void*)_block.Pointer, _capacity)
            .Slice(start, maximumAdditionalCount);
    }

    private NativeTransfer<T> CompleteDirect(int length)
    {
        NativeBlock block = _block;
        NativeTransfer<T> transfer =
            NativeTransfer<T>.CreateOwnedBlock(
                block,
                length,
                _capacity);
        _block = default;
        return transfer;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _capacity)
        {
            return;
        }

        _ = Grow(required, throwOnBudgetFailure: true);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool Grow(int required, bool throwOnBudgetFailure)
    {
        int next = _capacity == 0 ? 4 : _capacity;
        while (next < required)
        {
            next = next > int.MaxValue / 2
                ? required
                : checked(next * 2);
        }

        if (!NativeBlockAllocator.TryResize<T>(
            _block,
            next,
            required,
            nameof(NativeBuilder<T>),
            "NativeBuilder.Grow",
            _block.Budget,
            throwOnBudgetFailure,
            out NativeBlock replacement,
            out int capacity,
            ownerId: _id))
        {
            return false;
        }

        _block = replacement;
        _capacity = capacity;
        return true;
    }

    private void ReleaseBlock()
    {
        NativeBlock block = _block;
        _block = default;
        NativeBlockAllocator.Free(block);
    }

    /// <summary>Returns storage when an application abandons an active builder.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "MA0055", Justification = "Emergency native-memory cleanup supplements mandatory deterministic disposal.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "An emergency finalizer must never let cleanup exceptions terminate the process.")]
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
