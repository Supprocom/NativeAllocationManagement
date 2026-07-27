using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

/// <summary>Builds a canonical SHA-256 stream using little-endian lengths and values.</summary>
public sealed class CanonicalHashAccumulator : IDisposable
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private bool _completed;

    public void AddInt32(int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        _hash.AppendData(bytes);
    }

    public void AddInt64(long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        _hash.AppendData(bytes);
    }

    public void AddUInt16(ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        _hash.AppendData(bytes);
    }

    public void AddInt16(short value) => AddUInt16(unchecked((ushort)value));

    public void AddBytes(ReadOnlySpan<byte> bytes)
    {
        AddInt64(bytes.Length);
        _hash.AppendData(bytes);
    }

    internal void BeginByteSequence(long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        AddInt64(length);
    }

    internal void AppendByteSequencePart(
        ReadOnlySpan<byte> bytes) =>
        _hash.AppendData(bytes);

    public void AddString(string value)
    {
        bool ascii = true;
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] > 0x7F)
            {
                ascii = false;
                break;
            }
        }

        if (!ascii)
        {
            AddBytes(Encoding.UTF8.GetBytes(value));
            return;
        }

        Span<byte> bytes = stackalloc byte[value.Length];
        for (int index = 0; index < value.Length; index++)
        {
            bytes[index] = (byte)value[index];
        }

        AddBytes(bytes);
    }

    public void AddCanonicalInputCell(CanonicalInputCell value)
    {
        AddInt32(value.ChunkId);
        AddInt32(value.CellIndex);
        AddInt32(value.X);
        AddInt32(value.Y);
        AddInt32(value.Z);
        AddInt32(value.Section);
        AddUInt16(value.BlockId);
        AddInt16(value.Density);
    }

    public void AddVertex(Vertex value)
    {
        AddInt32(value.X);
        AddInt32(value.Y);
        AddInt32(value.Z);
        AddInt32(value.Face);
        AddInt32(value.Corner);
        AddInt32(value.BlockId);
    }

    public void AddPayloadSlice(PayloadSlice value)
    {
        AddInt32(value.Offset);
        AddInt32(value.Length);
        AddInt32(value.Alignment);
        AddInt32(value.StageMask);
        AddInt32(value.BlockId);
        AddInt32(value.CellIndex);
    }

    public void AddVertices(ReadOnlySpan<Vertex> values)
    {
        if (BitConverter.IsLittleEndian)
        {
            _hash.AppendData(MemoryMarshal.AsBytes(values));
            return;
        }

        for (int index = 0; index < values.Length; index++)
        {
            AddVertex(values[index]);
        }
    }

    public void AddInt32Values(ReadOnlySpan<int> values)
    {
        if (BitConverter.IsLittleEndian)
        {
            _hash.AppendData(MemoryMarshal.AsBytes(values));
            return;
        }

        for (int index = 0; index < values.Length; index++)
        {
            AddInt32(values[index]);
        }
    }

    public void AddPayloadSlices(ReadOnlySpan<PayloadSlice> values)
    {
        if (BitConverter.IsLittleEndian)
        {
            _hash.AppendData(MemoryMarshal.AsBytes(values));
            return;
        }

        for (int index = 0; index < values.Length; index++)
        {
            AddPayloadSlice(values[index]);
        }
    }

    public string Complete()
    {
        if (_completed)
        {
            throw new InvalidOperationException("The canonical hash has already been completed.");
        }

        _completed = true;
        return Convert.ToHexString(_hash.GetHashAndReset());
    }

    public void Dispose() => _hash.Dispose();
}

public static class VoxelMath
{
    public const int ChunkDimension = 32;
    public const int SectionDimension = 16;
    public const int SectionsPerAxis = ChunkDimension / SectionDimension;
    public const int SectionsPerChunk = SectionsPerAxis * SectionsPerAxis * SectionsPerAxis;
    public const int CellsPerSection =
        SectionDimension * SectionDimension * SectionDimension;
    public const int CellsPerChunk = ChunkDimension * ChunkDimension * ChunkDimension;
    public const int FacesPerCell = 6;
    public const int VerticesPerFace = 4;
    public const int IndicesPerFace = 6;
    public static readonly int VoxelCellBytes = Unsafe.SizeOf<VoxelCell>();
    public static readonly int FaceRecordBytes = Unsafe.SizeOf<FaceRecord>();
    public static readonly int VertexBytes = Unsafe.SizeOf<Vertex>();
    public static readonly int IndexBytes = Unsafe.SizeOf<int>();
    public static readonly int PayloadSliceBytes = Unsafe.SizeOf<PayloadSlice>();
    public static readonly int SectionPrerenderDescriptorBytes =
        Unsafe.SizeOf<SectionPrerenderDescriptor>();
    public const int TransparentMaskWordsPerId = 64;
    public const int DigestModulus = 1_000_000_007;
    public const int DigestMultiplier = 1_000_003;
    public const int AirBlockId = 0;
    public const int IndependentFixtureSeed = 0x13579;

    public static readonly CanonicalInputFixture ExpectedCanonicalInputFixture = new(
        0x13579,
        0,
        [
            new(0, 0, 0, 0, 0, 0, 0, short.MinValue),
            new(0, 16, 16, 0, 0, 1, 256, 118),
            new(0, 512, 0, 16, 0, 2, 257, -68),
            new(0, 528, 16, 16, 0, 3, 259, 12),
            new(0, 16384, 0, 0, 16, 4, 257, -65)
        ],
        400663378);

    public static readonly HandAuthoredInputFixture ExpectedHandAuthoredInputFixture = new(
        0x2468,
        7,
        2,
        2,
        2,
        [
            new(7, 0, 0, 0, 0, 0, 0, short.MinValue),
            new(7, 1, 1, 0, 0, 0, 256, 41),
            new(7, 2, 0, 1, 0, 0, 257, -13),
            new(7, 3, 1, 1, 0, 0, 258, 7),
            new(7, 4, 0, 0, 1, 0, 259, 88),
            new(7, 5, 1, 0, 1, 0, 260, 111),
            new(7, 6, 0, 1, 1, 0, 261, -4),
            new(7, 7, 1, 1, 1, 0, 256, 19)
        ],
        181681900);

    // These are setup-time value LUTs. The measured cells carry only ushort IDs,
    // matching VoxelEngine's runtime BlockType registry contract.
    public static readonly BlockTypeDescriptor[] BlockTypes =
    [
        new(AirBlockId, "air", 0, 1, -128, -32768, 0, 100),
        new(256, "stone", 8, 4, 0, 32, 0b0011, 40),
        new(257, "water", 24, 8, 16, -32768, 0b0101, 18),
        new(258, "foliage", 40, 16, -12, -32768, 0b1001, 9),
        new(259, "metal", 56, 32, 8, 24, 0b0110, 5),
        new(260, "crystal", 72, 16, 24, 18, 0b1110, 2),
        new(261, "moss", 16, 8, -4, -32768, 0b0010, 7)
    ];

