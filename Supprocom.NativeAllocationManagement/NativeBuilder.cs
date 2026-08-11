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

    /// <summary>Creates one direct native builder with an optional element reservation.</summary>
    /// <param name="preLease">The initial reservation in elements of <typeparamref name="T"/>.</param>
    public NativeBuilder(int preLease = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(preLease);
        _block = NativeBlockAllocator.Allocate<T>(
            preLease,
            nameof(NativeBuilder<T>),
            "NativeBuilder.Constructor");
        _capacity = preLease;
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
            (cleanupFailures ??= []).Add(cleanupFailure);
        }

        try
        {
            second.FailOperation(failure);
        }
        catch (Exception cleanupFailure)
        {
            (cleanupFailures ??= []).Add(cleanupFailure);
        }

        if (cleanupFailures is null)
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
        source.CopyTo(
            new Span<T>((void*)_block.Pointer, _capacity)
                .Slice(start, source.Length));
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
        _capacity = 0;
        return transfer;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _capacity)
        {
            return;
        }

        int next = _capacity == 0 ? 4 : _capacity;
        while (next < required)
        {
            next = next > int.MaxValue / 2
                ? required
                : checked(next * 2);
        }

        NativeBlock replacement = NativeBlockAllocator.Resize<T>(
            _block,
            next,
            nameof(NativeBuilder<T>),
            "NativeBuilder.Grow");
        _block = replacement;
        _capacity = next;
    }

    private void ReleaseBlock()
    {
        NativeBlock block = _block;
        _block = default;
        _capacity = 0;
        NativeBlockAllocator.Free(block);
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
