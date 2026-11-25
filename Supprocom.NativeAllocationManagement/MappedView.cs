namespace Supprocom.NativeAllocationManagement;

/// <summary>[wording: MappedView summary]</summary>
/// <typeparam name="T">[wording: element type]</typeparam>
public sealed class MappedView<T> : IDisposable
    where T : unmanaged
{
    private readonly NativeMappedFile _file;
    private readonly NativeSegment? _segment;
    private readonly unsafe T* _start;
    private int _released;

    internal unsafe MappedView(NativeMappedFile file, NativeSegment? segment, long byteOffset, long length)
    {
        _file = file;
        _segment = segment;
        Length = length;
        _start = segment is null ? null : (T*)((byte*)segment.Pointer + byteOffset);
    }

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

    /// <summary>[wording: Get]</summary>
    public unsafe T Get(long index)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Length);
        return _start[index];
    }

    /// <summary>[wording: Set]</summary>
    public unsafe void Set(long index, T value)
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
    }

    private void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _segment?.FreeNow();
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