    private static readonly ushort[] BlockTypeIndexById = CreateBlockTypeIndex();
    public static readonly byte[] TypeIndexById = CreateTypeIndexById();
    public static readonly bool[] TransparentById = CreateTransparentById();

    public const int TotalFrequencyWeight = 181;

    public static readonly FaceRecord[] IndependentOpaqueRecords =
    [
        CreateFaceRecord(CellIndex(1, 2, 3), 259, 0b100101),
        CreateFaceRecord(CellIndex(7, 4, 2), 256, 0b001010)
    ];

    public static readonly FaceRecord[] IndependentTransparentRecords =
    [
        CreateFaceRecord(CellIndex(4, 6, 1), 258, 0b010001),
        CreateFaceRecord(CellIndex(12, 1, 5), 261, 0b100100)
    ];

    // This is a hand-authored expected output, not a second implementation of the
    // packer. It exercises opaque and transparent streams, four runtime block types,
    // padding, all four corners, all six indices, slices, and the complete upload
    // byte ranges. The two implementations must match these values exactly.
    public static readonly OutputFixture ExpectedIndependentFixture = new(
        [
            new(1, 2, 3, 0, 0, 259), new(1, 2, 4, 0, 1, 259),
            new(1, 3, 4, 0, 2, 259), new(1, 3, 3, 0, 3, 259),
            new(1, 2, 3, 2, 0, 259), new(2, 2, 4, 2, 1, 259),
            new(1, 2, 4, 2, 2, 259), new(2, 2, 3, 2, 3, 259),
            new(1, 2, 4, 5, 0, 259), new(2, 2, 4, 5, 1, 259),
            new(1, 3, 4, 5, 2, 259), new(2, 3, 4, 5, 3, 259),
            new(8, 4, 2, 1, 0, 256), new(8, 4, 3, 1, 1, 256),
            new(8, 5, 3, 1, 2, 256), new(8, 5, 2, 1, 3, 256),
            new(7, 5, 2, 3, 0, 256), new(8, 5, 3, 3, 1, 256),
            new(7, 5, 3, 3, 2, 256), new(8, 5, 2, 3, 3, 256)
        ],
        [0, 1, 2, 2, 3, 0, 4, 5, 6, 6, 7, 4, 8, 9, 10, 10, 11, 8,
            12, 13, 14, 14, 15, 12, 16, 17, 18, 18, 19, 16],
        [
            new(0, 192, 32, 6, 259, 3137),
            new(192, 192, 32, 6, 259, 3137),
            new(384, 192, 32, 6, 259, 3137),
            new(576, 128, 4, 3, 256, 2183),
            new(704, 128, 4, 3, 256, 2183)
        ],
        Convert.FromBase64String(
            "ABIjAABWZwAAmqsAAN7vAAAiMwAAZncAAKq7AADu/wAAMkMAAHaHAAC6ywAA/g8AAEJTAACGlwABAAAAAgAAAAMAAAAAAAAAAAAAAAMBAAABAAAAAgAAAAQAAAAAAAAAAQAAAAMBAAABAAAAAwAAAAQAAAAAAAAAAgAAAAMBAAABAAAAAwAAAAMAAAAAAAAAAwAAAAMBAAAAAAAAAQAAAAIAAAACAAAAAwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABIjAABWZwAAmqsAAN7vAAAiMwAAZncAAKq7AADu/wAAMkMAAHaHAAC6ywAA/g8AAEJTAACGlwABAAAAAgAAAAMAAAACAAAAAAAAAAMBAAACAAAAAgAAAAQAAAACAAAAAQAAAAMBAAABAAAAAgAAAAQAAAACAAAAAgAAAAMBAAACAAAAAgAAAAMAAAACAAAAAwAAAAMBAAAEAAAABQAAAAYAAAAGAAAABwAAAAQAAAAAAAAAAAAAAAAAAAAAAAAAABIjAABWZwAAmqsAAN7vAAAiMwAAZncAAKq7AADu/wAAMkMAAHaHAAC6ywAA/g8AAEJTAACGlwABAAAAAgAAAAQAAAAFAAAAAAAAAAMBAAACAAAAAgAAAAQAAAAFAAAAAQAAAAMBAAABAAAAAwAAAAQAAAAFAAAAAgAAAAMBAAACAAAAAwAAAAQAAAAFAAAAAwAAAAMBAAAIAAAACQAAAAoAAAAKAAAACwAAAAgAAAAAAAAAAAAAAAAAAAAAAAAAbX4AALHCAAAIAAAABAAAAAIAAAABAAAAAAAAAAABAAAIAAAABAAAAAMAAAABAAAAAQAAAAABAAAIAAAABQAAAAMAAAABAAAAAgAAAAABAAAIAAAABQAAAAIAAAABAAAAAwAAAAABAAAMAAAADQAAAA4AAAAOAAAADwAAAAwAAABtfgAAscIAAAcAAAAFAAAAAgAAAAMAAAAAAAAAAAEAAAgAAAAFAAAAAwAAAAMAAAABAAAAAAEAAAcAAAAFAAAAAwAAAAMAAAACAAAAAAEAAAgAAAAFAAAAAgAAAAMAAAADAAAAAAEAABAAAAARAAAAEgAAABIAAAATAAAAEAAAAA=="),
        [
            new(4, 6, 1, 0, 0, 258), new(4, 6, 2, 0, 1, 258),
            new(4, 7, 2, 0, 2, 258), new(4, 7, 1, 0, 3, 258),
            new(4, 6, 1, 4, 0, 258), new(5, 6, 1, 4, 1, 258),
            new(4, 7, 1, 4, 2, 258), new(5, 7, 1, 4, 3, 258),
            new(12, 1, 5, 2, 0, 261), new(13, 1, 6, 2, 1, 261),
            new(12, 1, 6, 2, 2, 261), new(13, 1, 5, 2, 3, 261),
            new(12, 1, 6, 5, 0, 261), new(13, 1, 6, 5, 1, 261),
            new(12, 2, 6, 5, 2, 261), new(13, 2, 6, 5, 3, 261)
        ],
        [0, 1, 2, 2, 3, 0, 4, 5, 6, 6, 7, 4, 8, 9, 10, 10, 11, 8,
            12, 13, 14, 14, 15, 12],
        [
            new(0, 160, 16, 9, 258, 1220),
            new(160, 160, 16, 9, 258, 1220),
            new(320, 136, 8, 2, 261, 5164),
            new(456, 136, 8, 2, 261, 5164)
        ],
        Convert.FromBase64String(
            "pAAA1+gAABssAABfcAAAo7QAAOf4AAArPAAAb4AAALPEAAD3CAAAOwQAAAAGAAAAAQAAAAAAAAAAAAAAAgEAAAQAAAAGAAAAAgAAAAAAAAABAAAAAgEAAAQAAAAHAAAAAgAAAAAAAAACAAAAAgEAAAQAAAAHAAAAAQAAAAAAAAADAAAAAgEAAAAAAAABAAAAAgAAAAIAAAADAAAAAAAAAKQAANfoAAAbLAAAX3AAAKO0AADn+AAAKzwAAG+AAACzxAAA9wgAADsEAAAABgAAAAEAAAAEAAAAAAAAAAIBAAAFAAAABgAAAAEAAAAEAAAAAQAAAAIBAAAEAAAABwAAAAEAAAAEAAAAAgAAAAIBAAAFAAAABwAAAAEAAAAEAAAAAwAAAAIBAAAEAAAABQAAAAYAAAAGAAAABwAAAAQAAAAAQQAAAIUAAADJAAAADQAADAAAAAEAAAAFAAAAAgAAAAAAAAAFAQAADQAAAAEAAAAGAAAAAgAAAAEAAAAFAQAADAAAAAEAAAAGAAAAAgAAAAIAAAAFAQAADQAAAAEAAAAFAAAAAgAAAAMAAAAFAQAACAAAAAkAAAAKAAAACgAAAAsAAAAIAAAAAEEAAACFAAAAyQAAAA0AAAwAAAABAAAABgAAAAUAAAAAAAAABQEAAA0AAAABAAAABgAAAAUAAAABAAAABQEAAAwAAAACAAAABgAAAAUAAAACAAAABQEAAA0AAAACAAAABgAAAAUAAAADAAAABQEAAAwAAAANAAAADgAAAA4AAAAPAAAADAAAAA==")
    );

