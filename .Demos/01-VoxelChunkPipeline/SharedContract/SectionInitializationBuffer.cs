namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

/// <summary>Provides checked initialization access to one exact typed range.</summary>
/// <typeparam name="T">The element type in the range.</typeparam>
public interface ISectionInitializationBuffer<T>
    where T : unmanaged
{
    /// <summary>Gets the element count.</summary>
    int Length { get; }

    /// <summary>Writes the next element in the range.</summary>
    void Append(T value);

    /// <summary>Reads one element that the current initializer already wrote.</summary>
    T ReadInitialized(int index);

    /// <summary>Completes the exact output range.</summary>
    void Complete();

    /// <summary>Adds one initialized range to the section content tag.</summary>
    uint MixInitialized(uint hash, int start, int length);
}

/// <summary>Provides checked indexed writes during exact output initialization.</summary>
/// <typeparam name="T">The element type in the output range.</typeparam>
public interface IOutputInitializationBuffer<T>
{
    /// <summary>Gets the element count.</summary>
    int Length { get; }

    /// <summary>Writes one element.</summary>
    void Write(int index, T value);

    /// <summary>Writes one contiguous source range.</summary>
    void Write(int start, scoped ReadOnlySpan<T> source);

    /// <summary>Writes one value to a contiguous range.</summary>
    void Fill(int start, int length, T value);
}
