using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

internal enum NativeOwnerKind
{
    Pool,
    Region,
    Arena
}

/// <summary>Describes the lifecycle gate state of a native owner.</summary>
public enum NativeOwnerLifecycle
{
    /// <summary>The owner accepts allocations and bounded native operations.</summary>
    Active,
    /// <summary>A lifecycle transition has claimed the owner gate.</summary>
    Returning,
    /// <summary>A lease-generation transition has claimed the owner gate.</summary>
    RollingOver,
    /// <summary>The current generation is stale and cannot be used.</summary>
    Returned,
    /// <summary>The owner is permanently closed.</summary>
    Disposed,
    /// <summary>The owner-shaped value was never constructed.</summary>
    Uninitialized,
    /// <summary>The owner was declared without leasing its first generation.</summary>
    Unleased
}

internal enum NativeAllocationLifecycle
{
    Active,
    Returning,
    Returned
}

internal readonly record struct NativePoolLease(long Generation, long AllocationId, int Length, int Capacity);

internal readonly record struct NativeRegionAllocation(long Generation, long AllocationId, int Length, int Capacity);

internal readonly record struct NativeHandleMetadata(int Length, int Capacity);

internal readonly record struct NativeMemoryTestMetrics(
    long AllocationCount,
    long ZeroedAllocationCount,
    long FreeCount,
    long DetachedGenerationCount,
    long OutstandingNativeBytes,
    long DetachedNativeBytes,
    long RetiredNativeBytes,
    long BumpTraversalVisitCount)
{
    internal long RetainedNativeBytes => OutstandingNativeBytes - DetachedNativeBytes;
}

internal static class NativeMemoryTestHooks
{
    private static long _allocationCount;
    private static long _zeroedAllocationCount;
    private static long _freeCount;
    private static long _detachedGenerationCount;
    private static long _outstandingNativeBytes;
    private static long _detachedNativeBytes;
    private static long _retiredNativeBytes;
    private static long _bumpTraversalVisitCount;
    private static long _metricsEpoch;
    private static int _forcedFailures;
    private static int _forcedClearFailures;
    private static int _forcedCommitBoundary;
    private static int _commitBoundary;
    private static int _forcedPostCommitBoundary;
    private static int _postCommitBoundary;
    private static int _forcedRetiredSnapshotPreparation;
    private static int _forcedQuarantineReservation;
    private static Action<string>? _operationEntered;
    private static Action<string>? _beforeOperationEntry;
    private static Action<string, NativeOwnerKernel>? _beforeOperationEntryWithKernel;
    private static Action<string, NativeOwnerKernel, long, long>? _operationEnteredWithAllocation;
    private static Action<string, NativeGenerationOwner>? _operationEnteredWithGenerationOwner;

    internal static void Reset()
    {
        Interlocked.Increment(ref _metricsEpoch);
        Interlocked.Exchange(ref _allocationCount, 0);
        Interlocked.Exchange(ref _zeroedAllocationCount, 0);
        Interlocked.Exchange(ref _freeCount, 0);
        Interlocked.Exchange(ref _detachedGenerationCount, 0);
        Interlocked.Exchange(ref _outstandingNativeBytes, 0);
        Interlocked.Exchange(ref _detachedNativeBytes, 0);
        Interlocked.Exchange(ref _retiredNativeBytes, 0);
        Interlocked.Exchange(ref _bumpTraversalVisitCount, 0);
        Interlocked.Exchange(ref _forcedFailures, 0);
        Interlocked.Exchange(ref _forcedClearFailures, 0);
        Interlocked.Exchange(ref _forcedCommitBoundary, 0);
        Interlocked.Exchange(ref _commitBoundary, 0);
        Interlocked.Exchange(ref _forcedPostCommitBoundary, 0);
        Interlocked.Exchange(ref _postCommitBoundary, 0);
        Interlocked.Exchange(ref _forcedRetiredSnapshotPreparation, 0);
        Interlocked.Exchange(ref _forcedQuarantineReservation, 0);
        Volatile.Write(ref _operationEntered, null);
        Volatile.Write(ref _beforeOperationEntry, null);
        Volatile.Write(ref _beforeOperationEntryWithKernel, null);
        Volatile.Write(ref _operationEnteredWithAllocation, null);
        Volatile.Write(ref _operationEnteredWithGenerationOwner, null);
    }

    internal static long CurrentMetricsEpoch => Volatile.Read(ref _metricsEpoch);

    internal static void FailNextAllocation() => Interlocked.Increment(ref _forcedFailures);

    internal static bool ConsumeForcedFailure()
    {
        while (true)
        {
            int current = Volatile.Read(ref _forcedFailures);
            if (current == 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _forcedFailures, current - 1, current) == current)
            {
                return true;
            }
        }
    }

    internal static void FailNextClear() => Interlocked.Increment(ref _forcedClearFailures);

    internal static bool ConsumeForcedClearFailure()
    {
        while (true)
        {
            int current = Volatile.Read(ref _forcedClearFailures);
            if (current == 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _forcedClearFailures, current - 1, current) == current)
            {
                return true;
            }
        }
    }

    internal static void FailAtCommitBoundary(int boundary)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(boundary);
        Volatile.Write(ref _forcedCommitBoundary, boundary);
    }

    internal static void FailAfterCommitBoundary(int boundary)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(boundary);
        Volatile.Write(ref _forcedPostCommitBoundary, boundary);
    }

    internal static void FailNextRetiredSnapshotPreparation() =>
        Interlocked.Increment(ref _forcedRetiredSnapshotPreparation);

    internal static bool ConsumeRetiredSnapshotPreparationFailure() =>
        ConsumeCounter(ref _forcedRetiredSnapshotPreparation);

    internal static void FailNextQuarantineReservation() =>
        Interlocked.Increment(ref _forcedQuarantineReservation);

    internal static bool ConsumeQuarantineReservationFailure() =>
        ConsumeCounter(ref _forcedQuarantineReservation);

    private static bool ConsumeCounter(ref int counter)
    {
        while (true)
        {
            int current = Volatile.Read(ref counter);
            if (current == 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref counter, current - 1, current) == current)
            {
                return true;
            }
        }
    }

    internal static void BeginCommitSequence() => Volatile.Write(ref _commitBoundary, 0);

    internal static void BeginPostCommitSequence() => Volatile.Write(ref _postCommitBoundary, 0);

    internal static void CheckCommitBoundary(string operation, string boundary)
    {
        int ordinal = Interlocked.Increment(ref _commitBoundary);
        if (Volatile.Read(ref _forcedCommitBoundary) == ordinal
            && Interlocked.CompareExchange(ref _forcedCommitBoundary, 0, ordinal) == ordinal)
        {
            throw new InvalidOperationException($"Injected pre-publication failure during {operation} at {boundary} commit boundary {ordinal}.");
        }
    }

    internal static void CheckPostCommitBoundary(string operation, string boundary)
    {
        int ordinal = Interlocked.Increment(ref _postCommitBoundary);
        if (Volatile.Read(ref _forcedPostCommitBoundary) == ordinal
            && Interlocked.CompareExchange(ref _forcedPostCommitBoundary, 0, ordinal) == ordinal)
        {
            throw new InvalidOperationException($"Injected post-publication cleanup failure during {operation} at {boundary} commit boundary {ordinal}.");
        }
    }

    internal static long RecordAllocation(nuint byteLength, bool zeroed)
    {
        long metricsEpoch = CurrentMetricsEpoch;
        Interlocked.Increment(ref _allocationCount);
        Interlocked.Add(ref _outstandingNativeBytes, checked((long)byteLength));
        if (zeroed)
        {
            Interlocked.Increment(ref _zeroedAllocationCount);
        }

        return metricsEpoch;
    }

    internal static void RecordFree(nuint byteLength, bool detached, long metricsEpoch)
    {
        if (metricsEpoch != CurrentMetricsEpoch)
        {
            return;
        }

        Interlocked.Increment(ref _freeCount);
        long bytes = checked((long)byteLength);
        Interlocked.Add(ref _outstandingNativeBytes, -bytes);
        if (detached)
        {
            Interlocked.Add(ref _detachedNativeBytes, -bytes);
        }
    }

    internal static void RecordDetachedGeneration(long metricsEpoch)
    {
        if (metricsEpoch == CurrentMetricsEpoch)
        {
            Interlocked.Increment(ref _detachedGenerationCount);
        }
    }

    internal static void RecordDetachedBytes(nuint byteLength, long metricsEpoch)
    {
        if (metricsEpoch == CurrentMetricsEpoch)
        {
            Interlocked.Add(ref _detachedNativeBytes, checked((long)byteLength));
        }
    }

    internal static void RecordRetiredBytes(nuint byteLength, bool add, long metricsEpoch)
    {
        if (metricsEpoch != CurrentMetricsEpoch)
        {
            return;
        }

        long bytes = checked((long)byteLength);
        Interlocked.Add(ref _retiredNativeBytes, add ? bytes : -bytes);
    }

    internal static NativeMemoryTestMetrics Snapshot() => new(
        Volatile.Read(ref _allocationCount),
        Volatile.Read(ref _zeroedAllocationCount),
        Volatile.Read(ref _freeCount),
        Volatile.Read(ref _detachedGenerationCount),
        Volatile.Read(ref _outstandingNativeBytes),
        Volatile.Read(ref _detachedNativeBytes),
        Volatile.Read(ref _retiredNativeBytes),
        Volatile.Read(ref _bumpTraversalVisitCount));

    internal static void RecordBumpTraversalVisit() => Interlocked.Increment(ref _bumpTraversalVisitCount);

    internal static void SetOperationEntered(Action<string>? callback) => Volatile.Write(ref _operationEntered, callback);

    internal static void SetBeforeOperationEntry(Action<string>? callback) => Volatile.Write(ref _beforeOperationEntry, callback);

    internal static void SetBeforeOperationEntryWithKernel(Action<string, NativeOwnerKernel>? callback) =>
        Volatile.Write(ref _beforeOperationEntryWithKernel, callback);

    internal static void SetOperationEnteredWithAllocation(Action<string, NativeOwnerKernel, long, long>? callback) =>
        Volatile.Write(ref _operationEnteredWithAllocation, callback);

    internal static void SetOperationEnteredWithGenerationOwner(Action<string, NativeGenerationOwner>? callback) =>
        Volatile.Write(ref _operationEnteredWithGenerationOwner, callback);

    internal static void NotifyBeforeOperationEntry(string operation, NativeOwnerKernel kernel)
    {
        Volatile.Read(ref _beforeOperationEntry)?.Invoke(operation);
        Volatile.Read(ref _beforeOperationEntryWithKernel)?.Invoke(operation, kernel);
    }

    internal static void NotifyOperationEntered(
        string operation,
        NativeOwnerKernel kernel,
        NativeGenerationOwner generationOwner,
        long generation,
        long allocationId)
    {
        Volatile.Read(ref _operationEntered)?.Invoke(operation);
        Volatile.Read(ref _operationEnteredWithAllocation)?.Invoke(operation, kernel, generation, allocationId);
        Volatile.Read(ref _operationEnteredWithGenerationOwner)?.Invoke(operation, generationOwner);
    }
}

internal sealed class NativeSegment
{
    private IntPtr _pointer;
    private int _detached;
    private readonly long _metricsEpoch;

    private NativeSegment(IntPtr pointer, nuint byteLength, long metricsEpoch)
    {
        _pointer = pointer;
        ByteLength = byteLength;
        _metricsEpoch = metricsEpoch;
    }

    internal nuint ByteLength { get; }

    internal long MetricsEpoch => _metricsEpoch;

    internal IntPtr Pointer => Volatile.Read(ref _pointer);

    internal void MarkDetached() => Volatile.Write(ref _detached, 1);

    internal static NativeSegment AllocateZeroed(
        nuint byteLength,
        string ownerKind,
        long generation,
        string operation,
        NativeOwnerLifecycle currentLifecycle)
    {
        if (byteLength == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteLength), "A native segment must contain at least one byte.");
        }

        if (NativeMemoryTestHooks.ConsumeForcedFailure())
        {
            throw new NativeAllocationFailedException(byteLength, ownerKind, generation, operation, currentLifecycle);
        }

        try
        {
            unsafe
            {
                IntPtr pointer = (IntPtr)NativeMemory.AllocZeroed(byteLength);
                if (pointer == IntPtr.Zero)
                {
                    throw new NativeAllocationFailedException(byteLength, ownerKind, generation, operation, currentLifecycle);
                }

                return new NativeSegment(pointer, byteLength, NativeMemoryTestHooks.RecordAllocation(byteLength, zeroed: true));
            }
        }
        catch (OutOfMemoryException exception)
        {
            throw new NativeAllocationFailedException(byteLength, ownerKind, generation, operation, currentLifecycle, exception);
        }
    }

    internal void FreeNow()
    {
        IntPtr pointer = Interlocked.Exchange(ref _pointer, IntPtr.Zero);
        if (pointer == IntPtr.Zero)
        {
            return;
        }

        unsafe
        {
            NativeMemory.Free((void*)pointer);
        }

        NativeMemoryTestHooks.RecordFree(ByteLength, Volatile.Read(ref _detached) != 0, _metricsEpoch);
    }

    ~NativeSegment()
    {
        try
        {
            FreeNow();
        }
        catch
        {
        }
    }
}