    public static int SizeOf<T>() where T : unmanaged => Unsafe.SizeOf<T>();

    public static int CellIndex(int x, int y, int z) =>
        checked((z * ChunkDimension + y) * ChunkDimension + x);

    public static int BlockIdForCell(int seed, int chunkIndex, int x, int y, int z)
    {
        return (chunkIndex & 7) switch
        {
            0 => BlockIdForCompleteSectionMix(seed, chunkIndex, x, y, z),
            1 => BlockIdForSparseChunk(seed, chunkIndex, x, y, z),
            2 => BlockIdForUniformChunk(x, y, z),
            3 => BlockIdForPackedChunk(seed, chunkIndex, x, y, z),
            4 => BlockIdForExpandedChunk(seed, chunkIndex, x, y, z),
            5 => BlockIdForMultiPackedChunk(seed, chunkIndex, x, y, z),
            6 => BlockIdForTransparentChunk(seed, chunkIndex, x, y, z),
            _ => BlockIdForTerrainChunk(seed, chunkIndex, x, y, z)
        };
    }

    private static int BlockIdForCompleteSectionMix(
        int seed,
        int chunkIndex,
        int x,
        int y,
        int z)
    {
        int sectionPattern = SectionIndex(x, y, z) % 6;
        if (sectionPattern == 0)
        {
            return AirBlockId;
        }

        if (sectionPattern == 1)
        {
            return 256;
        }

        if (sectionPattern == 2)
        {
            return 257;
        }

        int hash = Hash(seed, chunkIndex, x, y, z);
        return sectionPattern switch
        {
            3 => (hash & 31) == 0 ? 259 : 256,
            4 => (hash % 11) switch
            {
                0 or 1 or 2 or 3 or 4 => 257,
                5 or 6 or 7 => 258,
                8 => 261,
                _ => AirBlockId
            },
            _ => (hash % 13) switch
            {
                0 or 1 => AirBlockId,
                2 or 3 or 4 => 257,
                5 or 6 => 258,
                7 or 8 => 259,
                9 or 10 => 260,
                _ => 261
            }
        };
    }

    private static int BlockIdForSparseChunk(
        int seed,
        int chunkIndex,
        int x,
        int y,
        int z)
    {
        int section = SectionIndex(x, y, z);
        if (section < 6)
        {
            return AirBlockId;
        }

        int hash = Hash(seed, chunkIndex, x, y, z);
        return section == 6
            ? (hash & 31) == 0 ? 256 : AirBlockId
            : (hash & 63) == 0 ? 258 : AirBlockId;
    }

    private static int BlockIdForUniformChunk(int x, int y, int z) =>
        SectionIndex(x, y, z) switch
        {
            0 or 1 or 2 => 256,
            3 => 259,
            4 or 5 => 257,
            6 => AirBlockId,
            _ => 261
        };

    private static int BlockIdForPackedChunk(
        int seed,
        int chunkIndex,
        int x,
        int y,
        int z)
    {
        int hash = Hash(seed, chunkIndex, x >> 1, y >> 1, z >> 1);
        if ((hash & 3) != 0)
        {
            return AirBlockId;
        }

        return (SectionIndex(x, y, z) & 3) switch
        {
            0 => 256,
            1 => 257,
            2 => 259,
            _ => 258
        };
    }

    private static int BlockIdForExpandedChunk(
        int seed,
        int chunkIndex,
        int x,
        int y,
        int z)
    {
        int hash = Hash(seed, chunkIndex, x >> 2, y >> 2, z >> 2);
        return (hash & 3) switch
        {
            0 => AirBlockId,
            1 => 256,
            2 => 257,
            _ => 259
        };
    }

    private static int BlockIdForMultiPackedChunk(
        int seed,
        int chunkIndex,
        int x,
        int y,
        int z)
    {
        int hash = Hash(seed, chunkIndex, x >> 2, y >> 2, z >> 2);
        return (hash % 13) switch
        {
            0 or 1 => AirBlockId,
            2 or 3 or 4 => 256,
            5 or 6 => 257,
            7 or 8 => 258,
            9 or 10 => 259,
            11 => 260,
            _ => 261
        };
    }

    private static int BlockIdForTransparentChunk(
        int seed,
        int chunkIndex,
        int x,
        int y,
        int z)
    {
        int hash = Hash(seed, chunkIndex, x >> 2, y >> 2, z >> 2);
        return (hash & 15) switch
        {
            <= 8 => 257,
            <= 11 => 258,
            <= 13 => 261,
            14 => AirBlockId,
            _ => 256
        };
    }

    private static int BlockIdForTerrainChunk(
        int seed,
        int chunkIndex,
        int x,
        int y,
        int z)
    {
        int columnHash = Hash(seed, chunkIndex, x, 0, z);
        int surface = 9 + columnHash % 14;
        if (y > surface)
        {
            return y <= 15 ? 257 : AirBlockId;
        }

        if (y + 3 < surface)
        {
            return 256;
        }

        return (columnHash % 11) switch
        {
            0 => 258,
            1 => 261,
            2 => 259,
            _ => 256
        };
    }

    public static short DensityForCell(int seed, int chunkIndex, int x, int y, int z, int blockId)
    {
        if (IsAir(blockId))
        {
            return short.MinValue;
        }

        int value = Hash(seed, chunkIndex, x, y, z) % 241 - 120;
        return checked((short)(value + BlockTypeForId(blockId).DensityBias));
    }

