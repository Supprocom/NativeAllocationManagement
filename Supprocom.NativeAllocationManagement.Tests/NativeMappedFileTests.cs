using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeMappedFileTests : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Tick
    {
        public long Time;
        public double Price;
    }

    private const int TickCount = 1024;
    private readonly string _directory;

    public NativeMappedFileTests()
    {
        // The smallest fixtures live on tmpfs when it exists.
        string root = Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath();
        _directory = Path.Combine(root, "nam-mapped-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Fixture(int ticks = TickCount, int headerBytes = 0)
    {
        string path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".bin");
        using FileStream stream = File.Create(path);
        stream.Write(new byte[headerBytes]);
        Tick[] data = new Tick[ticks];
        for (int i = 0; i < ticks; i++)
        {
            data[i] = new Tick { Time = i, Price = i * 0.5 };
        }

        stream.Write(MemoryMarshal.AsBytes(data.AsSpan()));
        return path;
    }

    [Fact]
    public void ReadOnlyViewReadsTypedElementsAndWindows()
    {
        using NativeMappedFile file = NativeMappedFile.OpenRead(Fixture());
        using MappedView<Tick> view = file.View<Tick>();
        Assert.Equal(TickCount, view.Length);
        Assert.True(view.IsReadOnly);
        Assert.Equal(7, view.Get(7).Time);
        ReadOnlySpan<Tick> window = view.AsReadOnlySpan(100, 16);
        Assert.Equal(16, window.Length);
        Assert.Equal(115, window[15].Time);
        Assert.Equal(511.5, view.AsReadOnlySpan(TickCount - 1)[0].Price);
    }

    [Fact]
    public void ViewHonorsOffsetAndCount()
    {
        using NativeMappedFile file = NativeMappedFile.OpenRead(Fixture(headerBytes: 16));
        using MappedView<Tick> view = file.View<Tick>(16, TickCount);
        Assert.Equal(3, view.Get(3).Time);
        Assert.Throws<ArgumentOutOfRangeException>(() => file.View<Tick>(16, TickCount + 1));
        Assert.Throws<ArgumentException>(() => file.View<Tick>(4, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => file.View<Tick>(-8, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => file.View<Tick>(0, -1));
        using NativeMappedFile odd = NativeMappedFile.OpenRead(Fixture(headerBytes: 4));
        Assert.Throws<InvalidOperationException>(() => odd.View<Tick>());
    }

    [Fact]
    public void WritableMappingWritesBackOnFlushAndDispose()
    {
        string path = Fixture();
        using (NativeMappedFile file = NativeMappedFile.OpenReadWrite(path))
        using (MappedView<Tick> view = file.View<Tick>())
        {
            view.AsSpan(10, 2)[1] = new Tick { Time = -5, Price = 1 };
            view.Set(0, new Tick { Time = -1, Price = 2 });
            view.Flush();
            using FileStream other = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] raw = new byte[Marshal.SizeOf<Tick>()];
            other.Seek(11 * raw.Length, SeekOrigin.Begin);
            other.ReadExactly(raw);
            Assert.Equal(-5, MemoryMarshal.Read<Tick>(raw).Time);
        }

        using NativeMappedFile reopened = NativeMappedFile.OpenRead(path);
        using MappedView<Tick> back = reopened.View<Tick>();
        Assert.Equal(-1, back.Get(0).Time);
    }

    [Fact]
    public void ReadOnlyMappingRejectsWritableSpansAndWrites()
    {
        using NativeMappedFile file = NativeMappedFile.OpenRead(Fixture());
        using MappedView<Tick> view = file.View<Tick>();
        Assert.Throws<InvalidOperationException>(() => view.AsSpan(0, 1));
        Assert.Throws<InvalidOperationException>(() => view.AsSpan(0));
        Assert.Throws<InvalidOperationException>(() => view.Set(0, default));
        view.Flush();
    }

    [Fact]
    public void WindowsAreCheckedAgainstTheView()
    {
        using NativeMappedFile file = NativeMappedFile.OpenRead(Fixture());
        using MappedView<Tick> view = file.View<Tick>(0, 100);
        Assert.Throws<ArgumentOutOfRangeException>(() => view.AsReadOnlySpan(-1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => view.AsReadOnlySpan(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => view.AsReadOnlySpan(50, 51));
        Assert.Throws<ArgumentOutOfRangeException>(() => view.AsReadOnlySpan(101));
        Assert.Throws<ArgumentOutOfRangeException>(() => view.Get(100));
        Assert.Equal(0, view.AsReadOnlySpan(100).Length);
        Assert.Equal(50, view.AsReadOnlySpan(50).Length);
    }

    [Fact]
    public void ViewsAreIndependentAndDisposeIsIdempotent()
    {
        using NativeMappedFile file = NativeMappedFile.OpenRead(Fixture());
        MappedView<Tick> a = file.View<Tick>(0, 10);
        using MappedView<Tick> b = file.View<Tick>(16 * 5, 10);
        a.Dispose();
        a.Dispose();
        Assert.True(a.IsDisposed);
        Assert.False(b.IsDisposed);
        Assert.Equal(5, b.Get(0).Time);
        Assert.Throws<ObjectDisposedException>(() => a.Get(0));
        Assert.Throws<ObjectDisposedException>(() => a.AsReadOnlySpan(0, 1));
        Assert.Throws<ObjectDisposedException>(a.Flush);
    }

    [Fact]
    public void DisposedFileRejectsItsViewsAndNewViews()
    {
        NativeMappedFile file = NativeMappedFile.OpenRead(Fixture());
        MappedView<Tick> view = file.View<Tick>();
        file.Dispose();
        file.Dispose();
        Assert.True(file.IsDisposed);
        Assert.True(view.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => view.Get(0));
        Assert.Throws<ObjectDisposedException>(() => view.AsReadOnlySpan(0, 1));
        Assert.Throws<ObjectDisposedException>(() => file.View<Tick>());
        Assert.Throws<ObjectDisposedException>(file.Flush);
        view.Dispose();
    }

    [Fact]
    public void SpanTakenBeforeFileDisposeStaysMappedWhileViewIsAlive()
    {
        NativeMappedFile file = NativeMappedFile.OpenRead(Fixture());
        using MappedView<Tick> view = file.View<Tick>();
        ReadOnlySpan<Tick> span = view.AsReadOnlySpan(0, 4);
        file.Dispose();
        // The view's own reference keeps the pages mapped, so this reads data, not freed memory.
        Assert.Equal(3, span[3].Time);
    }

    [Fact]
    public void EmptyFileOpensWithAnEmptyView()
    {
        string path = Path.Combine(_directory, "empty.bin");
        File.WriteAllBytes(path, []);
        using NativeMappedFile file = NativeMappedFile.OpenRead(path);
        using MappedView<byte> view = file.View<byte>();
        Assert.Equal(0, file.Length);
        Assert.Equal(0, view.Length);
        Assert.True(view.AsReadOnlySpan(0).IsEmpty);
        Assert.Throws<ArgumentOutOfRangeException>(() => file.View<byte>(0, 1));
    }

    [Fact]
    public void MissingFileAndBadPathsThrowAtOpen()
    {
        Assert.Throws<FileNotFoundException>(() => NativeMappedFile.OpenRead(Path.Combine(_directory, "none.bin")));
        Assert.Throws<ArgumentException>(() => NativeMappedFile.OpenRead(""));
        Assert.Throws<ArgumentNullException>(() => NativeMappedFile.OpenRead(null!));
    }

    [Fact]
    public void OpenFileBlocksAWriterWhileMapped()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Unix share modes are advisory and do not refuse a second writer.
            return;
        }

        string path = Fixture();
        using NativeMappedFile file = NativeMappedFile.OpenRead(path);
        Assert.Throws<IOException>(() => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
    }

    [Fact]
    public void WindowMathHandlesRangesBeyondTwoGiB()
    {
        const long count = 6_000_000_000;
        MappedRange.CheckWindow(5_999_999_990, 10, count);
        Assert.Throws<ArgumentOutOfRangeException>(() => MappedRange.CheckWindow(5_999_999_990, 11, count));
        Assert.Equal(10, MappedRange.RestOf(5_999_999_990, count));
        Assert.Throws<ArgumentOutOfRangeException>(() => MappedRange.RestOf(0, count));
        MappedRange.CheckView<Tick>(0, count, count * 16);
        MappedRange.CheckView<Tick>(8, count - 1, count * 16);
        Assert.Throws<ArgumentOutOfRangeException>(() => MappedRange.CheckView<Tick>(8, count, count * 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => MappedRange.CheckView<Tick>(0, long.MaxValue, count * 16));
    }
}
