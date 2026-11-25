namespace Supprocom.NativeAllocationManagement;

/// <summary>[wording: MappedView summary]</summary>
/// <typeparam name="T">[wording: element type]</typeparam>
public sealed class MappedView<T> : IDisposable
    where T : unmanaged
{
    private readonly NativeMappedFile _file;
    private readonly SafeBuffer? _buffer;
    private readonly unsafe T* _start;
    private int _released;

    internal unsafe MappedView(NativeMappedFile file, SafeBuffer? buffer, byte* start, long length)
    {
        _file = file;
        Length = length;
        _start = (T*)start;
        if (buffer is not null)
        {
            bool added = false;
            buffer.DangerousAddRef(ref added);
            _buffer = buffer;
        }
    }

    /// <summary>Releases an unreleased mapping reference when the view was never disposed.</summary>
    ~MappedView() => Release();

    /// <summary>[wording: Length]</summary>
    public long Length { get; }

    /// <summary>[wording: IsReadOnly]</summary>
    public bool IsReadOnly => _file.IsReadOnly;

    /// <summary>[wording: IsDisposed]</summary>
    public bool IsDisposed => Volatile.Read(ref _released) != 0 || _file.IsDisposed;

    // TODO(item 3): AsMemory over a MemoryManager<T> that checks IsDisposed on every access.

    /// <summary>[wording: AsReadOnlySpan window]</summary>
    public unsafe ReadOnlySpan<T> AsReadOnlySpan(long start, int length)
    {
        ThrowIfDisposed();
        MappedRange.CheckWindow(start, length, Length);
        return new ReadOnlySpan<T>(_start + start, length);
    }

    /// <summary>[wording: AsReadOnlySpan rest]</summary>
    public ReadOnlySpan<T> AsReadOnlySpan(long start) =>
        AsReadOnlySpan(start, MappedRange.RestOf(start, Length));

    /// <summary>[wording: AsSpan window]</summary>
    public unsafe Span<T> AsSpan(long start, int length)
    {
        ThrowIfDisposed();
        ThrowIfReadOnly();
        MappedRange.CheckWindow(start, length, Length);
        return new Span<T>(_start + start, length);
    }

    /// <summary>[wording: AsSpan rest]</summary>
    public Span<T> AsSpan(long start) =>
        AsSpan(start, MappedRange.RestOf(start, Length));

    /// <summary>[wording: Read]</summary>
    public unsafe T Read(long index)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Length);
        return _start[index];
    }

    /// <summary>[wording: Write]</summary>
    public unsafe void Write(long index, T value)
    {
        ThrowIfDisposed();
        ThrowIfReadOnly();
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Length);
        _start[index] = value;
    }

    /// <summary>[wording: Flush]</summary>
    public void Flush()
    {
        ThrowIfDisposed();
        _file.Flush();
    }

    /// <summary>[wording: Dispose]</summary>
    public void Dispose()
    {
        Release();
        GC.SuppressFinalize(this);
    }

    private void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _buffer?.DangerousRelease();
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _released) != 0)
        {
            throw new ObjectDisposedException("MappedView", "The view is disposed.");
        }

        _file.ThrowIfDisposed();
    }

    private void ThrowIfReadOnly()
    {
        if (_file.IsReadOnly)
        {
            throw new InvalidOperationException("The mapping is read-only.");
        }
    }
}
