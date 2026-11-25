using System.IO.MemoryMappedFiles;

namespace Supprocom.NativeAllocationManagement;

/// <summary>[wording: NativeMappedFile summary]</summary>
public sealed class NativeMappedFile : IDisposable
{
    private readonly MemoryMappedFile? _map;
    private readonly MemoryMappedViewAccessor? _accessor;
    private readonly SafeBuffer? _buffer;
    private readonly NativeSegment? _segment;
    private int _disposed;

    private NativeMappedFile(string path, bool writable)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        IsReadOnly = !writable;
        FileStream stream = new(
            path,
            FileMode.Open,
            writable ? FileAccess.ReadWrite : FileAccess.Read,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.None);
        try
        {
            Length = stream.Length;
            if (Length == 0)
            {
                stream.Dispose();
                return;
            }

            MemoryMappedFileAccess access = writable ? MemoryMappedFileAccess.ReadWrite : MemoryMappedFileAccess.Read;
            _map = MemoryMappedFile.CreateFromFile(
                stream,
                mapName: null,
                capacity: 0,
                access,
                HandleInheritability.None,
                leaveOpen: false);
            _accessor = _map.CreateViewAccessor(0, 0, access);
            _buffer = _accessor.SafeMemoryMappedViewHandle;
            // One borrow of the whole mapping from offset 0 (page aligned): Borrow does the
            // reference count, range, overflow and alignment checks; views window inside it.
            _segment = NativeSegment.Borrow(_buffer, 0, checked((nuint)Length));
        }
        catch
        {
            _accessor?.Dispose();
            _map?.Dispose();
            stream.Dispose();
            throw;
        }
    }

    /// <summary>[wording: Length]</summary>
    public long Length { get; }

    /// <summary>[wording: IsReadOnly]</summary>
    public bool IsReadOnly { get; }

    /// <summary>[wording: IsDisposed]</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>[wording: OpenRead]</summary>
    public static NativeMappedFile OpenRead(string path) => new(path, writable: false);

    /// <summary>[wording: OpenReadWrite]</summary>
    public static NativeMappedFile OpenReadWrite(string path) => new(path, writable: true);

    /// <summary>[wording: View whole file]</summary>
    public MappedView<T> View<T>()
        where T : unmanaged
    {
        ThrowIfDisposed();
        int size = MappedRange.SizeOf<T>();
        if (Length % size != 0)
        {
            throw new InvalidOperationException(
                $"The file length {Length} is not a multiple of the element size {size}.");
        }

        return View<T>(0, Length / size);
    }

    /// <summary>[wording: View range]</summary>
    public MappedView<T> View<T>(long byteOffset, long count)
        where T : unmanaged
    {
        ThrowIfDisposed();
        MappedRange.CheckView<T>(byteOffset, count, Length);
        // Each view takes its own borrow of the mapping, so it stays mapped until the view ends.
        NativeSegment? segment = _buffer is null ? null : NativeSegment.Borrow(_buffer, 0, checked((nuint)Length));
        return new MappedView<T>(this, segment, byteOffset, count);
    }

    /// <summary>[wording: Flush]</summary>
    public void Flush()
    {
        ThrowIfDisposed();
        if (!IsReadOnly)
        {
            _accessor?.Flush();
        }
    }

    /// <summary>[wording: Dispose]</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (!IsReadOnly)
        {
            _accessor?.Flush();
        }

        // Views keep their own references; the mapping ends when the last one is released.
        _segment?.FreeNow();
        _accessor?.Dispose();
        _map?.Dispose();
    }

    internal void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(IsDisposed, this);
}
