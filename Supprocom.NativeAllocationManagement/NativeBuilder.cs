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
    private int _borrowEpoch;
    private int _activeBorrowAuthority;
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

    /// <summary>Provides one exclusive builder borrow to a bounded callback.</summary>
    /// <param name="action">The callback that receives exclusive builder authority.</param>
    /// <param name="cancellationToken">The token that can cancel the complete builder.</param>
    public void Borrow(
        NativeBuilderBorrowAction<T> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnterOperation(nameof(Borrow));
        int authority = NextBorrowAuthority();
        Volatile.Write(
            ref _activeBorrowAuthority,
            authority);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            NativeBuilderBorrow<T> borrow = new(
                this,
                authority);
            action(ref borrow);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateBorrowAuthority(authority);
        }
        catch (Exception failure)
        {
            FailOperation(failure);
            throw;
        }
        finally
        {
            Interlocked.CompareExchange(
                ref _activeBorrowAuthority,
                0,
                authority);
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

    internal int ReadBorrowedState(
        int authority,
        bool readCapacity)
    {
        ValidateBorrowAuthority(authority);
        return readCapacity
            ? _capacity
            : _count;
    }

    internal void AppendBorrowed(
        int authority,
        T value,
        CancellationToken cancellationToken)
    {
        ValidateBorrowAuthority(authority);
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
    }

    internal void AppendBorrowed(
        int authority,
        scoped ReadOnlySpan<T> source,
        CancellationToken cancellationToken)
    {
        ValidateBorrowAuthority(authority);
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
    }

    internal void WriteBorrowed(
        int authority,
        int maximumAdditionalCount,
        NativeBuilderWriteAction<T> action,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(
            maximumAdditionalCount);
        ArgumentNullException.ThrowIfNull(action);
        ValidateBorrowAuthority(authority);
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
    }

    private void WriteCore(
        int maximumAdditionalCount,
        NativeBuilderWriteAction<T> action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int required = checked(
            _count + maximumAdditionalCount);
        Span<T> values = PrepareWriteDirect(
            _count,
            required,
            maximumAdditionalCount);
        cancellationToken.ThrowIfCancellationRequested();

        int committedCount = -1;
        action(new NativeBuilderWriter<T>(
            values,
            ref committedCount));
        cancellationToken.ThrowIfCancellationRequested();
        if (committedCount < 0)
        {
            throw new InvalidOperationException(
                "The bounded builder write did not commit an initialized prefix.");
        }

        _count = checked(_count + committedCount);
    }

    private int NextBorrowAuthority()
    {
        int authority = Interlocked.Increment(
            ref _borrowEpoch);
        if (authority != 0)
        {
            return authority;
        }

        return Interlocked.Increment(ref _borrowEpoch);
    }

    private void ValidateBorrowAuthority(int authority)
    {
        if (authority == 0
            || Volatile.Read(ref _activeBorrowAuthority)
                != authority
            || Volatile.Read(ref _state) != Active
            || Volatile.Read(ref _writerGate) != 1)
        {
            throw new InvalidOperationException(
                "The native builder borrow is not active in its callback.");
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