internal sealed class NativeSlab
{
    internal NativeSlab(NativeSegment segment, int capacity, bool containsReferences, long allocationOrdinal)
    {
        Segment = segment;
        Capacity = capacity;
        ContainsReferences = containsReferences;
        AllocationOrdinal = allocationOrdinal;
    }

    internal NativeSegment Segment { get; }

    internal int Capacity { get; }

    internal bool ContainsReferences { get; }

    internal long AllocationOrdinal { get; }
}

/// <summary>
/// Owns the managed roots for native pointer-sized handle slots. The native segment
/// is the slot storage; this table is only the GC root side of that representation.
/// </summary>
internal sealed class NativeReferenceRootTable
{
    private readonly object _gate = new();
    private readonly Dictionary<long, object?> _roots = [];
    private readonly Stack<long> _availableIds = [];
    private long _nextId;

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _roots.Count;
            }
        }
    }

    internal void ReserveForClear(int slotCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slotCount);
        lock (_gate)
        {
            _availableIds.EnsureCapacity(checked(_availableIds.Count + slotCount));
        }
    }

    internal void Set<T>(NativeSegment segment, nuint offsetBytes, T value)
    {
        lock (_gate)
        {
            nuint previousRawId = ReadSlot(segment, offsetBytes);
            long previousId = previousRawId == 0 ? 0 : checked((long)previousRawId);
            _availableIds.EnsureCapacity(checked(_availableIds.Count + (previousId == 0 ? 0 : 1)));
            _roots.EnsureCapacity(checked(_roots.Count + 1));

            bool reusedId = _availableIds.Count != 0;
            long id = reusedId
                ? _availableIds.Peek()
                : checked(_nextId + 1);
            _roots.Add(id, value);
            try
            {
                WriteSlot(segment, offsetBytes, checked((nuint)id));
            }
            catch
            {
                _roots.Remove(id);
                throw;
            }

            if (reusedId)
            {
                _availableIds.Pop();
            }
            else
            {
                _nextId = id;
            }

            if (previousId != 0 && _roots.Remove(previousId))
            {
                _availableIds.Push(previousId);
            }
        }
    }

    internal T Get<T>(NativeSegment segment, nuint offsetBytes)
    {
        lock (_gate)
        {
            nuint rawId = ReadSlot(segment, offsetBytes);
            if (rawId == 0)
            {
                return default!;
            }

            long id = checked((long)rawId);
            if (!_roots.TryGetValue(id, out object? value))
            {
                throw new InvalidOperationException("The native reference slot has no owner-held root.");
            }

            return value is null ? default! : (T)value;
        }
    }

    internal void Clear(NativeSegment segment, nuint offsetBytes)
    {
        lock (_gate)
        {
            _availableIds.EnsureCapacity(checked(_availableIds.Count + 1));
            ClearLocked(segment, offsetBytes);
        }
    }

    internal void ClearRange(NativeSegment segment, nuint offsetBytes, int slotCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slotCount);
        ValidateSlotRange(offsetBytes, slotCount);
        lock (_gate)
        {
            _availableIds.EnsureCapacity(checked(_availableIds.Count + slotCount));
            for (int index = 0; index < slotCount; index++)
            {
                ClearLocked(segment, ComputeSlotOffset(offsetBytes, index, slotCount));
            }
        }
    }

    internal void ClearRangeReserved(NativeSegment segment, nuint offsetBytes, int slotCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slotCount);
        ValidateSlotRange(offsetBytes, slotCount);
        lock (_gate)
        {
            for (int index = 0; index < slotCount; index++)
            {
                ClearLocked(segment, ComputeSlotOffset(offsetBytes, index, slotCount));
            }
        }
    }

    internal static nuint ComputeSlotOffsetForTest(nuint offsetBytes, int index, int slotCount) =>
        ComputeSlotOffset(offsetBytes, index, slotCount);

    private static void ValidateSlotRange(nuint offsetBytes, int slotCount)
    {
        if (slotCount == 0)
        {
            return;
        }

        _ = ComputeSlotOffset(offsetBytes, checked(slotCount - 1), slotCount);
    }

    internal static nuint ComputeSlotOffset(nuint offsetBytes, int index, int slotCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slotCount);
        if (index >= slotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        nuint stride = (nuint)IntPtr.Size;
        nuint totalBytes = checked((nuint)slotCount * stride);
        _ = checked(offsetBytes + totalBytes - stride);
        return checked(offsetBytes + checked((nuint)index * stride));
    }

    private void ClearLocked(NativeSegment segment, nuint offsetBytes)
    {
        nuint rawId = ReadSlot(segment, offsetBytes);
        if (rawId == 0)
        {
            return;
        }

        WriteSlot(segment, offsetBytes, 0);
        long id = checked((long)rawId);
        if (_roots.Remove(id))
        {
            _availableIds.Push(id);
        }
    }

    private static nuint ReadSlot(NativeSegment segment, nuint offsetBytes)
    {
        unsafe
        {
            byte* pointer = (byte*)segment.Pointer + checked((nint)offsetBytes);
            return Unsafe.Read<nuint>(pointer);
        }
    }

    private static void WriteSlot(NativeSegment segment, nuint offsetBytes, nuint value)
    {
        unsafe
        {
            byte* pointer = (byte*)segment.Pointer + checked((nint)offsetBytes);
            Unsafe.Write(pointer, value);
        }
    }
}

internal sealed class NativeBumpSegment
{
    internal NativeBumpSegment(NativeSegment segment, long allocationOrdinal)
    {
        Segment = segment;
        HighCursor = segment.ByteLength;
        AllocationOrdinal = allocationOrdinal;
    }

    internal NativeSegment Segment { get; }

    internal nuint LowCursor { get; set; }

    internal nuint HighCursor { get; set; }

    internal long AllocationOrdinal { get; }

    internal bool IsCompletelyIdle => LowCursor == 0 && HighCursor == Segment.ByteLength;
}

internal sealed class NativeAllocation
{
    internal NativeAllocation(
        long id,
        NativeSlab? slab,
        NativeBumpSegment? bumpSegment,
        nuint offsetBytes,
        int length,
        int capacity,
        nuint storageBytes,
        NativeReferenceRootTable? referenceRoots,
        bool scoped,
        long scopeEpoch)
    {
        Id = id;
        Slab = slab;
        BumpSegment = bumpSegment;
        OffsetBytes = offsetBytes;
        Length = length;
        Capacity = capacity;
        StorageBytes = storageBytes;
        ReferenceRoots = referenceRoots;
        IsScoped = scoped;
        ScopeEpoch = scopeEpoch;
        Lifecycle = NativeAllocationLifecycle.Active;
    }

    internal long Id { get; }

    internal NativeSlab? Slab { get; }

    internal NativeBumpSegment? BumpSegment { get; }

    internal NativeSegment? Segment => Slab?.Segment ?? BumpSegment?.Segment;

    internal nuint OffsetBytes { get; }

    internal int Length { get; }

    internal int Capacity { get; }

    internal nuint StorageBytes { get; }

    internal NativeReferenceRootTable? ReferenceRoots { get; }

    internal bool IsScoped { get; }

    internal long ScopeEpoch { get; }

    internal NativeAllocationLifecycle Lifecycle { get; set; }

    internal int ActiveOperations { get; set; }

    internal T GetValue<T>(int index)
    {
        if (ReferenceRoots is not null)
        {
            return ReferenceRoots.Get<T>(
                Segment ?? throw new InvalidOperationException("A reference allocation has no native segment."),
                NativeReferenceRootTable.ComputeSlotOffset(
                    OffsetBytes,
                    index,
                    checked((int)(StorageBytes / (nuint)IntPtr.Size))));
        }

        if (Length == 0 || Segment is null)
        {
            return default!;
        }

        unsafe
        {
            byte* pointer = (byte*)Segment.Pointer + checked((nint)OffsetBytes);
            return Unsafe.Read<T>(pointer + checked(index * Unsafe.SizeOf<T>()));
        }
    }

    internal void SetValue<T>(int index, T value)
    {
        if (ReferenceRoots is not null)
        {
            ReferenceRoots.Set(
                Segment ?? throw new InvalidOperationException("A reference allocation has no native segment."),
                NativeReferenceRootTable.ComputeSlotOffset(
                    OffsetBytes,
                    index,
                    checked((int)(StorageBytes / (nuint)IntPtr.Size))),
                value);
            return;
        }

        if (Length == 0 || Segment is null)
        {
            return;
        }

        unsafe
        {
            byte* pointer = (byte*)Segment.Pointer + checked((nint)OffsetBytes);
            Unsafe.Write(pointer + checked(index * Unsafe.SizeOf<T>()), value);
        }
    }

    internal void ClearValues(bool reservedRootCapacity = false)
    {
        if (ReferenceRoots is not null && Segment is not null && StorageBytes != 0)
        {
            int slotCount = checked((int)(StorageBytes / (nuint)IntPtr.Size));
            if (reservedRootCapacity)
            {
                ReferenceRoots.ClearRangeReserved(Segment, OffsetBytes, slotCount);
            }
            else
            {
                ReferenceRoots.ClearRange(Segment, OffsetBytes, slotCount);
            }
        }

        if (StorageBytes == 0 || Segment is null)
        {
            return;
        }

        unsafe
        {
            NativeMemory.Clear((byte*)Segment.Pointer + checked((nint)OffsetBytes), StorageBytes);
        }
    }

    internal void Fill<T>(T value)
    {
        for (int index = 0; index < Length; index++)
        {
            SetValue(index, value);
        }
    }

    internal void CopyFrom<T>(ReadOnlySpan<T> source)
    {
        for (int index = 0; index < Length; index++)
        {
            SetValue(index, source[index]);
        }
    }

    internal void CopyTo<T>(Span<T> destination)
    {
        for (int index = 0; index < Length; index++)
        {
            destination[index] = GetValue<T>(index);
        }
    }
}

internal sealed class NativeGenerationOwner
{
    private readonly object _gate = new();
    private List<NativeSegment>? _segments = [];
    private int _released;
    private int _detached;
    private readonly long _metricsEpoch;

    internal NativeGenerationOwner(long generation)
    {
        Generation = generation;
        _metricsEpoch = NativeMemoryTestHooks.CurrentMetricsEpoch;
    }

    internal long Generation { get; }

    internal long MetricsEpoch => _metricsEpoch;

    internal void AddSegment(NativeSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        lock (_gate)
        {
            if (Volatile.Read(ref _released) != 0 || _segments is null)
            {
                segment.FreeNow();
                throw new InvalidOperationException("The native generation owner has already been released.");
            }

            _segments.Add(segment);
        }
    }

    internal void PrepareAddSegmentCapacity(int additionalSegments)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(additionalSegments);
        lock (_gate)
        {
            if (Volatile.Read(ref _released) != 0 || _segments is null)
            {
                throw new InvalidOperationException("The native generation owner has already been released.");
            }

            _segments.EnsureCapacity(checked(_segments.Count + additionalSegments));
        }
    }

    internal int SegmentListCapacityForTest()
    {
        lock (_gate)
        {
            return _segments?.Capacity ?? 0;
        }
    }

    internal void RemoveSegment(NativeSegment segment)
    {
        lock (_gate)
        {
            _segments?.Remove(segment);
        }
    }

    internal void Detach()
    {
        if (Volatile.Read(ref _released) != 0 || Interlocked.Exchange(ref _detached, 1) != 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_segments is null)
            {
                return;
            }

            foreach (NativeSegment segment in _segments)
            {
                segment.MarkDetached();
                NativeMemoryTestHooks.RecordDetachedBytes(segment.ByteLength, segment.MetricsEpoch);
            }
        }

        NativeMemoryTestHooks.RecordDetachedGeneration(_metricsEpoch);
    }

    internal void ReleaseToNative()
    {
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            List<NativeSegment>? segments = _segments;
            _segments = null;
            if (segments is not null)
            {
                foreach (NativeSegment segment in segments)
                {
                    segment.FreeNow();
                }
            }
        }

        GC.SuppressFinalize(this);
    }

    ~NativeGenerationOwner()
    {
        try
        {
            ReleaseToNative();
        }
        catch
        {
        }
    }
}

