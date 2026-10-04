using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

/// <summary>Owns one aligned native range for a mapped GPU upload phase.</summary>
public sealed class MappedGpuBuffer : SafeBuffer
{
    private const nuint Alignment = 64;
    private nint _allocation;

    /// <summary>Creates one aligned native range.</summary>
    public MappedGpuBuffer(nuint byteLength)
        : base(ownsHandle: true)
    {
        ArgumentOutOfRangeException.ThrowIfZero(byteLength);
        nuint allocationLength = checked(
            byteLength + Alignment - 1);
        nint allocation = Marshal.AllocHGlobal(
            checked((nint)allocationLength));
        try
        {
            nuint rawAddress = unchecked((nuint)allocation);
            nuint alignedAddress =
                checked(rawAddress + Alignment - 1)
                & ~(Alignment - 1);
            _allocation = allocation;
            SetHandle(unchecked((nint)alignedAddress));
            Initialize(checked((ulong)byteLength));
        }
        catch
        {
            Marshal.FreeHGlobal(allocation);
            throw;
        }
    }

    /// <summary>Opens safe stream access to the complete mapped range.</summary>
    public UnmanagedMemoryStream OpenStream(
        FileAccess access = FileAccess.ReadWrite) =>
        new(
            this,
            0,
            checked((long)ByteLength),
            access);

    /// <inheritdoc />
    protected override bool ReleaseHandle()
    {
        nint allocation = Interlocked.Exchange(
            ref _allocation,
            0);
        if (allocation != 0)
        {
            Marshal.FreeHGlobal(allocation);
        }

        return true;
    }
}
