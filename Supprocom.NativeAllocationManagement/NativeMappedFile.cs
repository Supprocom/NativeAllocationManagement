using System.IO.MemoryMappedFiles;

namespace Supprocom.NativeAllocationManagement;

/// <summary>[wording: NativeMappedFile summary]</summary>
public sealed class NativeMappedFile : IDisposable
{
    private readonly MemoryMappedFile? _map;
    private readonly MemoryMappedViewAccessor? _accessor;
    private readonly SafeBuffer? _buffer;
    private readonly unsafe byte* _baseAddress;
    private int _disposed;

    private unsafe NativeMappedFile(string path, bool writable)
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
            SafeBuffer buffer = _accessor.SafeMemoryMappedViewHandle;
            byte* pointer = null;
            buffer.AcquirePointer(ref pointer);
            _baseAddress = pointer + _accessor.PointerOffset;
            _buffer = buffer;
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
    public unsafe MappedView<T> View<T>(long byteOffset, long count)
        where T : unmanaged
    {
        ThrowIfDisposed();
        MappedRange.CheckView<T>(byteOffset, count, Length);
        return new MappedView<T>(this, _buffer, _baseAddress + byteOffset, count);
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
    public unsafe void Dispose()
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
        _buffer?.ReleasePointer();
        _accessor?.Dispose();
        _map?.Dispose();
    }

    internal void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(IsDisposed, this);
}