internal sealed class NativeGeneration
{
    internal NativeGeneration(long number)
    {
        Number = number;
        Owner = new NativeGenerationOwner(number);
    }

    internal long Number { get; }

    internal NativeGenerationOwner Owner { get; }

    internal Dictionary<long, NativeAllocation> Allocations { get; } = new();

    internal NativeReferenceRootTable ReferenceRoots { get; } = new();

    internal List<NativeSlab> Slabs { get; } = [];

    internal List<NativeSlab> AvailableSlabs { get; } = [];

    internal List<NativeBumpSegment> BumpSegments { get; } = [];

    internal int OrdinaryBumpTraversalIndex { get; set; }

    internal int ScopedBumpTraversalIndex { get; set; } = -1;

    internal int ActiveOperations { get; set; }

    internal int LeaseReturnsInProgress { get; set; }

    internal long RetiredNativeBytes { get; set; }

    internal long ScopeEpoch { get; set; }

    internal List<NativeAllocation> ScopedPending { get; } = [];

    internal Dictionary<NativeBumpSegment, nuint>? ScopedBaseline { get; set; }

    internal bool MemoryDetached { get; set; }

    // These arrays are prepared before the generation can become retired. Retired
    // cleanup must never allocate a snapshot while it is draining or quarantining.
    internal NativeSlab[] RetiredSlabs { get; set; } = [];

    internal NativeBumpSegment[] RetiredBumps { get; set; } = [];

    internal bool IsQuarantined { get; set; }

    internal int RetiredSegmentCount => checked(RetiredSlabs.Length + RetiredBumps.Length);

    internal void AddSlabOrdered(NativeSlab slab)
    {
        int index = 0;
        while (index < Slabs.Count && Slabs[index].AllocationOrdinal < slab.AllocationOrdinal)
        {
            index++;
        }

        Slabs.Insert(index, slab);
    }

    internal void AddAvailableSlabOrdered(NativeSlab slab)
    {
        int index = 0;
        while (index < AvailableSlabs.Count && AvailableSlabs[index].AllocationOrdinal < slab.AllocationOrdinal)
        {
            index++;
        }

        AvailableSlabs.Insert(index, slab);
    }

    internal int AddBumpOrdered(NativeBumpSegment segment)
    {
        int index = 0;
        while (index < BumpSegments.Count && BumpSegments[index].AllocationOrdinal < segment.AllocationOrdinal)
        {
            index++;
        }

        if (OrdinaryBumpTraversalIndex >= index)
        {
            OrdinaryBumpTraversalIndex++;
        }

        if (ScopedBumpTraversalIndex >= index)
        {
            ScopedBumpTraversalIndex++;
        }

        BumpSegments.Insert(index, segment);
        return index;
    }
}

internal ref struct NativeOperationToken
{
    private NativeOwnerKernel? _kernel;
    private readonly NativeGeneration _generationState;
    private readonly NativeGenerationOwner _generationOwner;
    private readonly NativeAllocation _allocation;
    private readonly string _operation;

    internal NativeOperationToken(
        NativeOwnerKernel kernel,
        NativeGeneration generation,
        NativeAllocation allocation,
        string operation)
    {
        _kernel = kernel;
        _generationState = generation;
        _generationOwner = generation.Owner;
        _allocation = allocation;
        _operation = operation;
    }

    internal NativeLeaseView<T> GetView<T>() => new(_allocation);

    internal T GetValue<T>(int index) => _allocation.GetValue<T>(index);

    internal void SetValue<T>(int index, T value) => _allocation.SetValue(index, value);

    public void Dispose()
    {
        NativeOwnerKernel? kernel = _kernel;
        if (kernel is null)
        {
            return;
        }

        _kernel = null;
        kernel.ExitOperation(_generationState, _allocation, _operation);
        GC.KeepAlive(_generationOwner);
    }
}

internal sealed class NativeOwnerKernel
{
    private const nuint DefaultBumpSegmentBytes = 4096;

    private readonly object _gate = new();
    private readonly NativeOwnerKind _kind;
    private readonly string _ownerKind;
    private readonly NativeMemoryReturn _returnMemoryOnDispose;
    private readonly Type? _elementType;
    private readonly int _storageElementSize;
    private readonly nuint _initialReservation;
    private readonly bool _containsReferences;
    private long _generation;
    private long _nextAllocationId;
    private long _nextSegmentOrdinal;
    private NativeGeneration? _current;
    private NativeOwnerLifecycle _lifecycle;
    private readonly List<NativeGeneration> _retiredGenerations = [];
    private readonly List<NativeGeneration> _quarantinedGenerations = [];

    internal NativeOwnerLifecycle Lifecycle
    {
        get
        {
            lock (_gate)
            {
                return _lifecycle;
            }
        }
    }

    internal int CurrentAllocationRecordCountForTest()
    {
        lock (_gate)
        {
            return _current?.Allocations.Count ?? 0;
        }
    }

    internal int CurrentReferenceRootCountForTest()
    {
        lock (_gate)
        {
            return _current?.ReferenceRoots.Count ?? 0;
        }
    }

    internal (int Ordinary, int Scoped, int SegmentCount) CurrentBumpTraversalForTest()
    {
        lock (_gate)
        {
            NativeGeneration? generation = _current;
            return generation is null
                ? (0, -1, 0)
                : (generation.OrdinaryBumpTraversalIndex, generation.ScopedBumpTraversalIndex, generation.BumpSegments.Count);
        }
    }

    internal int QuarantinedSegmentCountForTest()
    {
        lock (_gate)
        {
            int count = 0;
            foreach (NativeGeneration generation in _quarantinedGenerations)
            {
                count = checked(count + generation.RetiredSegmentCount);
            }

            return count;
        }
    }

    internal int QuarantinedGenerationCountForTest()
    {
        lock (_gate)
        {
            return _quarantinedGenerations.Count;
        }
    }

    internal int RetiredGenerationCountForTest()
    {
        lock (_gate)
        {
            return _retiredGenerations.Count;
        }
    }

    internal int QuarantineCapacityForTest()
    {
        lock (_gate)
        {
            return _quarantinedGenerations.Capacity;
        }
    }

    internal (int Slabs, int AvailableSlabs, int Bumps, int OwnerSegments) CurrentBankCapacitiesForTest()
    {
        lock (_gate)
        {
            NativeGeneration? generation = _current;
            return generation is null
                ? (0, 0, 0, 0)
                : (
                    generation.Slabs.Capacity,
                    generation.AvailableSlabs.Capacity,
                    generation.BumpSegments.Capacity,
                    generation.Owner.SegmentListCapacityForTest());
        }
    }

    internal long CurrentScopeEpochForTest()
    {
        lock (_gate)
        {
            return _current?.ScopeEpoch ?? 0;
        }
    }

    internal long GenerationCounterForTest()
    {
        lock (_gate)
        {
            return _generation;
        }
    }

    internal void SetScopeEpochForTest(long value)
    {
        lock (_gate)
        {
            NativeGeneration generation = EnsureActiveLocked("SetScopeEpochForTest");
            generation.ScopeEpoch = value;
        }
    }

    internal void SetGenerationCounterForTest(long value)
    {
        lock (_gate)
        {
            _generation = value;
        }
    }

    internal long[] CurrentSegmentOrdinalsForTest()
    {
        lock (_gate)
        {
            if (_current is null)
            {
                return [];
            }

            if (_kind == NativeOwnerKind.Pool)
            {
                return _current.Slabs.Select(slab => slab.AllocationOrdinal).ToArray();
            }

            return _current.BumpSegments.Select(segment => segment.AllocationOrdinal).ToArray();
        }
    }

    private NativeOwnerKernel(
        NativeOwnerKind kind,
        string ownerKind,
        NativeMemoryReturn returnMemoryOnDispose,
        Type? elementType,
        int storageElementSize,
        nuint initialReservation,
        bool containsReferences,
        bool doNotLeaseOnDeclaration)
    {
        _kind = kind;
        _ownerKind = ownerKind;
        _returnMemoryOnDispose = returnMemoryOnDispose;
        _elementType = elementType;
        _storageElementSize = storageElementSize;
        _initialReservation = initialReservation;
        _containsReferences = containsReferences;
        _lifecycle = doNotLeaseOnDeclaration ? NativeOwnerLifecycle.Unleased : NativeOwnerLifecycle.Active;

        if (doNotLeaseOnDeclaration)
        {
            return;
        }

        NativeGeneration generation = new(0);
        _current = generation;
        try
        {
            ReserveInitialStorageLocked(generation, "initial reservation", _lifecycle);
        }
        catch
        {
            generation.Owner.ReleaseToNative();
            _current = null;
            throw;
        }
    }