    public static bool IsAir(int blockId) => blockId == AirBlockId;

    public static bool IsOccupied(int blockId) => !IsAir(blockId);

    public static bool IsTransparent(int blockId) => IsOccupied(blockId) && BlockTypeForId(blockId).SolidThreshold < 0;

    public static bool IsOpaque(int blockId) => IsOccupied(blockId) && !IsTransparent(blockId);

    public static bool IsSolid(short density, int blockId) => IsOccupied(blockId);

    public static bool FaceVisible(int currentBlockId, int neighborBlockId)
    {
        if (IsAir(currentBlockId))
        {
            return false;
        }

        if (IsOpaque(currentBlockId))
        {
            return IsAir(neighborBlockId) || IsTransparent(neighborBlockId);
        }

        return IsAir(neighborBlockId)
            || IsOpaque(neighborBlockId)
            || neighborBlockId != currentBlockId;
    }

    public static int AlignUp(int value, int alignment)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(alignment);
        int remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }

    public static int StageBytesForFace(int blockId)
    {
        return StageBytesForType(BlockTypeForId(blockId));
    }

    public static int StageBytesForType(BlockTypeDescriptor type)
    {
        return AlignUp(
            checked(type.PayloadBytes + VerticesPerFace * VertexBytes + IndicesPerFace * IndexBytes),
            type.Alignment);
    }

    public static long DigestStep(long state, long value)
    {
        long normalized = value % DigestModulus;
        if (normalized < 0)
        {
            normalized += DigestModulus;
        }

        return (state * DigestMultiplier + normalized + 97) % DigestModulus;
    }

    public static long DigestInt32(long state, int value)
    {
        uint bits = unchecked((uint)value);
        for (int shift = 0; shift < sizeof(int); shift++)
        {
            state = DigestStep(state, (byte)(bits >> (shift * 8)));
        }

        return state;
    }

    public static long DigestUInt16(long state, ushort value)
    {
        for (int shift = 0; shift < sizeof(ushort); shift++)
        {
            state = DigestStep(state, (byte)(value >> (shift * 8)));
        }

        return state;
    }

    public static long DigestInt16(long state, short value) =>
        DigestUInt16(state, unchecked((ushort)value));

    public static long DigestInt64(long state, long value)
    {
        ulong bits = unchecked((ulong)value);
        for (int shift = 0; shift < sizeof(long); shift++)
        {
            state = DigestStep(state, (byte)(bits >> (shift * 8)));
        }

        return state;
    }

    public static long DigestCanonicalInputCell(long state, CanonicalInputCell value)
    {
        state = DigestInt32(state, value.ChunkId);
        state = DigestInt32(state, value.CellIndex);
        state = DigestInt32(state, value.X);
        state = DigestInt32(state, value.Y);
        state = DigestInt32(state, value.Z);
        state = DigestInt32(state, value.Section);
        state = DigestUInt16(state, value.BlockId);
        return DigestInt16(state, value.Density);
    }

    public static CanonicalInputCell CreateCanonicalInputCell(
        int seed,
        int chunkId,
        int cellIndex)
    {
        int x = cellIndex % ChunkDimension;
        int y = (cellIndex / ChunkDimension) % ChunkDimension;
        int z = cellIndex / (ChunkDimension * ChunkDimension);
        int blockId = BlockIdForCell(seed, chunkId, x, y, z);
        return new(
            chunkId,
            cellIndex,
            x,
            y,
            z,
            SectionIndex(x, y, z),
            checked((ushort)blockId),
            DensityForCell(seed, chunkId, x, y, z, blockId));
    }

    public static long ComputeCanonicalInputCellsByteHash(
        int seed,
        int chunkId,
        ReadOnlySpan<CanonicalInputCell> cells)
    {
        long hash = 17;
        hash = DigestInt32(hash, seed);
        hash = DigestInt32(hash, chunkId);
        for (int index = 0; index < cells.Length; index++)
        {
            hash = DigestCanonicalInputCell(hash, cells[index]);
        }

        return hash;
    }

    public static CanonicalInputContract ComputeCanonicalInput(
        VoxelWorkloadOptions options,
        bool includeCells = false)
    {
        long hash = 17;
        hash = DigestInt32(hash, options.Seed);
        hash = DigestInt32(hash, options.ChunkCount);
        hash = DigestInt32(hash, options.WorkerCount);
        hash = DigestInt32(hash, options.Iterations);
        hash = DigestInt32(hash, options.WarmupChunksPerWorker);
        long registryHash = ComputeRegistryHash();
        hash = DigestInt32(hash, BlockTypes.Length);
        hash = DigestInt64(hash, registryHash);
        long chunkOrderHash = 17;
        long cellValueHash = 17;
        long cellCount = 0;
        int measuredChunkCount = checked(options.ChunkCount * options.Iterations);
        List<CanonicalInputCell>? completeCells = includeCells
            ? new List<CanonicalInputCell>(checked(measuredChunkCount * CellsPerChunk))
            : null;
        using CanonicalHashAccumulator strong = new();
        AddCanonicalInputPreamble(strong, options);
        for (int chunk = 0; chunk < measuredChunkCount; chunk++)
        {
            hash = DigestInt32(hash, chunk);
            using CanonicalHashAccumulator chunkStrong = new();
            chunkStrong.AddString("voxel-input-chunk-v1");
            chunkStrong.AddInt32(options.Seed);
            chunkStrong.AddInt32(chunk);
            chunkStrong.AddInt32(CellsPerChunk);
            for (int cell = 0; cell < CellsPerChunk; cell++)
            {
                CanonicalInputCell value = CreateCanonicalInputCell(options.Seed, chunk, cell);
                completeCells?.Add(value);
                chunkStrong.AddCanonicalInputCell(value);
                hash = DigestCanonicalInputCell(hash, value);
                cellValueHash = DigestCanonicalInputCell(cellValueHash, value);
                chunkOrderHash = DigestInt32(chunkOrderHash, chunk);
                chunkOrderHash = DigestInt32(chunkOrderHash, cell);
                cellCount++;
            }

            strong.AddInt32(chunk);
            strong.AddInt64(CellsPerChunk);
            strong.AddString(chunkStrong.Complete());
        }

        return new(
            options,
            BlockTypes.ToArray(),
            cellCount,
            cellValueHash,
            hash,
            chunkOrderHash,
            completeCells?.ToArray(),
            strong.Complete(),
            observed: false);
    }

    /// <summary>Builds the input contract from values observed by an implementation before mutation.</summary>
    public static CanonicalInputContract CreateObservedCanonicalInput(
        VoxelWorkloadOptions options,
        IReadOnlyList<ChunkOutputSummary> chunks)
    {
        ChunkOutputSummary[] ordered = chunks
            .OrderBy(static value => value.ChunkId)
            .ToArray();
        using CanonicalHashAccumulator strong = new();
        AddCanonicalInputPreamble(strong, options);
        long cellCount = 0;
        bool complete = ordered.Length != 0;
        List<CanonicalInputCell>? cells = [];
        long cellValueHash = 17;
        long chunkOrderHash = 17;
        long legacyHash = 17;
        for (int index = 0; index < ordered.Length; index++)
        {
            ChunkOutputSummary chunk = ordered[index];
            strong.AddInt32(chunk.ChunkId);
            strong.AddInt64(chunk.InputCellCount);
            strong.AddString(chunk.StrongInputHash);
            cellCount = checked(cellCount + chunk.InputCellCount);
            CanonicalInputCell[]? observed = chunk.InputCells;
            if (observed is null)
            {
                complete = false;
                continue;
            }

            for (int cell = 0; cell < observed.Length; cell++)
            {
                CanonicalInputCell value = observed[cell];
                cells!.Add(value);
                cellValueHash = DigestCanonicalInputCell(cellValueHash, value);
                chunkOrderHash = DigestInt32(chunkOrderHash, value.ChunkId);
                chunkOrderHash = DigestInt32(chunkOrderHash, value.CellIndex);
                legacyHash = DigestCanonicalInputCell(legacyHash, value);
            }
        }

        return new(
            options,
            BlockTypes.ToArray(),
            cellCount,
            complete ? cellValueHash : 0,
            legacyHash,
            chunkOrderHash,
            complete ? cells!.ToArray() : null,
            strong.Complete(),
            observed: true);
    }

    private static void AddCanonicalInputPreamble(
        CanonicalHashAccumulator hash,
        VoxelWorkloadOptions options)
    {
        hash.AddString("voxel-input-v1");
        hash.AddInt32(options.Seed);
        hash.AddInt32(options.ChunkCount);
        hash.AddInt32(options.WorkerCount);
        hash.AddInt32(options.Iterations);
        hash.AddInt32(options.WarmupChunksPerWorker);
        hash.AddInt32(BlockTypes.Length);
        for (int index = 0; index < BlockTypes.Length; index++)
        {
            BlockTypeDescriptor type = BlockTypes[index];
            hash.AddInt32(type.Id);
            hash.AddString(type.Name);
            hash.AddInt32(type.PayloadBytes);
            hash.AddInt32(type.Alignment);
            hash.AddInt32(type.DensityBias);
            hash.AddInt32(type.SolidThreshold);
            hash.AddInt32(type.StageMask);
            hash.AddInt32(type.FrequencyWeight);
        }
    }

    public static string ComputeStrongCanonicalInputChunkHash(
        int seed,
        int chunkId,
        ReadOnlySpan<CanonicalInputCell> cells)
    {
        using CanonicalHashAccumulator hash = new();
        hash.AddString("voxel-input-chunk-v1");
        hash.AddInt32(seed);
        hash.AddInt32(chunkId);
        hash.AddInt32(cells.Length);
        for (int index = 0; index < cells.Length; index++)
        {
            hash.AddCanonicalInputCell(cells[index]);
        }

        return hash.Complete();
    }

    public static long ComputeRegistryHash()
    {
        long registryHash = 17;
        for (int index = 0; index < BlockTypes.Length; index++)
        {
            BlockTypeDescriptor type = BlockTypes[index];
            registryHash = DigestInt32(registryHash, type.Id);
            byte[] name = Encoding.UTF8.GetBytes(type.Name);
            registryHash = DigestInt32(registryHash, name.Length);
            registryHash = DigestBytes(registryHash, name);
            registryHash = DigestInt32(registryHash, type.PayloadBytes);
            registryHash = DigestInt32(registryHash, type.Alignment);
            registryHash = DigestInt32(registryHash, type.DensityBias);
            registryHash = DigestInt32(registryHash, type.SolidThreshold);
            registryHash = DigestInt32(registryHash, type.StageMask);
            registryHash = DigestInt32(registryHash, type.FrequencyWeight);
        }

        return registryHash;
    }

    public static void ValidateCanonicalInputFixture()
    {
        CanonicalInputFixture fixture = ExpectedCanonicalInputFixture;
        CanonicalInputCell[] expected = fixture.Cells;
        for (int index = 0; index < expected.Length; index++)
        {
            CanonicalInputCell actual = CreateCanonicalInputCell(
                fixture.Seed,
                fixture.ChunkId,
                expected[index].CellIndex);
            if (actual != expected[index])
            {
                throw new InvalidDataException($"Canonical input fixture mismatch at cell {index}: expected {expected[index]}, actual {actual}.");
            }
        }

        long hash = ComputeCanonicalInputCellsByteHash(fixture.Seed, fixture.ChunkId, expected);
        if (fixture.ExpectedByteHash != 0 && hash != fixture.ExpectedByteHash)
        {
            throw new InvalidDataException($"Canonical input fixture hash mismatch: expected {fixture.ExpectedByteHash}, actual {hash}.");
        }
    }

    public static void ValidateHandAuthoredInputFixture()
    {
        HandAuthoredInputFixture fixture = ExpectedHandAuthoredInputFixture;
        int expectedCount = checked(fixture.Width * fixture.Height * fixture.Depth);
        if (fixture.Cells.Length != expectedCount)
        {
            throw new InvalidDataException(
                $"Hand-authored input fixture is incomplete: expected {expectedCount} cells, actual {fixture.Cells.Length}.");
        }

        bool[] seen = new bool[expectedCount];
        for (int index = 0; index < fixture.Cells.Length; index++)
        {
            CanonicalInputCell cell = fixture.Cells[index];
            if (cell.ChunkId != fixture.ChunkId
                || cell.CellIndex != index
                || (uint)cell.X >= fixture.Width
                || (uint)cell.Y >= fixture.Height
                || (uint)cell.Z >= fixture.Depth
                || cell.Section != 0)
            {
                throw new InvalidDataException(
                    $"Hand-authored input fixture has an invalid complete-cell entry at index {index}: {cell}.");
            }

            int coordinateIndex = (cell.Z * fixture.Height + cell.Y) * fixture.Width + cell.X;
            if (seen[coordinateIndex])
            {
                throw new InvalidDataException(
                    $"Hand-authored input fixture repeats coordinate {cell.X},{cell.Y},{cell.Z}.");
            }

            seen[coordinateIndex] = true;
        }

        long hash = ComputeCanonicalInputCellsByteHash(fixture.Seed, fixture.ChunkId, fixture.Cells);
        if (hash != fixture.ExpectedByteHash)
        {
            throw new InvalidDataException(
                $"Hand-authored input fixture hash mismatch: expected {fixture.ExpectedByteHash}, actual {hash}.");
        }
    }

    public static long DigestBytes(long state, ReadOnlySpan<byte> bytes)
    {
        for (int index = 0; index < bytes.Length; index++)
        {
            state = DigestStep(state, bytes[index]);
        }

        return state;
    }

    public static long DigestZeroBytes(long state, int count)
    {
        for (int index = 0; index < count; index++)
        {
            state = DigestStep(state, 0);
        }

        return state;
    }

    public static long DigestMaterializedOutput(
        long state,
        ReadOnlySpan<Vertex> vertices,
        ReadOnlySpan<int> indices,
        ReadOnlySpan<PayloadSlice> slices,
        ReadOnlySpan<byte> upload)
    {
        state = DigestInt32(state, vertices.Length);
        for (int index = 0; index < vertices.Length; index++)
        {
            Vertex value = vertices[index];
            state = DigestInt32(state, value.X);
            state = DigestInt32(state, value.Y);
            state = DigestInt32(state, value.Z);
            state = DigestInt32(state, value.Face);
            state = DigestInt32(state, value.Corner);
            state = DigestInt32(state, value.BlockId);
        }

        state = DigestInt32(state, indices.Length);
        for (int index = 0; index < indices.Length; index++)
        {
            state = DigestInt32(state, indices[index]);
        }

        state = DigestInt32(state, slices.Length);
        for (int index = 0; index < slices.Length; index++)
        {
            PayloadSlice value = slices[index];
            state = DigestInt32(state, value.Offset);
            state = DigestInt32(state, value.Length);
            state = DigestInt32(state, value.Alignment);
            state = DigestInt32(state, value.StageMask);
            state = DigestInt32(state, value.BlockId);
            state = DigestInt32(state, value.CellIndex);
        }

        state = DigestInt32(state, upload.Length);
        return DigestBytes(state, upload);
    }

    /// <summary>Hashes every materialized output element and byte in canonical little-endian order.</summary>
    public static string ComputeStrongOutputHash(
        ReadOnlySpan<Vertex> opaqueVertices,
        ReadOnlySpan<int> opaqueIndices,
        ReadOnlySpan<PayloadSlice> opaqueSlices,
        ReadOnlySpan<byte> opaqueUpload,
        ReadOnlySpan<Vertex> transparentVertices,
        ReadOnlySpan<int> transparentIndices,
        ReadOnlySpan<PayloadSlice> transparentSlices,
        ReadOnlySpan<byte> transparentUpload)
    {
        using CanonicalHashAccumulator hash = new();
        hash.AddString("voxel-output-v2");
        AddOutputStream(hash, "opaque", opaqueVertices, opaqueIndices, opaqueSlices, opaqueUpload);
        AddOutputStream(hash, "transparent", transparentVertices, transparentIndices, transparentSlices, transparentUpload);
        return hash.Complete();
    }

    private static void AddOutputStream(
        CanonicalHashAccumulator hash,
        string name,
        ReadOnlySpan<Vertex> vertices,
        ReadOnlySpan<int> indices,
        ReadOnlySpan<PayloadSlice> slices,
        ReadOnlySpan<byte> upload)
    {
        hash.AddString(name);
        hash.AddInt32(vertices.Length);
        hash.AddVertices(vertices);

        hash.AddInt32(indices.Length);
        hash.AddInt32Values(indices);

        hash.AddInt32(slices.Length);
        hash.AddPayloadSlices(slices);

        hash.AddBytes(upload);
    }

    public static string ComputeStrongPipelineOutputHash(
        IReadOnlyList<ChunkOutputSummary> chunks)
    {
        using CanonicalHashAccumulator hash = new();
        hash.AddString("voxel-pipeline-output-v2");
        ChunkOutputSummary[] ordered = chunks
            .OrderBy(static value => value.ChunkId)
            .ToArray();
        hash.AddInt32(ordered.Length);
        for (int index = 0; index < ordered.Length; index++)
        {
            ChunkOutputSummary chunk = ordered[index];
            hash.AddInt32(chunk.ChunkId);
            hash.AddInt64(chunk.ByteHash);
            hash.AddString(chunk.StrongOutputHash);
            hash.AddInt32(chunk.OpaqueVertexLength);
            hash.AddInt32(chunk.OpaqueIndexLength);
            hash.AddInt32(chunk.OpaqueSliceLength);
            hash.AddInt32(chunk.OpaqueUploadLength);
            hash.AddInt32(chunk.TransparentVertexLength);
            hash.AddInt32(chunk.TransparentIndexLength);
            hash.AddInt32(chunk.TransparentSliceLength);
            hash.AddInt32(chunk.TransparentUploadLength);
        }

        return hash.Complete();
    }

    public static long DigestChunkOutputSummary(long state, ChunkOutputSummary value)
    {
        state = DigestInt32(state, value.ChunkId);
        state = DigestInt64(state, value.ByteHash);
        state = DigestInt32(state, value.OpaqueVertexLength);
        state = DigestInt32(state, value.OpaqueIndexLength);
        state = DigestInt32(state, value.OpaqueSliceLength);
        state = DigestInt32(state, value.OpaqueUploadLength);
        state = DigestInt32(state, value.TransparentVertexLength);
        state = DigestInt32(state, value.TransparentIndexLength);
        state = DigestInt32(state, value.TransparentSliceLength);
        return DigestInt32(state, value.TransparentUploadLength);
    }

    public static ChunkOutputSummary CreateChunkOutputSummary(ChunkResult result) =>
        new(
            result.ChunkId,
            result.OutputByteHash,
            result.OpaqueVertices,
            result.OpaqueIndices,
            result.OpaqueFaces,
            result.OpaqueStagedBytes,
            result.TransparentVertices,
            result.TransparentIndices,
            result.TransparentFaces,
            result.TransparentStagedBytes,
            result.StrongOutputHash,
            result.StrongInputHash,
            result.InputCells?.LongLength ?? VoxelMath.CellsPerChunk,
            result.InputCells);

    public static int FaceCount(int mask) => BitOperations.PopCount((uint)mask);

    public static int NeighborIndex(int cellIndex, int face)
    {
        int x = cellIndex % ChunkDimension;
        int y = (cellIndex / ChunkDimension) % ChunkDimension;
        int z = cellIndex / (ChunkDimension * ChunkDimension);
        int nx = x;
        int ny = y;
        int nz = z;
        switch (face)
        {
            case 0: nx--; break;
            case 1: nx++; break;
            case 2: ny--; break;
            case 3: ny++; break;
            case 4: nz--; break;
            case 5: nz++; break;
            default: throw new ArgumentOutOfRangeException(nameof(face));
        }

        return (uint)nx >= ChunkDimension || (uint)ny >= ChunkDimension || (uint)nz >= ChunkDimension
            ? -1
            : CellIndex(nx, ny, nz);
    }

    public static int FaceMaskFromManaged(int cellIndex, ReadOnlySpan<short> densities, ReadOnlySpan<ushort> materials)
    {
        _ = densities;
        int current = materials[cellIndex];
        int mask = 0;
        for (int face = 0; face < FacesPerCell; face++)
        {
            int neighbor = NeighborIndex(cellIndex, face);
            int neighborBlock = neighbor < 0 ? AirBlockId : materials[neighbor];
            if (FaceVisible(current, neighborBlock))
            {
                mask |= 1 << face;
            }
        }

        return mask;
    }

    public static int FaceMaskFromCells(int cellIndex, ReadOnlySpan<VoxelCell> cells)
    {
        int current = cells[cellIndex].BlockId;
        if (current == AirBlockId)
        {
            return 0;
        }

        bool currentTransparent = TransparentById[current];
        int mask = 0;
        for (int face = 0; face < FacesPerCell; face++)
        {
            int neighbor = NeighborIndex(cellIndex, face);
            int neighborBlock = neighbor < 0 ? AirBlockId : cells[neighbor].BlockId;
            bool visible = neighborBlock == AirBlockId
                || (currentTransparent
                    ? neighborBlock != current
                    : TransparentById[neighborBlock]);
            if (visible)
            {
                mask |= 1 << face;
            }
        }

        return mask;
    }

    public static int PayloadByte(int seed, int cellIndex, int blockId, int slot)
    {
        BlockTypeDescriptor type = BlockTypeForId(blockId);
        return (seed + cellIndex * 11 + blockId * 37 + slot * 17 + type.StageMask * 13) & 0xFF;
    }

    public static BlockTypeDescriptor BlockTypeForId(int blockId)
    {
        if ((uint)blockId > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(blockId));
        }

        ushort index = BlockTypeIndexById[blockId];
        if (index >= BlockTypes.Length || BlockTypes[index].Id != blockId)
        {
            throw new ArgumentOutOfRangeException(nameof(blockId), blockId, "The runtime block registry does not contain this identifier.");
        }

        return BlockTypes[index];
    }

    public static FaceRecord CreateFaceRecord(int cellIndex, int blockId, int mask)
    {
        BlockTypeDescriptor type = BlockTypeForId(blockId);
        return new FaceRecord(
            cellIndex,
            blockId,
            mask,
            type.PayloadBytes,
            type.Alignment,
            type.StageMask,
            StageBytesForType(type));
    }

    public static int BlockTypeIndexForId(int blockId)
    {
        if ((uint)blockId > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(blockId));
        }

        ushort index = BlockTypeIndexById[blockId];
        if (index >= BlockTypes.Length || BlockTypes[index].Id != blockId)
        {
            throw new ArgumentOutOfRangeException(nameof(blockId));
        }

        return index;
    }

    public static int SectionIndex(int x, int y, int z) =>
        checked(((z / SectionDimension) * SectionsPerAxis + (y / SectionDimension))
            * SectionsPerAxis
            + (x / SectionDimension));

    public static SectionSummary ClassifySection(ReadOnlySpan<ushort> materials, ReadOnlySpan<short> densities, int sectionIndex)
    {
        _ = densities;
        Span<int> counts = stackalloc int[BlockTypes.Length];
        Span<int> transparentCounts = stackalloc int[BlockTypes.Length];
        int distinct = 0;
        int transparentIds = 0;
        int firstBlockId = -1;
        int startX = (sectionIndex % SectionsPerAxis) * SectionDimension;
        int startY = ((sectionIndex / SectionsPerAxis) % SectionsPerAxis) * SectionDimension;
        int startZ = (sectionIndex / (SectionsPerAxis * SectionsPerAxis)) * SectionDimension;
        for (int z = startZ; z < startZ + SectionDimension; z++)
        {
            for (int y = startY; y < startY + SectionDimension; y++)
            {
                int row = (z * ChunkDimension + y) * ChunkDimension;
                for (int x = startX; x < startX + SectionDimension; x++)
                {
                    int blockId = materials[row + x];
                    if (firstBlockId < 0)
                    {
                        firstBlockId = blockId;
                    }

                    int index = BlockTypeIndexById[blockId];
                    if (counts[index]++ == 0)
                    {
                        distinct++;
                    }

                    if (TransparentById[blockId] && transparentCounts[index]++ == 0)
                    {
                        transparentIds++;
                    }
                }
            }
        }

        return CreateSectionSummary(
            counts,
            transparentCounts,
            distinct,
            transparentIds,
            firstBlockId);
    }

    public static SectionSummary ClassifySection(ReadOnlySpan<VoxelCell> cells, int sectionIndex)
    {
        Span<int> counts = stackalloc int[BlockTypes.Length];
        Span<int> transparentCounts = stackalloc int[BlockTypes.Length];
        int distinct = 0;
        int transparentIds = 0;
        int firstBlockId = -1;
        int startX = (sectionIndex % SectionsPerAxis) * SectionDimension;
        int startY = ((sectionIndex / SectionsPerAxis) % SectionsPerAxis) * SectionDimension;
        int startZ = (sectionIndex / (SectionsPerAxis * SectionsPerAxis)) * SectionDimension;
        for (int z = startZ; z < startZ + SectionDimension; z++)
        {
            for (int y = startY; y < startY + SectionDimension; y++)
            {
                int row = (z * ChunkDimension + y) * ChunkDimension;
                for (int x = startX; x < startX + SectionDimension; x++)
                {
                    int blockId = cells[row + x].BlockId;
                    if (firstBlockId < 0)
                    {
                        firstBlockId = blockId;
                    }

                    int index = BlockTypeIndexById[blockId];
                    if (counts[index]++ == 0)
                    {
                        distinct++;
                    }

                    if (TransparentById[blockId] && transparentCounts[index]++ == 0)
                    {
                        transparentIds++;
                    }
                }
            }
        }

        return CreateSectionSummary(
            counts,
            transparentCounts,
            distinct,
            transparentIds,
            firstBlockId);
    }

    private static SectionSummary CreateSectionSummary(
        ReadOnlySpan<int> counts,
        ReadOnlySpan<int> transparentCounts,
        int distinct,
        int transparentIds,
        int firstBlockId)
    {
        SectionRepresentationKind kind =
            distinct == 1 && counts[0] == CellsPerSection
            ? SectionRepresentationKind.Empty
            : distinct == 1
                ? SectionRepresentationKind.Uniform
                : distinct <= 2
                    ? SectionRepresentationKind.Packed
                    : distinct <= 4
                        ? SectionRepresentationKind.Expanded
                        : SectionRepresentationKind.MultiPacked;
        int largest = 0;
        int totalTransparent = 0;
        for (int index = 0; index < transparentCounts.Length; index++)
        {
            largest = Math.Max(largest, transparentCounts[index]);
            totalTransparent += transparentCounts[index];
        }

        int emptyCount = counts[0];
        int opaqueCount = checked(
            CellsPerSection - emptyCount - totalTransparent);
        int bitsPerIndex = kind is SectionRepresentationKind.Packed
            or SectionRepresentationKind.MultiPacked
                ? Math.Max(1, BitOperations.Log2((uint)(distinct - 1)) + 1)
                : 0;
        bool dominant = transparentIds > 0 && largest * 2 >= totalTransparent;
        return new SectionSummary(
            kind,
            distinct,
            transparentIds,
            kind == SectionRepresentationKind.Uniform
                ? checked((ushort)firstBlockId)
                : (ushort)0,
            opaqueCount,
            totalTransparent,
            emptyCount,
            bitsPerIndex,
            dominant,
            transparentIds > 1 && !dominant);
    }

    public static int BuildTransparentMasks(
        ReadOnlySpan<ushort> materials,
        ReadOnlySpan<short> densities,
        int sectionIndex,
        Span<ulong> destination)
    {
        _ = densities;
        Span<int> maskSlots = stackalloc int[BlockTypes.Length];
        maskSlots.Fill(-1);
        int transparentIds = 0;
        int startX = (sectionIndex % SectionsPerAxis) * SectionDimension;
        int startY = ((sectionIndex / SectionsPerAxis) % SectionsPerAxis) * SectionDimension;
        int startZ = (sectionIndex / (SectionsPerAxis * SectionsPerAxis)) * SectionDimension;
        for (int z = startZ; z < startZ + SectionDimension; z++)
        {
            for (int y = startY; y < startY + SectionDimension; y++)
            {
                int row = (z * ChunkDimension + y) * ChunkDimension;
                for (int x = startX; x < startX + SectionDimension; x++)
                {
                    int cell = row + x;
                    int blockId = materials[cell];
                    if (!TransparentById[blockId])
                    {
                        continue;
                    }

                    int typeIndex = TypeIndexById[blockId];
                    int maskIndex = maskSlots[typeIndex];
                    if (maskIndex < 0)
                    {
                        maskIndex = maskSlots[typeIndex] = transparentIds++;
                    }

                    int local = ((z - startZ) * 16 + (y - startY)) * 16 + x - startX;
                    destination[maskIndex * TransparentMaskWordsPerId + (local >> 6)] |= 1UL << (local & 63);
                }
            }
        }

        return transparentIds;
    }

    public static int BuildTransparentMasks(ReadOnlySpan<VoxelCell> cells, int sectionIndex, Span<ulong> destination)
    {
        Span<int> maskSlots = stackalloc int[BlockTypes.Length];
        maskSlots.Fill(-1);
        int transparentIds = 0;
        int startX = (sectionIndex % SectionsPerAxis) * SectionDimension;
        int startY = ((sectionIndex / SectionsPerAxis) % SectionsPerAxis) * SectionDimension;
        int startZ = (sectionIndex / (SectionsPerAxis * SectionsPerAxis)) * SectionDimension;
        for (int z = startZ; z < startZ + SectionDimension; z++)
        {
            for (int y = startY; y < startY + SectionDimension; y++)
            {
                int row = (z * ChunkDimension + y) * ChunkDimension;
                for (int x = startX; x < startX + SectionDimension; x++)
                {
                    int cell = row + x;
                    int blockId = cells[cell].BlockId;
                    if (!TransparentById[blockId])
                    {
                        continue;
                    }

                    int typeIndex = TypeIndexById[blockId];
                    int maskIndex = maskSlots[typeIndex];
                    if (maskIndex < 0)
                    {
                        maskIndex = maskSlots[typeIndex] = transparentIds++;
                    }

                    int local = ((z - startZ) * 16 + (y - startY)) * 16 + x - startX;
                    destination[maskIndex * TransparentMaskWordsPerId + (local >> 6)] |= 1UL << (local & 63);
                }
            }
        }

        return transparentIds;
    }

    public static int VertexValue(int cellIndex, int face, int vertex, int field, int blockId)
    {
        int x = cellIndex % ChunkDimension;
        int y = (cellIndex / ChunkDimension) % ChunkDimension;
        int z = cellIndex / (ChunkDimension * ChunkDimension);
        int corner = vertex & 3;
        int xOffset = face switch { 0 => 0, 1 => 1, _ => (corner & 1) },
            yOffset = face switch { 2 => 0, 3 => 1, _ => ((corner >> 1) & 1) },
            zOffset = face switch { 4 => 0, 5 => 1, _ => (corner ^ (corner >> 1)) & 1 };
        return field switch
        {
            0 => x + xOffset,
            1 => y + yOffset,
            2 => z + zOffset,
            3 => face,
            4 => corner,
            5 => blockId,
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
    }

    public static int IndexValue(int vertexOffset, int indexOffset) =>
        vertexOffset + ((indexOffset % IndicesPerFace) switch
        {
            0 => 0,
            1 => 1,
            2 => 2,
            3 => 2,
            4 => 3,
            _ => 0
        });

    public static void ValidateBoundaryFixture()
    {
        int[] opaque = [256, 259, 260];
        int[] transparent = [257, 258, 261];
        int[] materials = [AirBlockId, .. opaque, .. transparent];
        foreach (int current in materials)
        {
            foreach (int neighbor in materials)
            {
                bool expected = !IsAir(current)
                    && (IsOpaque(current)
                        ? IsAir(neighbor) || IsTransparent(neighbor)
                        : IsAir(neighbor) || IsOpaque(neighbor) || neighbor != current);
                if (FaceVisible(current, neighbor) != expected)
                {
                    throw new InvalidDataException(
                        $"The independent opaque/transparent boundary fixture failed for {current}/{neighbor}.");
                }
            }
        }

        int first = VertexValue(CellIndex(1, 2, 3), 5, 0, 0, transparent[0]);
        int second = VertexValue(CellIndex(1, 2, 3), 5, 1, 0, transparent[0]);
        if (first == second)
        {
            throw new InvalidDataException("Face-corner fixture did not distinguish vertex work.");
        }
    }

    private static int Hash(int seed, int chunkIndex, int x, int y, int z)
    {
        unchecked
        {
            uint value = (uint)(seed * 31 + chunkIndex * 113 + x * 13 + y * 29 + z * 43 + x * z * 7);
            value ^= value >> 16;
            value *= 0x7feb352d;
            value ^= value >> 15;
            return (int)(value & 0x7fffffff);
        }
    }

    private static byte[] CreateTypeIndexById()
    {
        byte[] lookup = new byte[ushort.MaxValue + 1];
        for (int index = 0; index < BlockTypes.Length; index++)
        {
            lookup[BlockTypes[index].Id] = checked((byte)index);
        }

        return lookup;
    }

    private static bool[] CreateTransparentById()
    {
        bool[] lookup = new bool[ushort.MaxValue + 1];
        for (int index = 0; index < BlockTypes.Length; index++)
        {
            BlockTypeDescriptor type = BlockTypes[index];
            lookup[type.Id] = type.Id != AirBlockId && type.SolidThreshold < 0;
        }

        return lookup;
    }

    private static ushort[] CreateBlockTypeIndex()
    {
        ushort[] lookup = new ushort[ushort.MaxValue + 1];
        for (ushort index = 0; index < BlockTypes.Length; index++)
        {
            lookup[BlockTypes[index].Id] = index;
        }

        return lookup;
    }
}