    internal static NativeOwnerKernel CreatePool(
        int initialCapacity,
        Type elementType,
        int storageElementSize,
        string ownerKind,
        NativeMemoryReturn returnMemoryOnDispose,
        bool containsReferences,
        bool doNotLeaseOnDeclaration)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(storageElementSize);
        NativeMemoryReturnValidation.Validate(returnMemoryOnDispose, nameof(returnMemoryOnDispose));
        return new NativeOwnerKernel(
            NativeOwnerKind.Pool,
            ownerKind,
            returnMemoryOnDispose,
            elementType,
            storageElementSize,
            (nuint)initialCapacity,
            containsReferences,
            doNotLeaseOnDeclaration);
    }

    internal static NativeOwnerKernel CreateRegion(
        nuint preAllocateBytes,
        string ownerKind,
        NativeMemoryReturn returnMemoryOnDispose,
        bool containsReferences,
        bool doNotLeaseOnDeclaration)
    {
        NativeMemoryReturnValidation.Validate(returnMemoryOnDispose, nameof(returnMemoryOnDispose));
        return new NativeOwnerKernel(
            NativeOwnerKind.Region,
            ownerKind,
            returnMemoryOnDispose,
            elementType: null,
            storageElementSize: 0,
            preAllocateBytes,
            containsReferences,
            doNotLeaseOnDeclaration);
    }

    internal static NativeOwnerKernel CreateArena(
        nuint preAllocateBytes,
        string ownerKind,
        NativeMemoryReturn returnMemoryOnDispose,
        bool doNotLeaseOnDeclaration)
    {
        NativeMemoryReturnValidation.Validate(returnMemoryOnDispose, nameof(returnMemoryOnDispose));
        return new NativeOwnerKernel(
            NativeOwnerKind.Arena,
            ownerKind,
            returnMemoryOnDispose,
            elementType: null,
            storageElementSize: 0,
            preAllocateBytes,
            containsReferences: false,
            doNotLeaseOnDeclaration);
    }

    internal NativePoolLease Rent(int length, bool scoped)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        lock (_gate)
        {
            NativeGeneration generation = EnsureActiveLocked(scoped ? "LeaseScoped" : "Rent");
            if (_kind != NativeOwnerKind.Pool)
            {
                throw CreateStateException("Rent", "This owner does not expose typed pool leases.", 0);
            }

            NativeSlab? slab = null;
            if (length > 0)
            {
                slab = TakeSmallestAvailableSlabLocked(generation, length);
                if (slab is null)
                {
                    generation.Slabs.EnsureCapacity(checked(generation.Slabs.Count + 1));
                    slab = AddPoolSlabLocked(generation, length, "pool growth", _lifecycle);
                    generation.AddSlabOrdered(slab);
                }
            }

            long allocationId = NextAllocationIdLocked();
            long epoch = scoped ? BeginScopedLocked(generation) : generation.ScopeEpoch;
            NativeAllocation allocation = new(
                allocationId,
                slab,
                bumpSegment: null,
                offsetBytes: 0,
                length,
                slab?.Capacity ?? 0,
                storageBytes: slab is null ? 0 : checked((nuint)slab.Capacity * (nuint)_storageElementSize),
                referenceRoots: _containsReferences ? generation.ReferenceRoots : null,
                scoped,
                epoch);
            generation.Allocations.Add(allocationId, allocation);
            if (scoped)
            {
                generation.ScopedPending.Add(allocation);
            }

            return new NativePoolLease(generation.Number, allocationId, length, allocation.Capacity);
        }
    }

    internal NativeRegionAllocation LeaseBump(int length, int elementSize, nuint alignment, bool scoped, Type elementType)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(elementSize);
        ArgumentOutOfRangeException.ThrowIfZero(alignment);
        nuint byteLength = CalculateByteLength(length, elementSize, scoped ? "scoped allocation" : "allocation");

        lock (_gate)
        {
            NativeGeneration generation = EnsureActiveLocked(scoped ? "LeaseScoped" : "Lease");
            if (_kind is not (NativeOwnerKind.Region or NativeOwnerKind.Arena))
            {
                throw CreateStateException("Lease", "This owner does not expose heterogeneous allocations.", 0);
            }

            bool startedScope = scoped && generation.ScopedBaseline is null;
            long epoch = scoped ? BeginScopedLocked(generation) : generation.ScopeEpoch;
            NativeBumpSegment? bumpSegment = null;
            NativeBumpSegment? createdSegment = null;
            nuint originalCursor = 0;
            bool cursorCaptured = false;
            nuint offset = 0;
            try
            {
                if (byteLength > 0)
                {
                    bumpSegment = FindBumpSpaceLocked(generation, byteLength, alignment, scoped);
                    if (bumpSegment is null)
                    {
                        nuint segmentBytes = ChooseBumpSegmentBytes(
                            generation,
                            checked(byteLength + alignment - 1));
                        generation.BumpSegments.EnsureCapacity(checked(generation.BumpSegments.Count + 1));
                        generation.Owner.PrepareAddSegmentCapacity(1);
                        NativeSegment segment = NativeSegment.AllocateZeroed(
                            segmentBytes,
                            _ownerKind,
                            generation.Number,
                            scoped ? "scoped growth" : "allocation growth",
                            _lifecycle);
                        createdSegment = new NativeBumpSegment(segment, NextSegmentOrdinalLocked());
                        bumpSegment = createdSegment;
                        AppendBumpSegmentLocked(generation, createdSegment);
                        generation.Owner.AddSegment(segment);
                    }

                    if (scoped)
                    {
                        originalCursor = bumpSegment.HighCursor;
                        cursorCaptured = true;
                        nuint unaligned = checked(bumpSegment.HighCursor - byteLength);
                        offset = AlignDown(unaligned, alignment);
                        bumpSegment.HighCursor = offset;
                    }
                    else
                    {
                        originalCursor = bumpSegment.LowCursor;
                        cursorCaptured = true;
                        offset = AlignUp(bumpSegment.LowCursor, alignment);
                        bumpSegment.LowCursor = checked(offset + byteLength);
                    }
                }

                long allocationId = NextAllocationIdLocked();
                NativeAllocation allocation = new(
                    allocationId,
                    slab: null,
                    bumpSegment,
                    offset,
                    length,
                    length,
                    byteLength,
                    referenceRoots: ContainsReferences(elementType) && length > 0
                        ? generation.ReferenceRoots
                        : null,
                    scoped,
                    epoch);
                generation.Allocations.Add(allocationId, allocation);
                if (scoped)
                {
                    generation.ScopedPending.Add(allocation);
                }

                return new NativeRegionAllocation(generation.Number, allocationId, length, length);
            }
            catch
            {
                if (createdSegment is not null)
                {
                    generation.BumpSegments.Remove(createdSegment);
                    generation.Owner.RemoveSegment(createdSegment.Segment);
                    createdSegment.Segment.FreeNow();
                    ResetBumpTraversal(generation);
                }
                else if (bumpSegment is not null && cursorCaptured)
                {
                    if (scoped)
                    {
                        bumpSegment.HighCursor = originalCursor;
                    }
                    else
                    {
                        bumpSegment.LowCursor = originalCursor;
                    }
                }

                if (startedScope && generation.ScopedPending.Count == 0)
                {
                    generation.ScopedBaseline = null;
                }

                throw;
            }
        }
    }

    internal NativeHandleMetadata ValidateHandle(long generationNumber, long allocationId, string operation)
    {
        lock (_gate)
        {
            NativeAllocation allocation = GetActiveAllocationLocked(generationNumber, allocationId, operation);
            return new NativeHandleMetadata(allocation.Length, allocation.Capacity);
        }
    }

    internal NativeOperationToken EnterOperation(long generationNumber, long allocationId, string operation)
    {
        NativeMemoryTestHooks.NotifyBeforeOperationEntry(operation, this);
        NativeOperationToken token;
        NativeGenerationOwner generationOwner;
        lock (_gate)
        {
            NativeGeneration generation = EnsureActiveLocked(operation, generationNumber, allocationId);
            NativeAllocation allocation = GetActiveAllocationLocked(generationNumber, allocationId, operation, generation);
            generation.ActiveOperations++;
            allocation.ActiveOperations++;
            generationOwner = generation.Owner;
            token = new NativeOperationToken(this, generation, allocation, operation);
        }

        NativeMemoryTestHooks.NotifyOperationEntered(operation, this, generationOwner, generationNumber, allocationId);
        return token;
    }

    internal void ExitOperation(NativeGeneration generation, NativeAllocation allocation, string operation)
    {
        lock (_gate)
        {
            if (allocation.ActiveOperations > 0)
            {
                allocation.ActiveOperations--;
            }

            if (generation.ActiveOperations > 0)
            {
                generation.ActiveOperations--;
            }

            if (generation.MemoryDetached)
            {
                if (generation.ActiveOperations == 0)
                {
                    DrainDetachedGenerationLocked(generation);
                }

                return;
            }

            if (generation != _current
                && generation.ActiveOperations == 0
                && _retiredGenerations.Contains(generation))
            {
                DrainRetiredGenerationLocked(generation);
            }
        }
    }

    internal void ReturnLease(long generationNumber, long allocationId)
    {
        lock (_gate)
        {
            if (_lifecycle is NativeOwnerLifecycle.Disposed or NativeOwnerLifecycle.Returned or NativeOwnerLifecycle.Unleased)
            {
                return;
            }

            if (_lifecycle is NativeOwnerLifecycle.Returning or NativeOwnerLifecycle.RollingOver)
            {
                return;
            }

            NativeGeneration generation = _current!;
            if (generation.Number != generationNumber || !generation.Allocations.TryGetValue(allocationId, out NativeAllocation? allocation))
            {
                return;
            }

            if (allocation.Lifecycle != NativeAllocationLifecycle.Active)
            {
                return;
            }

            if (allocation.ActiveOperations != 0)
            {
                throw CreateInUseException("Pooled.Dispose", generation.Number, allocationId, allocation.ActiveOperations, "The lease has an active native operation. No storage was cleared or requeued.");
            }

            if (allocation.Slab is not null && allocation.Length > 0)
            {
                generation.AvailableSlabs.EnsureCapacity(checked(generation.AvailableSlabs.Count + 1));
            }

            allocation.ReferenceRoots?.ReserveForClear(ClearSlotCount(allocation));
            allocation.Lifecycle = NativeAllocationLifecycle.Returning;
            generation.LeaseReturnsInProgress++;
            try
            {
                EnsureNoInjectedClearFailureLocked("Pooled.Dispose", afterStateChange: true);
                ClearAllocationStorage(allocation);
                if (allocation.Slab is not null && allocation.Length > 0)
                {
                    generation.AddAvailableSlabOrdered(allocation.Slab);
                }

                allocation.Lifecycle = NativeAllocationLifecycle.Returned;
                generation.ScopedPending.Remove(allocation);
                generation.Allocations.Remove(allocation.Id);
            }
            catch
            {
                allocation.Lifecycle = NativeAllocationLifecycle.Active;
                throw;
            }
            finally
            {
                generation.LeaseReturnsInProgress--;
            }
        }
    }

    internal void ReturnMemoryToNativeMemory() => ReturnMemory(NativeMemoryReturn.ToNativeMemory, "ReturnMemoryToNativeMemory");

    internal void ReturnMemoryToGarbageCollector() => ReturnMemory(NativeMemoryReturn.ToGarbageCollector, "ReturnMemoryToGarbageCollector");

    internal void ReleaseLeasesToNativeMemory() => ReleaseLeases(NativeMemoryReturn.ToNativeMemory, "ReleaseLeasesToNativeMemory");

    internal void ReleaseLeasesToGarbageCollector() => ReleaseLeases(NativeMemoryReturn.ToGarbageCollector, "ReleaseLeasesToGarbageCollector");

    internal void LeaseFromMemory()
    {
        lock (_gate)
        {
            EnsureNotDisposedLocked("LeaseFromMemory", 0);
            if (_lifecycle == NativeOwnerLifecycle.Active)
            {
                throw CreateStateException("LeaseFromMemory", "The owner is already active; return its current generation first.", 0);
            }

            if (_lifecycle is NativeOwnerLifecycle.Returning or NativeOwnerLifecycle.RollingOver)
            {
                throw CreateInUseException("LeaseFromMemory", _generation, 0, 0, "The owner is in a lifecycle transition.");
            }

            if (_kind == NativeOwnerKind.Region && _lifecycle == NativeOwnerLifecycle.Returned)
            {
                throw CreateStateException("LeaseFromMemory", "NativeRegion is a single-generation lexical owner and cannot be re-leased.", 0);
            }

            if (_lifecycle is not (NativeOwnerLifecycle.Unleased or NativeOwnerLifecycle.Returned))
            {
                throw CreateStateException("LeaseFromMemory", "The owner has no leasable generation.", 0);
            }

            NativeOwnerLifecycle observed = _lifecycle;
            NativeGeneration candidate = new(_generation);
            try
            {
                ReserveInitialStorageLocked(candidate, "activation reservation", observed);
            }
            catch
            {
                candidate.Owner.ReleaseToNative();
                throw;
            }

            _current = candidate;
            _lifecycle = NativeOwnerLifecycle.Active;
        }
    }

    internal void RecycleScoped()
    {
        lock (_gate)
        {
            NativeGeneration generation = EnsureActiveLocked("RecycleScoped");
            if (generation.ScopedPending.Count == 0)
            {
                return;
            }

            if (generation.ActiveOperations != 0 || generation.LeaseReturnsInProgress != 0)
            {
                throw CreateInUseException("RecycleScoped", generation.Number, 0, generation.ActiveOperations, "A scoped allocation is still entered by a native operation.");
            }

            long nextScopeEpoch = checked(generation.ScopeEpoch + 1);
            EnsureNoInjectedClearFailureLocked("RecycleScoped");
            PrepareGenerationClearLocked(
                generation,
                skipActiveOperations: false,
                minimumAvailableSlabs: generation.ScopedPending.Count);
            PreflightCommitBoundaries(
                "RecycleScoped",
                generation.ScopedPending.Count,
                 slabTransferCount: 0,
                 bumpTransferCount: 0);
            foreach (NativeAllocation allocation in generation.ScopedPending)
            {
                if (allocation.Lifecycle != NativeAllocationLifecycle.Active)
                {
                    continue;
                }

                ClearAllocationStorage(allocation);
                allocation.Lifecycle = NativeAllocationLifecycle.Returned;
                generation.Allocations.Remove(allocation.Id);
                if (allocation.Slab is not null && allocation.Length > 0 && !generation.AvailableSlabs.Contains(allocation.Slab))
                {
                    generation.AddAvailableSlabOrdered(allocation.Slab);
                }
            }

            if (generation.ScopedBaseline is not null)
            {
                foreach (KeyValuePair<NativeBumpSegment, nuint> baseline in generation.ScopedBaseline)
                {
                    baseline.Key.HighCursor = baseline.Value;
                }
            }

            foreach (NativeBumpSegment segment in generation.BumpSegments)
            {
                if (generation.ScopedBaseline?.ContainsKey(segment) != true
                    && !HasScopedAllocationOnSegment(generation, segment))
                {
                    segment.HighCursor = segment.Segment.ByteLength;
                }
            }

            generation.ScopedPending.Clear();
            generation.ScopedBaseline = null;
            generation.ScopeEpoch = nextScopeEpoch;
            ResetBumpTraversal(generation);
        }
    }

    internal nuint TrimRetainedMemory()
    {
        lock (_gate)
        {
            if (!CanTrimLocked("TrimRetainedMemory"))
            {
                return 0;
            }

            return TrimRetainedMemoryLocked(null);
        }
    }

    internal nuint TrimRetainedMemoryByBytes(nuint bytesToRelease)
    {
        if (bytesToRelease == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytesToRelease), "A zero trim request is not valid; use the parameterless trim operation.");
        }

        lock (_gate)
        {
            if (!CanTrimLocked("TrimRetainedMemoryByBytes"))
            {
                return 0;
            }

            return TrimRetainedMemoryLocked(bytesToRelease);
        }
    }

    internal nuint TrimRetainedMemoryByLeaseSize(
        int leaseLength,
        int elementSize,
        nuint alignment,
        Type elementType)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(leaseLength);
        ArgumentOutOfRangeException.ThrowIfZero(alignment);
        lock (_gate)
        {
            if (!CanTrimLocked("TrimRetainedMemoryByLeaseSize"))
            {
                return 0;
            }

            int effectiveElementSize = ContainsReferences(elementType) ? IntPtr.Size : elementSize;
            nuint byteLength = CalculateByteLength(leaseLength, effectiveElementSize, "lease-sized trim");
            nuint requested = _kind == NativeOwnerKind.Pool
                ? byteLength
                : ChooseBumpSegmentBytes(_current!, checked(byteLength + alignment - 1));
            return TrimRetainedMemoryLocked(requested);
        }
    }

    internal void Dispose()
    {
        lock (_gate)
        {
            if (_lifecycle == NativeOwnerLifecycle.Disposed)
            {
                return;
            }

            if (_lifecycle is NativeOwnerLifecycle.Returning or NativeOwnerLifecycle.RollingOver)
            {
                throw CreateInUseException("Dispose", _generation, 0, 0, "Another lifecycle transition is already using the owner gate.");
            }

            EnsureOwnerWideIdleLocked("Dispose", "No current, retired, or quarantined generation was invalidated or freed.");

            if (_lifecycle == NativeOwnerLifecycle.Unleased)
            {
                if (_returnMemoryOnDispose == NativeMemoryReturn.ToNativeMemory)
                {
                    ReleaseOwnerWideStorageLocked();
                }
                else
                {
                    DetachOwnerWideStorageLocked();
                }

                _current = null;
                _retiredGenerations.Clear();
                _quarantinedGenerations.Clear();
                _lifecycle = NativeOwnerLifecycle.Disposed;
                GC.SuppressFinalize(this);
                return;
            }

            if (_lifecycle == NativeOwnerLifecycle.Returned)
            {
                EnsureNoInjectedClearFailureLocked("Dispose");
                PrepareOwnerWideClearLocked(skipActiveOperations: false);
                PreflightCommitBoundaries(
                    "Dispose",
                    CountOwnerWideClearableAllocationsLocked(skipActiveOperations: false),
                    slabTransferCount: 0,
                    bumpTransferCount: 0);
                ClearOwnerWideAllocationsLocked(skipActiveOperations: false);
                _lifecycle = NativeOwnerLifecycle.Returning;
                InvalidateOwnerWideGenerationsLocked();
                if (_returnMemoryOnDispose == NativeMemoryReturn.ToNativeMemory)
                {
                    ReleaseOwnerWideStorageLocked();
                }
                else
                {
                    DetachOwnerWideStorageLocked();
                }

                _current = null;
                _retiredGenerations.Clear();
                _quarantinedGenerations.Clear();
                _lifecycle = NativeOwnerLifecycle.Disposed;
                GC.SuppressFinalize(this);
                return;
            }

            long nextGenerationNumber = checked(_generation + 1);
            EnsureNoInjectedClearFailureLocked("Dispose");
            PrepareOwnerWideClearLocked(skipActiveOperations: false);
            PreflightCommitBoundaries(
                "Dispose",
                CountOwnerWideClearableAllocationsLocked(skipActiveOperations: false),
                slabTransferCount: 0,
                bumpTransferCount: 0);
            ClearOwnerWideAllocationsLocked(skipActiveOperations: false);
            _lifecycle = NativeOwnerLifecycle.Returning;
            InvalidateOwnerWideGenerationsLocked();
            if (_returnMemoryOnDispose == NativeMemoryReturn.ToNativeMemory)
            {
                ReleaseOwnerWideStorageLocked();
            }
            else
            {
                DetachOwnerWideStorageLocked();
            }

            _current = null;
            _generation = nextGenerationNumber;
            _retiredGenerations.Clear();
            _quarantinedGenerations.Clear();
            _lifecycle = NativeOwnerLifecycle.Disposed;
            GC.SuppressFinalize(this);
        }
    }

    private void ReserveInitialStorageLocked(NativeGeneration generation, string operation, NativeOwnerLifecycle observedLifecycle)
    {
        if (_kind == NativeOwnerKind.Pool && _initialReservation > 0)
        {
            int capacity = checked((int)_initialReservation);
            generation.Slabs.EnsureCapacity(checked(generation.Slabs.Count + 1));
            generation.AvailableSlabs.EnsureCapacity(checked(generation.AvailableSlabs.Count + 1));
            generation.Owner.PrepareAddSegmentCapacity(1);
            NativeSlab slab = AddPoolSlabLocked(generation, capacity, operation, observedLifecycle);
            generation.AddSlabOrdered(slab);
            generation.AddAvailableSlabOrdered(slab);
        }
        else if (_kind is NativeOwnerKind.Region or NativeOwnerKind.Arena && _initialReservation > 0)
        {
            generation.BumpSegments.EnsureCapacity(checked(generation.BumpSegments.Count + 1));
            generation.Owner.PrepareAddSegmentCapacity(1);
            NativeSegment segment = NativeSegment.AllocateZeroed(_initialReservation, _ownerKind, generation.Number, operation, observedLifecycle);
            NativeBumpSegment bump = new(segment, NextSegmentOrdinalLocked());
            AppendBumpSegmentLocked(generation, bump);
            generation.Owner.AddSegment(segment);
        }
    }

    private NativeSlab AddPoolSlabLocked(NativeGeneration generation, int capacity, string operation, NativeOwnerLifecycle observedLifecycle)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        nuint byteLength = CalculateByteLength(capacity, _storageElementSize, operation);
        NativeSegment segment = NativeSegment.AllocateZeroed(byteLength, _ownerKind, generation.Number, operation, observedLifecycle);
        try
        {
            generation.Owner.AddSegment(segment);
        }
        catch
        {
            segment.FreeNow();
            throw;
        }

        try
        {
            return new NativeSlab(segment, capacity, _containsReferences, NextSegmentOrdinalLocked());
        }
        catch
        {
            generation.Owner.RemoveSegment(segment);
            segment.FreeNow();
            throw;
        }
    }

    private NativeSlab? TakeSmallestAvailableSlabLocked(NativeGeneration generation, int length)
    {
        NativeSlab? best = null;
        int bestIndex = -1;
        for (int index = 0; index < generation.AvailableSlabs.Count; index++)
        {
            NativeSlab candidate = generation.AvailableSlabs[index];
            if (candidate.Capacity < length || (best is not null && candidate.Capacity >= best.Capacity))
            {
                continue;
            }

            best = candidate;
            bestIndex = index;
        }

        if (bestIndex >= 0)
        {
            generation.AvailableSlabs.RemoveAt(bestIndex);
        }

        return best;
    }

    private NativeBumpSegment? FindBumpSpaceLocked(NativeGeneration generation, nuint byteLength, nuint alignment, bool scoped)
    {
        if (generation.BumpSegments.Count == 0)
        {
            return null;
        }

        if (scoped)
        {
            int start = Math.Min(
                Math.Max(generation.ScopedBumpTraversalIndex, 0),
                generation.BumpSegments.Count - 1);
            for (int index = start; index >= 0; index--)
            {
                NativeMemoryTestHooks.RecordBumpTraversalVisit();
                NativeBumpSegment segment = generation.BumpSegments[index];
                if (segment.HighCursor < segment.LowCursor || byteLength > segment.HighCursor - segment.LowCursor)
                {
                    continue;
                }

                nuint candidate = AlignDown(segment.HighCursor - byteLength, alignment);
                if (candidate >= segment.LowCursor && byteLength <= segment.HighCursor - candidate)
                {
                    generation.ScopedBumpTraversalIndex = index;
                    return segment;
                }
            }
        }
        else
        {
            int start = Math.Min(
                Math.Max(generation.OrdinaryBumpTraversalIndex, 0),
                generation.BumpSegments.Count - 1);
            for (int index = start; index < generation.BumpSegments.Count; index++)
            {
                NativeMemoryTestHooks.RecordBumpTraversalVisit();
                NativeBumpSegment segment = generation.BumpSegments[index];
                nuint candidate = AlignUp(segment.LowCursor, alignment);
                if (candidate <= segment.HighCursor && byteLength <= segment.HighCursor - candidate)
                {
                    generation.OrdinaryBumpTraversalIndex = index;
                    return segment;
                }
            }
        }

        if (scoped)
        {
            generation.ScopedBumpTraversalIndex = -1;
        }
        else
        {
            generation.OrdinaryBumpTraversalIndex = generation.BumpSegments.Count;
        }

        return null;
    }

    private static void AppendBumpSegmentLocked(NativeGeneration generation, NativeBumpSegment segment)
    {
        int previousCount = generation.BumpSegments.Count;
        int newIndex = generation.AddBumpOrdered(segment);
        if (previousCount == 0 || generation.OrdinaryBumpTraversalIndex >= previousCount)
        {
            generation.OrdinaryBumpTraversalIndex = newIndex;
        }

        if (segment.AllocationOrdinal >= generation.BumpSegments[^1].AllocationOrdinal)
        {
            generation.ScopedBumpTraversalIndex = newIndex;
        }
    }

    private static void ResetBumpTraversal(NativeGeneration generation)
    {
        generation.OrdinaryBumpTraversalIndex = 0;
        generation.ScopedBumpTraversalIndex = generation.BumpSegments.Count - 1;
    }

    private long BeginScopedLocked(NativeGeneration generation)
    {
        if (generation.ScopedBaseline is null)
        {
            generation.ScopedBaseline = generation.BumpSegments.ToDictionary(segment => segment, segment => segment.HighCursor);
        }

        return generation.ScopeEpoch;
    }

    private void ReturnMemory(NativeMemoryReturn policy, string operation)
    {
        lock (_gate)
        {
            EnsureNotDisposedLocked(operation, 0);
            if (_lifecycle == NativeOwnerLifecycle.Returned)
            {
                throw CreateReturnedException(operation, _generation, _generation, 0, "The current generation has already been returned.");
            }

            if (_lifecycle == NativeOwnerLifecycle.Unleased)
            {
                throw CreateStateException(operation, "The owner has not been leased; activate it with LeaseFromMemory first.", 0);
            }

            if (_lifecycle is NativeOwnerLifecycle.Returning or NativeOwnerLifecycle.RollingOver)
            {
                throw CreateInUseException(operation, _generation, 0, 0, "Another lifecycle transition is already using the owner gate.");
            }

            bool tolerant = policy == NativeMemoryReturn.ToGarbageCollector;
            NativeGeneration generation = _current!;
            if (tolerant)
            {
                EnsureOwnerWideLeaseReturnsIdleLocked(operation, "No individual lease return was left in progress.");
            }
            else
            {
                EnsureOwnerWideIdleLocked(operation, "No lease was invalidated, detached, or freed.");
            }
            long nextGenerationNumber = checked(_generation + 1);
            EnsureNoInjectedClearFailureLocked(operation);
            NativeSlab[] retiredSlabs = SnapshotRetiredSlabsLocked(generation, operation);
            NativeBumpSegment[] retiredBumps = SnapshotRetiredBumpsLocked(generation, operation);
            PrepareRetiredStorageLocked(
                generation,
                retiredSlabs,
                retiredBumps,
                operation,
                reserveQuarantineSlot: tolerant && generation.ActiveOperations != 0);
            if (tolerant && generation.ActiveOperations != 0)
            {
                _retiredGenerations.EnsureCapacity(checked(_retiredGenerations.Count + 1));
            }

            if (!tolerant)
            {
                PrepareOwnerWideClearLocked(skipActiveOperations: false);
                PreflightCommitBoundaries(
                    operation,
                    CountOwnerWideClearableAllocationsLocked(skipActiveOperations: false),
                    slabTransferCount: 0,
                    bumpTransferCount: 0);
                ClearOwnerWideAllocationsLocked(skipActiveOperations: false);
                _lifecycle = NativeOwnerLifecycle.Returning;
                InvalidateOwnerWideGenerationsLocked();
                ReleaseOwnerWideStorageLocked();
                _current = null;
                _generation = nextGenerationNumber;
                _retiredGenerations.Clear();
                _quarantinedGenerations.Clear();
                _lifecycle = NativeOwnerLifecycle.Returned;
                return;
            }

            PreflightCommitBoundaries(
                operation,
                CountClearableAllocations(generation, skipActiveOperations: true),
                slabTransferCount: 0,
                bumpTransferCount: 0);
            ClearGenerationAllocationsLocked(generation, skipActiveOperations: true);
            _lifecycle = NativeOwnerLifecycle.Returning;
            InvalidateOwnerWideGenerationsLocked();
            DetachOwnerWideStorageLocked();
            _current = null;
            _generation = nextGenerationNumber;
            _lifecycle = NativeOwnerLifecycle.Returned;
        }
    }

    private void ReleaseLeases(NativeMemoryReturn policy, string operation)
    {
        lock (_gate)
        {
            EnsureNotDisposedLocked(operation, 0);
            if (_kind == NativeOwnerKind.Region)
            {
                throw CreateStateException(operation, "NativeRegion has one lexical generation and does not release leases.", 0);
            }

            NativeGeneration current = EnsureActiveLocked(operation);
            if (current.LeaseReturnsInProgress != 0 || (policy == NativeMemoryReturn.ToNativeMemory && current.ActiveOperations != 0))
            {
                throw CreateInUseException(operation, current.Number, 0, current.ActiveOperations, "No lease generation was invalidated or reset.");
            }

            EnsureNoInjectedClearFailureLocked(operation);
            bool tolerant = policy == NativeMemoryReturn.ToGarbageCollector;
            long nextGenerationNumber = checked(current.Number + 1);
            NativeSlab[] slabs = SnapshotRetiredSlabsLocked(current, operation);
            NativeBumpSegment[] bumps = SnapshotRetiredBumpsLocked(current, operation);
            NativeGeneration next = new(nextGenerationNumber);
            try
            {
                int slabTransferCount = 0;
                foreach (NativeSlab slab in slabs)
                {
                    if (!IsSegmentBusy(current, slab))
                    {
                        slabTransferCount++;
                    }
                }

                int bumpTransferCount = 0;
                foreach (NativeBumpSegment bump in bumps)
                {
                    if (!IsSegmentBusy(current, bump))
                    {
                        bumpTransferCount++;
                    }
                }

                NativeSlab[] retiredSlabs = CreateRetiredSlabSnapshot(slabs, slabTransferCount, current);
                NativeBumpSegment[] retiredBumps = CreateRetiredBumpSnapshot(bumps, bumpTransferCount, current);
                PrepareRetiredStorageLocked(
                    current,
                    retiredSlabs,
                    retiredBumps,
                    operation,
                    reserveQuarantineSlot: tolerant && current.ActiveOperations != 0);
                (int rejoinableSlabs, int rejoinableBumps) = CountPotentialRejoinSegmentsLocked(
                    retiredSlabs,
                    retiredBumps);
                next.Slabs.EnsureCapacity(checked(slabTransferCount + rejoinableSlabs));
                next.AvailableSlabs.EnsureCapacity(checked(slabTransferCount + rejoinableSlabs));
                next.BumpSegments.EnsureCapacity(checked(bumpTransferCount + rejoinableBumps));
                next.Owner.PrepareAddSegmentCapacity(
                    checked(slabTransferCount + bumpTransferCount + rejoinableSlabs + rejoinableBumps));
                if (current.ActiveOperations != 0)
                {
                    _retiredGenerations.EnsureCapacity(checked(_retiredGenerations.Count + 1));
                }

                PreflightCommitBoundaries(
                    operation,
                    CountClearableAllocations(current, skipActiveOperations: tolerant),
                    slabTransferCount,
                    bumpTransferCount);
            }
            catch
            {
                next.Owner.ReleaseToNative();
                throw;
            }

            _lifecycle = NativeOwnerLifecycle.RollingOver;
            try
            {
                ClearGenerationAllocationsLocked(current, skipActiveOperations: tolerant);

                foreach (NativeSlab slab in slabs)
                {
                    if (!IsSegmentBusy(current, slab))
                    {
                        TransferSegmentLocked(current, next, slab.Segment);
                        current.Slabs.Remove(slab);
                        current.AvailableSlabs.Remove(slab);
                        next.AddSlabOrdered(slab);
                        next.AddAvailableSlabOrdered(slab);
                    }
                }

                foreach (NativeBumpSegment bump in bumps)
                {
                    if (!IsSegmentBusy(current, bump))
                    {
                        bump.LowCursor = 0;
                        bump.HighCursor = bump.Segment.ByteLength;
                        TransferSegmentLocked(current, next, bump.Segment);
                        current.BumpSegments.Remove(bump);
                        next.AddBumpOrdered(bump);
                    }
                }

                ResetBumpTraversal(next);

                InvalidateGenerationLocked(current);
                _current = next;
                _generation = next.Number;
                _lifecycle = NativeOwnerLifecycle.Active;
                if (current.ActiveOperations != 0)
                {
                    current.RetiredNativeBytes = checked((long)GetBusySegmentBytes(current));
                    if (current.RetiredNativeBytes != 0)
                    {
                        NativeMemoryTestHooks.RecordRetiredBytes((nuint)current.RetiredNativeBytes, add: true, metricsEpoch: current.Owner.MetricsEpoch);
                    }

                    _retiredGenerations.Add(current);
                }
                else
                {
                    current.Owner.ReleaseToNative();
                }
            }
            catch
            {
                _lifecycle = NativeOwnerLifecycle.Active;
                if (!ReferenceEquals(_current, next))
                {
                    next.Owner.ReleaseToNative();
                }
                throw;
            }
        }
    }

    private void DrainRetiredGenerationLocked(NativeGeneration generation)
    {
        if (generation.MemoryDetached)
        {
            DrainDetachedGenerationLocked(generation);
            return;
        }

        NativeSlab[] slabs = generation.RetiredSlabs;
        NativeBumpSegment[] bumps = generation.RetiredBumps;
        NativeGeneration? current = _current;
        string operation = "DrainRetiredGeneration";
        long failedAllocationId = 0;
        long failedSegmentOrdinal = 0;
        string failedBoundary = "retired generation drain";

        try
        {
            bool canRejoin = current is not null && _lifecycle != NativeOwnerLifecycle.Disposed;
            PreflightCommitBoundaries(
                operation,
                CountClearableAllocations(generation, skipActiveOperations: false),
                canRejoin ? slabs.Length : 0,
                canRejoin ? bumps.Length : 0);
            NativeMemoryTestHooks.BeginPostCommitSequence();

            foreach (NativeAllocation allocation in generation.Allocations.Values)
            {
                failedAllocationId = allocation.Id;
                failedSegmentOrdinal = GetSegmentOrdinal(allocation);
                failedBoundary = "clear";
                ClearAllocationStorageReserved(allocation);
                NativeMemoryTestHooks.CheckPostCommitBoundary(operation, "clear");
            }

            if (!canRejoin)
            {
                _retiredGenerations.Remove(generation);
                if (generation.RetiredNativeBytes != 0)
                {
                    NativeMemoryTestHooks.RecordRetiredBytes((nuint)generation.RetiredNativeBytes, add: false, metricsEpoch: generation.Owner.MetricsEpoch);
                    generation.RetiredNativeBytes = 0;
                }

                generation.Owner.ReleaseToNative();
                return;
            }

            foreach (NativeSlab slab in slabs)
            {
                failedSegmentOrdinal = slab.AllocationOrdinal;
                failedBoundary = "slab transfer";
                TransferSegmentLocked(generation, current!, slab.Segment);
                generation.Slabs.Remove(slab);
                generation.AvailableSlabs.Remove(slab);
                current!.AddSlabOrdered(slab);
                current.AddAvailableSlabOrdered(slab);
                NativeMemoryTestHooks.CheckPostCommitBoundary(operation, "slab transfer");
            }

            foreach (NativeBumpSegment bump in bumps)
            {
                failedSegmentOrdinal = bump.AllocationOrdinal;
                failedBoundary = "bump transfer";
                bump.LowCursor = 0;
                bump.HighCursor = bump.Segment.ByteLength;
                TransferSegmentLocked(generation, current!, bump.Segment);
                generation.BumpSegments.Remove(bump);
                current!.AddBumpOrdered(bump);
                NativeMemoryTestHooks.CheckPostCommitBoundary(operation, "bump transfer");
            }

            generation.AvailableSlabs.Clear();
            generation.BumpSegments.Clear();
            ResetBumpTraversal(current!);
            _retiredGenerations.Remove(generation);
            if (generation.RetiredNativeBytes != 0)
            {
                NativeMemoryTestHooks.RecordRetiredBytes((nuint)generation.RetiredNativeBytes, add: false, metricsEpoch: generation.Owner.MetricsEpoch);
                generation.RetiredNativeBytes = 0;
            }

            generation.Owner.ReleaseToNative();
        }
        catch (Exception exception)
        {
            _retiredGenerations.Remove(generation);
            QuarantineGenerationStorageLocked(generation, current, slabs, bumps);
            if (generation.RetiredNativeBytes != 0)
            {
                NativeMemoryTestHooks.RecordRetiredBytes((nuint)generation.RetiredNativeBytes, add: false, metricsEpoch: generation.Owner.MetricsEpoch);
                generation.RetiredNativeBytes = 0;
            }

            throw CreateQuarantinedException(
                operation,
                generation.Number,
                failedAllocationId,
                failedSegmentOrdinal,
                failedBoundary,
                exception);
        }
    }

    private void DrainDetachedGenerationLocked(NativeGeneration generation)
    {
        const string operation = "DrainDetachedGeneration";
        long failedAllocationId = 0;
        long failedSegmentOrdinal = 0;
        string failedBoundary = "detached clear";

        try
        {
            PreflightCommitBoundaries(
                operation,
                CountClearableAllocations(generation, skipActiveOperations: false),
                slabTransferCount: 0,
                bumpTransferCount: 0);
            NativeMemoryTestHooks.BeginPostCommitSequence();

            foreach (NativeAllocation allocation in generation.Allocations.Values)
            {
                failedAllocationId = allocation.Id;
                failedSegmentOrdinal = GetSegmentOrdinal(allocation);
                failedBoundary = "clear";
                ClearAllocationStorageReserved(allocation);
                allocation.Lifecycle = NativeAllocationLifecycle.Returned;
                NativeMemoryTestHooks.CheckPostCommitBoundary(operation, "clear");
            }

            generation.Allocations.Clear();
            generation.ScopedPending.Clear();
            generation.ScopedBaseline = null;
            if (generation.RetiredNativeBytes != 0)
            {
                NativeMemoryTestHooks.RecordRetiredBytes(
                    (nuint)generation.RetiredNativeBytes,
                    add: false,
                    metricsEpoch: generation.Owner.MetricsEpoch);
                generation.RetiredNativeBytes = 0;
            }
        }
        catch (Exception exception)
        {
            throw CreateDetachedCleanupException(
                operation,
                generation,
                failedAllocationId,
                failedSegmentOrdinal,
                failedBoundary,
                exception);
        }
    }

    private void TransferSegmentLocked(NativeGeneration source, NativeGeneration destination, NativeSegment segment)
    {
        destination.Owner.AddSegment(segment);
        source.Owner.RemoveSegment(segment);
    }

    private void QuarantineGenerationStorageLocked(
        NativeGeneration generation,
        NativeGeneration? current,
        NativeSlab[] slabs,
        NativeBumpSegment[] bumps)
    {
        foreach (NativeSlab slab in slabs)
        {
            generation.Slabs.Remove(slab);
            generation.AvailableSlabs.Remove(slab);
            generation.Owner.RemoveSegment(slab.Segment);
            current?.Slabs.Remove(slab);
            current?.AvailableSlabs.Remove(slab);
            current?.Owner.RemoveSegment(slab.Segment);
            generation.Owner.AddSegment(slab.Segment);
        }

        foreach (NativeBumpSegment bump in bumps)
        {
            generation.BumpSegments.Remove(bump);
            generation.Owner.RemoveSegment(bump.Segment);
            current?.BumpSegments.Remove(bump);
            current?.Owner.RemoveSegment(bump.Segment);
            generation.Owner.AddSegment(bump.Segment);
        }

        generation.AvailableSlabs.Clear();
        generation.BumpSegments.Clear();
        generation.IsQuarantined = true;
        _quarantinedGenerations.Add(generation);
        if (current is not null)
        {
            ResetBumpTraversal(current);
        }
    }

    private NativeAllocationQuarantinedException CreateQuarantinedException(
        string operation,
        long generation,
        long allocationId,
        long segmentOrdinal,
        string boundary,
        Exception innerException) =>
        new(
            $"{_ownerKind}.{operation} quarantined ended generation {generation} storage at segment {segmentOrdinal} after a cleanup failure at {boundary}. The storage remains physically owned but is removed from all reusable banks and will not be reused; dispose the owner to release it. (lifecycle {_lifecycle})",
            _ownerKind,
            generation,
            _generation,
            operation,
            ActiveOperationCountLocked(),
            allocationId,
            segmentOrdinal,
            boundary,
            _lifecycle,
            innerException);

    private NativeAllocationQuarantinedException CreateDetachedCleanupException(
        string operation,
        NativeGeneration generation,
        long allocationId,
        long segmentOrdinal,
        string boundary,
        Exception innerException) =>
        new(
            $"{_ownerKind}.{operation} could not clear detached generation {generation.Number} at {boundary}. The storage remains detached and finalizable; it is no longer owned by the allocator and will not be synchronously released or reused (lifecycle {_lifecycle}).",
            _ownerKind,
            generation.Number,
            _generation,
            operation,
            activeOperationCount: 0,
            allocationId,
            segmentOrdinal,
            boundary,
            _lifecycle,
            innerException);

    private static long GetSegmentOrdinal(NativeAllocation allocation) =>
        allocation.Slab?.AllocationOrdinal ?? allocation.BumpSegment?.AllocationOrdinal ?? 0;

    private void InvalidateGenerationLocked(NativeGeneration generation)
    {
        foreach (NativeAllocation allocation in generation.Allocations.Values)
        {
            allocation.Lifecycle = NativeAllocationLifecycle.Returned;
        }

        generation.AvailableSlabs.Clear();
        generation.ScopedPending.Clear();
        generation.ScopedBaseline = null;
    }

    private NativeGeneration EnsureActiveLocked(string operation, long generationNumber = 0, long allocationId = 0)
    {
        EnsureNotDisposedLocked(operation, allocationId);
        if (_lifecycle == NativeOwnerLifecycle.Returned)
        {
            throw CreateReturnedException(operation, generationNumber, _generation, allocationId, "The owner generation is returned.");
        }

        if (_lifecycle is NativeOwnerLifecycle.Returning or NativeOwnerLifecycle.RollingOver)
        {
            throw CreateInUseException(operation, generationNumber, allocationId, 0, "The owner is in a lifecycle transition and rejects new operations.");
        }

        if (_lifecycle == NativeOwnerLifecycle.Unleased)
        {
            throw CreateStateException(operation, "The owner has not been leased; activate it with LeaseFromMemory first.", allocationId);
        }

        return _current ?? throw new InvalidOperationException("The active native owner has no generation.");
    }

    private bool CanTrimLocked(string operation)
    {
        EnsureNotDisposedLocked(operation, 0);
        if (_lifecycle is NativeOwnerLifecycle.Unleased or NativeOwnerLifecycle.Returned)
        {
            return false;
        }

        if (_lifecycle is NativeOwnerLifecycle.Returning or NativeOwnerLifecycle.RollingOver)
        {
            throw CreateInUseException(operation, _generation, 0, ActiveOperationCountLocked(), "The owner is in a lifecycle transition and cannot be trimmed.");
        }

        return true;
    }

    private void EnsureNotDisposedLocked(string operation, long allocationId)
    {
        if (_lifecycle == NativeOwnerLifecycle.Disposed)
        {
            throw new NativeAllocationDisposedException(
                $"{_ownerKind}.{operation} cannot run because the owner is permanently disposed (lifecycle {NativeOwnerLifecycle.Disposed}).",
                _ownerKind,
                _generation,
                _generation,
                operation,
                ActiveOperationCountLocked(),
                allocationId,
                NativeOwnerLifecycle.Disposed);
        }
    }

    private NativeAllocation GetActiveAllocationLocked(long generationNumber, long allocationId, string operation, NativeGeneration? knownGeneration = null)
    {
        NativeGeneration generation = knownGeneration ?? EnsureActiveLocked(operation, generationNumber, allocationId);
        if (generation.Number != generationNumber || !generation.Allocations.TryGetValue(allocationId, out NativeAllocation? allocation))
        {
            throw CreateReturnedException(operation, generationNumber, _generation, allocationId, "The handle belongs to an earlier or unpublished generation.");
        }

        if (allocation.Lifecycle != NativeAllocationLifecycle.Active)
        {
            throw CreateReturnedException(operation, generationNumber, _generation, allocationId, "The derived allocation has already ended.");
        }

        return allocation;
    }

    private void ClearAllocationStorage(NativeAllocation allocation)
    {
        allocation.ClearValues();
    }

    private void ClearAllocationStorageReserved(NativeAllocation allocation)
    {
        allocation.ClearValues(reservedRootCapacity: true);
    }

    private static NativeSlab[] SnapshotRetiredSlabsLocked(NativeGeneration generation, string operation)
    {
        if (NativeMemoryTestHooks.ConsumeRetiredSnapshotPreparationFailure())
        {
            throw new InvalidOperationException($"Injected retired-generation snapshot preparation failure during {operation}.");
        }

        return generation.Slabs.ToArray();
    }

    private static NativeBumpSegment[] SnapshotRetiredBumpsLocked(NativeGeneration generation, string operation)
    {
        if (NativeMemoryTestHooks.ConsumeRetiredSnapshotPreparationFailure())
        {
            throw new InvalidOperationException($"Injected retired-generation snapshot preparation failure during {operation}.");
        }

        return generation.BumpSegments.ToArray();
    }

    private static NativeSlab[] CreateRetiredSlabSnapshot(
        NativeSlab[] slabs,
        int transferCount,
        NativeGeneration generation)
    {
        int retiredCount = checked(slabs.Length - transferCount);
        if (retiredCount == 0)
        {
            return [];
        }

        NativeSlab[] retired = new NativeSlab[retiredCount];
        int index = 0;
        foreach (NativeSlab slab in slabs)
        {
            if (IsSegmentBusy(generation, slab))
            {
                retired[index++] = slab;
            }
        }

        return retired;
    }

    private static NativeBumpSegment[] CreateRetiredBumpSnapshot(
        NativeBumpSegment[] bumps,
        int transferCount,
        NativeGeneration generation)
    {
        int retiredCount = checked(bumps.Length - transferCount);
        if (retiredCount == 0)
        {
            return [];
        }

        NativeBumpSegment[] retired = new NativeBumpSegment[retiredCount];
        int index = 0;
        foreach (NativeBumpSegment bump in bumps)
        {
            if (IsSegmentBusy(generation, bump))
            {
                retired[index++] = bump;
            }
        }

        return retired;
    }

    private (int Slabs, int Bumps) CountPotentialRejoinSegmentsLocked(
        NativeSlab[] newlyRetiredSlabs,
        NativeBumpSegment[] newlyRetiredBumps)
    {
        int slabs = newlyRetiredSlabs.Length;
        int bumps = newlyRetiredBumps.Length;
        foreach (NativeGeneration retired in _retiredGenerations)
        {
            if (retired.MemoryDetached)
            {
                continue;
            }

            slabs = checked(slabs + retired.RetiredSlabs.Length);
            bumps = checked(bumps + retired.RetiredBumps.Length);
        }

        return (slabs, bumps);
    }

    private void PrepareRetiredStorageLocked(
        NativeGeneration generation,
        NativeSlab[] retiredSlabs,
        NativeBumpSegment[] retiredBumps,
        string operation,
        bool reserveQuarantineSlot)
    {
        generation.RetiredSlabs = retiredSlabs;
        generation.RetiredBumps = retiredBumps;
        PrepareGenerationClearLocked(generation, skipActiveOperations: false);
        generation.Owner.PrepareAddSegmentCapacity(checked(retiredSlabs.Length + retiredBumps.Length));

        int outstandingQuarantineReservations = checked(
            _retiredGenerations.Count + (reserveQuarantineSlot ? 1 : 0));
        if (outstandingQuarantineReservations != 0)
        {
            if (reserveQuarantineSlot && NativeMemoryTestHooks.ConsumeQuarantineReservationFailure())
            {
                throw new InvalidOperationException($"Injected quarantine-slot reservation failure during {operation}.");
            }

            _quarantinedGenerations.EnsureCapacity(
                checked(_quarantinedGenerations.Count + outstandingQuarantineReservations));
        }
    }

    private static int ClearSlotCount(NativeAllocation allocation)
    {
        return allocation.ReferenceRoots is null || allocation.StorageBytes == 0
            ? 0
            : checked((int)(allocation.StorageBytes / (nuint)IntPtr.Size));
    }

    private static bool WillClear(NativeAllocation allocation, bool skipActiveOperations) =>
        !skipActiveOperations || allocation.ActiveOperations == 0;

    private void PrepareGenerationClearLocked(
        NativeGeneration generation,
        bool skipActiveOperations,
        int minimumAvailableSlabs = 0)
    {
        int slots = 0;
        int availableSlabs = minimumAvailableSlabs;
        foreach (NativeAllocation allocation in generation.Allocations.Values)
        {
            if (!WillClear(allocation, skipActiveOperations))
            {
                continue;
            }

            slots = checked(slots + ClearSlotCount(allocation));
            if (allocation.Slab is not null && allocation.Length > 0)
            {
                availableSlabs++;
            }
        }

        generation.ReferenceRoots.ReserveForClear(slots);
        if (availableSlabs != 0)
        {
            generation.AvailableSlabs.EnsureCapacity(checked(generation.AvailableSlabs.Count + availableSlabs));
        }
    }

    private static int CountClearableAllocations(NativeGeneration generation, bool skipActiveOperations)
    {
        int count = 0;
        foreach (NativeAllocation allocation in generation.Allocations.Values)
        {
            if (WillClear(allocation, skipActiveOperations))
            {
                count++;
            }
        }

        return count;
    }

    private static void PreflightCommitBoundaries(
        string operation,
        int clearCount,
        int slabTransferCount,
        int bumpTransferCount)
    {
        NativeMemoryTestHooks.BeginCommitSequence();
        for (int index = 0; index < clearCount; index++)
        {
            NativeMemoryTestHooks.CheckCommitBoundary(operation, "clear");
        }

        for (int index = 0; index < slabTransferCount; index++)
        {
            NativeMemoryTestHooks.CheckCommitBoundary(operation, "slab transfer");
        }

        for (int index = 0; index < bumpTransferCount; index++)
        {
            NativeMemoryTestHooks.CheckCommitBoundary(operation, "bump transfer");
        }
    }

    private void ClearGenerationAllocationsLocked(NativeGeneration generation, bool skipActiveOperations)
    {
        foreach (NativeAllocation allocation in generation.Allocations.Values)
        {
            if (skipActiveOperations && allocation.ActiveOperations != 0)
            {
                continue;
            }

            ClearAllocationStorage(allocation);
        }
    }

    private int CountOwnerWideClearableAllocationsLocked(bool skipActiveOperations)
    {
        int count = 0;
        if (_current is not null && !_current.MemoryDetached)
        {
            count = checked(count + CountClearableAllocations(_current, skipActiveOperations));
        }

        foreach (NativeGeneration generation in _retiredGenerations)
        {
            if (generation.MemoryDetached)
            {
                continue;
            }

            count = checked(count + CountClearableAllocations(generation, skipActiveOperations));
        }

        foreach (NativeGeneration generation in _quarantinedGenerations)
        {
            if (generation.MemoryDetached)
            {
                continue;
            }

            count = checked(count + CountClearableAllocations(generation, skipActiveOperations));
        }

        return count;
    }

    private void PrepareOwnerWideClearLocked(bool skipActiveOperations)
    {
        if (_current is not null && !_current.MemoryDetached)
        {
            PrepareGenerationClearLocked(_current, skipActiveOperations);
        }

        foreach (NativeGeneration generation in _retiredGenerations)
        {
            if (generation.MemoryDetached)
            {
                continue;
            }

            PrepareGenerationClearLocked(generation, skipActiveOperations);
        }

        foreach (NativeGeneration generation in _quarantinedGenerations)
        {
            if (generation.MemoryDetached)
            {
                continue;
            }

            PrepareGenerationClearLocked(generation, skipActiveOperations);
        }
    }

    private void ClearOwnerWideAllocationsLocked(bool skipActiveOperations)
    {
        if (_current is not null && !_current.MemoryDetached)
        {
            ClearGenerationAllocationsLocked(_current, skipActiveOperations);
        }

        foreach (NativeGeneration generation in _retiredGenerations)
        {
            if (generation.MemoryDetached)
            {
                continue;
            }

            ClearGenerationAllocationsLocked(generation, skipActiveOperations);
        }

        foreach (NativeGeneration generation in _quarantinedGenerations)
        {
            if (generation.MemoryDetached)
            {
                continue;
            }

            ClearGenerationAllocationsLocked(generation, skipActiveOperations);
        }
    }

    private void InvalidateOwnerWideGenerationsLocked()
    {
        if (_current is not null && !_current.MemoryDetached)
        {
            InvalidateGenerationLocked(_current);
        }

        foreach (NativeGeneration generation in _retiredGenerations)
        {
            if (generation.MemoryDetached)
            {
                continue;
            }

            InvalidateGenerationLocked(generation);
        }

        foreach (NativeGeneration generation in _quarantinedGenerations)
        {
            if (generation.MemoryDetached)
            {
                continue;
            }

            InvalidateGenerationLocked(generation);
        }
    }

    private void ReleaseOwnerWideStorageLocked()
    {
        if (_current is not null && !_current.MemoryDetached)
        {
            ReleaseGenerationStorageLocked(_current);
        }

        foreach (NativeGeneration generation in _retiredGenerations)
        {
            if (generation.MemoryDetached)
            {
                continue;
            }

            ReleaseGenerationStorageLocked(generation);
        }

        foreach (NativeGeneration generation in _quarantinedGenerations)
        {
            if (generation.MemoryDetached)
            {
                continue;
            }

            ReleaseGenerationStorageLocked(generation);
        }
    }

    private static void ReleaseGenerationStorageLocked(NativeGeneration generation)
    {
        if (generation.RetiredNativeBytes != 0)
        {
            NativeMemoryTestHooks.RecordRetiredBytes(
                (nuint)generation.RetiredNativeBytes,
                add: false,
                metricsEpoch: generation.Owner.MetricsEpoch);
            generation.RetiredNativeBytes = 0;
        }

        generation.Owner.ReleaseToNative();
    }

    private void DetachOwnerWideStorageLocked()
    {
        if (_current is not null)
        {
            _current.MemoryDetached = true;
            _current.Owner.Detach();
        }

        foreach (NativeGeneration generation in _retiredGenerations)
        {
            generation.MemoryDetached = true;
            generation.Owner.Detach();
            if (generation.ActiveOperations == 0 && generation.RetiredNativeBytes != 0)
            {
                NativeMemoryTestHooks.RecordRetiredBytes(
                    (nuint)generation.RetiredNativeBytes,
                    add: false,
                    metricsEpoch: generation.Owner.MetricsEpoch);
                generation.RetiredNativeBytes = 0;
            }
        }

        foreach (NativeGeneration generation in _quarantinedGenerations)
        {
            generation.MemoryDetached = true;
            generation.Owner.Detach();
            if (generation.ActiveOperations == 0 && generation.RetiredNativeBytes != 0)
            {
                NativeMemoryTestHooks.RecordRetiredBytes(
                    (nuint)generation.RetiredNativeBytes,
                    add: false,
                    metricsEpoch: generation.Owner.MetricsEpoch);
                generation.RetiredNativeBytes = 0;
            }
        }

        _retiredGenerations.Clear();
        _quarantinedGenerations.Clear();
    }

    private void EnsureNoInjectedClearFailureLocked(string operation, bool afterStateChange = false)
    {
        if (NativeMemoryTestHooks.ConsumeForcedClearFailure())
        {
            string timing = afterStateChange
                ? "after entering the reversible transition; the owner state will be rolled back"
                : "before the owner state changed";
            throw new InvalidOperationException($"Injected native clear failure during {operation} {timing}.");
        }
    }

    private nuint TrimRetainedMemoryLocked(nuint? requestedBytes)
    {
        nuint released = 0;
        if (_kind == NativeOwnerKind.Pool)
        {
            NativeGeneration current = _current!;
            for (int index = current.Slabs.Count - 1;
                index >= 0 && (requestedBytes is null || released < requestedBytes.Value);
                index--)
            {
                NativeSlab slab = current.Slabs[index];
                if (!current.AvailableSlabs.Remove(slab))
                {
                    continue;
                }

                foreach (NativeAllocation allocation in current.Allocations.Values
                    .Where(allocation => allocation.Lifecycle == NativeAllocationLifecycle.Returned
                        && ReferenceEquals(allocation.Slab, slab))
                    .ToArray())
                {
                    current.Allocations.Remove(allocation.Id);
                }

                current.Slabs.Remove(slab);
                current.Owner.RemoveSegment(slab.Segment);
                slab.Segment.FreeNow();
                released = checked(released + slab.Segment.ByteLength);
            }

            return released;
        }

        NativeGeneration generation = _current!;
        for (int index = generation.BumpSegments.Count - 1; index >= 0; index--)
        {
            NativeBumpSegment segment = generation.BumpSegments[index];
            if (requestedBytes is not null && released >= requestedBytes.Value)
            {
                break;
            }

            if (!segment.IsCompletelyIdle)
            {
                break;
            }

            generation.BumpSegments.RemoveAt(index);
            generation.Owner.RemoveSegment(segment.Segment);
            segment.Segment.FreeNow();
            released = checked(released + segment.Segment.ByteLength);
            ResetBumpTraversal(generation);
        }

        return released;
    }

    private static nuint GetBusySegmentBytes(NativeGeneration generation)
    {
        nuint total = 0;
        foreach (NativeSlab slab in generation.Slabs)
        {
            if (IsSegmentBusy(generation, slab))
            {
                total = checked(total + slab.Segment.ByteLength);
            }
        }

        foreach (NativeBumpSegment bump in generation.BumpSegments)
        {
            if (IsSegmentBusy(generation, bump))
            {
                total = checked(total + bump.Segment.ByteLength);
            }
        }

        return total;
    }

    private static bool IsSegmentBusy(NativeGeneration generation, NativeSlab slab)
    {
        foreach (NativeAllocation allocation in generation.Allocations.Values)
        {
            if (allocation.ActiveOperations != 0 && ReferenceEquals(allocation.Slab, slab))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSegmentBusy(NativeGeneration generation, NativeBumpSegment bump)
    {
        foreach (NativeAllocation allocation in generation.Allocations.Values)
        {
            if (allocation.ActiveOperations != 0 && ReferenceEquals(allocation.BumpSegment, bump))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasScopedAllocationOnSegment(NativeGeneration generation, NativeBumpSegment segment)
    {
        foreach (NativeAllocation allocation in generation.Allocations.Values)
        {
            if (allocation.IsScoped && ReferenceEquals(allocation.BumpSegment, segment))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsReferences(Type type) => ContainsReferences(type, new HashSet<Type>());

    private static bool ContainsReferences(Type type, HashSet<Type> visited)
    {
        if (!type.IsValueType)
        {
            return true;
        }

        if (type.IsPrimitive || type.IsEnum || !visited.Add(type))
        {
            return false;
        }

        foreach (System.Reflection.FieldInfo field in type.GetFields(
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic))
        {
            if (ContainsReferences(field.FieldType, visited))
            {
                return true;
            }
        }

        return false;
    }

    private static nuint CalculateByteLength(int length, int elementSize, string operation)
    {
        try
        {
            return checked((nuint)length * (nuint)elementSize);
        }
        catch (OverflowException exception)
        {
            throw new OverflowException($"The {operation} byte count overflows native addressable storage.", exception);
        }
    }

    private static nuint ChooseBumpSegmentBytes(NativeGeneration generation, nuint requiredBytes)
    {
        nuint previous = generation.BumpSegments.Count == 0 ? 0 : generation.BumpSegments[^1].Segment.ByteLength;
        nuint growth = DefaultBumpSegmentBytes;
        if (previous != 0)
        {
            try
            {
                growth = checked(previous * 2);
            }
            catch (OverflowException)
            {
                growth = requiredBytes;
            }
        }

        return Math.Max(requiredBytes, Math.Max(DefaultBumpSegmentBytes, growth));
    }

    private static nuint AlignUp(nuint value, nuint alignment)
    {
        nuint remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }

    private static nuint AlignDown(nuint value, nuint alignment) => value - value % alignment;

    private long NextAllocationIdLocked() => checked(++_nextAllocationId);

    private long NextSegmentOrdinalLocked() => checked(++_nextSegmentOrdinal);

    private int ActiveOperationCountLocked() => GetOwnerWideActivityLocked().ActiveOperations;

    private OwnerWideActivity GetOwnerWideActivityLocked()
    {
        int activeOperations = 0;
        int leaseReturnsInProgress = 0;
        int busyGenerationCount = 0;
        long firstBusyGeneration = 0;

        AccumulateOwnerWideActivity(
            _current is { MemoryDetached: false } ? _current : null,
            ref activeOperations,
            ref leaseReturnsInProgress,
            ref busyGenerationCount,
            ref firstBusyGeneration);
        foreach (NativeGeneration generation in _retiredGenerations)
        {
            if (generation.MemoryDetached)
            {
                continue;
            }

            AccumulateOwnerWideActivity(
                generation,
                ref activeOperations,
                ref leaseReturnsInProgress,
                ref busyGenerationCount,
                ref firstBusyGeneration);
        }

        foreach (NativeGeneration generation in _quarantinedGenerations)
        {
            if (generation.MemoryDetached)
            {
                continue;
            }

            AccumulateOwnerWideActivity(
                generation,
                ref activeOperations,
                ref leaseReturnsInProgress,
                ref busyGenerationCount,
                ref firstBusyGeneration);
        }

        return new OwnerWideActivity(
            activeOperations,
            leaseReturnsInProgress,
            busyGenerationCount,
            firstBusyGeneration);
    }

    private static void AccumulateOwnerWideActivity(
        NativeGeneration? generation,
        ref int activeOperations,
        ref int leaseReturnsInProgress,
        ref int busyGenerationCount,
        ref long firstBusyGeneration)
    {
        if (generation is null)
        {
            return;
        }

        activeOperations = checked(activeOperations + generation.ActiveOperations);
        leaseReturnsInProgress = checked(leaseReturnsInProgress + generation.LeaseReturnsInProgress);
        if (generation.ActiveOperations == 0 && generation.LeaseReturnsInProgress == 0)
        {
            return;
        }

        busyGenerationCount = checked(busyGenerationCount + 1);
        if (firstBusyGeneration == 0 || generation.Number < firstBusyGeneration)
        {
            firstBusyGeneration = generation.Number;
        }
    }

    private NativeAllocationInUseException CreateOwnerWideInUseException(
        string operation,
        string reason)
    {
        OwnerWideActivity activity = GetOwnerWideActivityLocked();
        string firstGeneration = activity.BusyGenerationCount == 0
            ? "none"
            : activity.FirstBusyGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new NativeAllocationInUseException(
            $"{_ownerKind}.{operation} could not complete because native ownership is in use across the owner. {reason} Owner-wide activity: {activity.ActiveOperations} active operation(s), {activity.LeaseReturnsInProgress} lease-return token(s), {activity.BusyGenerationCount} busy generation(s); first busy generation {firstGeneration}.",
            _ownerKind,
            activity.FirstBusyGeneration == 0 ? _generation : activity.FirstBusyGeneration,
            _generation,
            operation,
            activity.ActiveOperations,
            allocationId: 0,
            currentLifecycle: _lifecycle,
            ownerWideLeaseReturnCount: activity.LeaseReturnsInProgress,
            ownerWideBusyGenerationCount: activity.BusyGenerationCount);
    }

    private void EnsureOwnerWideIdleLocked(string operation, string reason)
    {
        OwnerWideActivity activity = GetOwnerWideActivityLocked();
        if (activity.ActiveOperations != 0 || activity.LeaseReturnsInProgress != 0)
        {
            throw CreateOwnerWideInUseException(operation, reason);
        }
    }

    private void EnsureOwnerWideLeaseReturnsIdleLocked(string operation, string reason)
    {
        OwnerWideActivity activity = GetOwnerWideActivityLocked();
        if (activity.LeaseReturnsInProgress != 0)
        {
            throw CreateOwnerWideInUseException(operation, reason);
        }
    }

    private readonly record struct OwnerWideActivity(
        int ActiveOperations,
        int LeaseReturnsInProgress,
        int BusyGenerationCount,
        long FirstBusyGeneration);

    private NativeAllocationReturnedException CreateReturnedException(string operation, long generation, long currentGeneration, long allocationId, string reason) =>
        new(
            $"{_ownerKind}.{operation} cannot access native storage. {reason} The handle belongs to generation {generation}; the owner is currently at generation {currentGeneration} (lifecycle {_lifecycle}).",
            _ownerKind,
            generation,
            currentGeneration,
            operation,
            ActiveOperationCountLocked(),
            allocationId,
            _lifecycle);

    private NativeAllocationInUseException CreateInUseException(string operation, long generation, long allocationId, int activeOperationCount, string reason) =>
        new(
            $"{_ownerKind}.{operation} could not complete for generation {generation} because native ownership is in use (lifecycle {_lifecycle}). {reason}",
            _ownerKind,
            generation,
            _generation,
            operation,
            activeOperationCount,
            allocationId,
            _lifecycle);

    private NativeAllocationStateException CreateStateException(string operation, string reason, long allocationId) =>
        new(
            $"{_ownerKind}.{operation} is invalid for the current owner state (lifecycle {_lifecycle}). {reason}",
            _ownerKind,
            _generation,
            _generation,
            operation,
            ActiveOperationCountLocked(),
            allocationId,
            _lifecycle);

    private void DisposeFromFinalizer()
    {
        NativeGeneration? generation;
        lock (_gate)
        {
            if (_lifecycle == NativeOwnerLifecycle.Disposed)
            {
                return;
            }

            generation = _current;
            _current = null;
            _lifecycle = NativeOwnerLifecycle.Disposed;
        }

        try
        {
            if (generation is not null && !generation.MemoryDetached)
            {
                generation.Owner.ReleaseToNative();
            }

            foreach (NativeGeneration retired in _retiredGenerations)
            {
                if (!retired.MemoryDetached)
                {
                    retired.Owner.ReleaseToNative();
                }
            }

            foreach (NativeGeneration quarantined in _quarantinedGenerations)
            {
                if (!quarantined.MemoryDetached)
                {
                    quarantined.Owner.ReleaseToNative();
                }
            }
        }
        catch
        {
        }
    }

    ~NativeOwnerKernel() => DisposeFromFinalizer();
}
