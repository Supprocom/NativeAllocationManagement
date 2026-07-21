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
    long RetiredNativeBytes)
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
    private static long _metricsEpoch;
    private static int _forcedFailures;
    private static int _forcedClearFailures;
    private static Action<string>? _operationEntered;
    private static Action<string>? _beforeOperationEntry;
    private static Action<string, NativeOwnerKernel>? _beforeOperationEntryWithKernel;
    private static Action<string, NativeOwnerKernel, long, long>? _operationEnteredWithAllocation;

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
        Interlocked.Exchange(ref _forcedFailures, 0);
        Interlocked.Exchange(ref _forcedClearFailures, 0);
        Volatile.Write(ref _operationEntered, null);
        Volatile.Write(ref _beforeOperationEntry, null);
        Volatile.Write(ref _beforeOperationEntryWithKernel, null);
        Volatile.Write(ref _operationEnteredWithAllocation, null);
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
        Volatile.Read(ref _retiredNativeBytes));

    internal static void SetOperationEntered(Action<string>? callback) => Volatile.Write(ref _operationEntered, callback);

    internal static void SetBeforeOperationEntry(Action<string>? callback) => Volatile.Write(ref _beforeOperationEntry, callback);

    internal static void SetBeforeOperationEntryWithKernel(Action<string, NativeOwnerKernel>? callback) =>
        Volatile.Write(ref _beforeOperationEntryWithKernel, callback);

    internal static void SetOperationEnteredWithAllocation(Action<string, NativeOwnerKernel, long, long>? callback) =>
        Volatile.Write(ref _operationEnteredWithAllocation, callback);

    internal static void NotifyBeforeOperationEntry(string operation, NativeOwnerKernel kernel)
    {
        Volatile.Read(ref _beforeOperationEntry)?.Invoke(operation);
        Volatile.Read(ref _beforeOperationEntryWithKernel)?.Invoke(operation, kernel);
    }

    internal static void NotifyOperationEntered(string operation, NativeOwnerKernel kernel, long generation, long allocationId)
    {
        Volatile.Read(ref _operationEntered)?.Invoke(operation);
        Volatile.Read(ref _operationEnteredWithAllocation)?.Invoke(operation, kernel, generation, allocationId);
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
    internal NativeSlab(NativeSegment segment, int capacity, Type elementType, bool containsReferences)
    {
        Segment = segment;
        Capacity = capacity;
        ManagedValues = containsReferences ? Array.CreateInstance(elementType, capacity) : null;
    }

    internal NativeSegment Segment { get; }

    internal int Capacity { get; }

    internal Array? ManagedValues { get; }
}

internal sealed class NativeBumpSegment
{
    internal NativeBumpSegment(NativeSegment segment)
    {
        Segment = segment;
        HighCursor = segment.ByteLength;
    }

    internal NativeSegment Segment { get; }

    internal nuint LowCursor { get; set; }

    internal nuint HighCursor { get; set; }

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
        Array? managedValues,
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
        ManagedValues = managedValues;
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

    internal Array? ManagedValues { get; }

    internal bool IsScoped { get; }

    internal long ScopeEpoch { get; }

    internal NativeAllocationLifecycle Lifecycle { get; set; }

    internal int ActiveOperations { get; set; }

    internal T GetValue<T>(int index)
    {
        if (ManagedValues is T[] values)
        {
            return values[index];
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
        if (ManagedValues is T[] values)
        {
            values[index] = value;
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

    internal void ClearValues()
    {
        if (ManagedValues is not null)
        {
            Array.Clear(ManagedValues, 0, ManagedValues.Length);
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
        NativeSegment[] segments;
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            segments = _segments?.ToArray() ?? [];
            _segments = null;
        }

        foreach (NativeSegment segment in segments)
        {
            segment.FreeNow();
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

    internal List<NativeSlab> Slabs { get; } = [];

    internal List<NativeSlab> AvailableSlabs { get; } = [];

    internal List<NativeBumpSegment> BumpSegments { get; } = [];

    internal int ActiveOperations { get; set; }

    internal int LeaseReturnsInProgress { get; set; }

    internal long RetiredNativeBytes { get; set; }

    internal long ScopeEpoch { get; set; }

    internal List<NativeAllocation> ScopedPending { get; } = [];

    internal Dictionary<NativeBumpSegment, nuint>? ScopedBaseline { get; set; }

    internal bool MemoryDetached { get; set; }
}

internal ref struct NativeOperationToken
{
    private NativeOwnerKernel? _kernel;
    private readonly NativeGenerationOwner _generationOwner;
    private readonly NativeAllocation _allocation;
    private readonly long _generation;
    private readonly string _operation;

    internal NativeOperationToken(
        NativeOwnerKernel kernel,
        NativeGenerationOwner generationOwner,
        NativeAllocation allocation,
        long generation,
        string operation)
    {
        _kernel = kernel;
        _generationOwner = generationOwner;
        _allocation = allocation;
        _generation = generation;
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
        kernel.ExitOperation(_generation, _allocation, _operation);
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
    private NativeGeneration? _current;
    private NativeOwnerLifecycle _lifecycle;
    private readonly List<NativeGeneration> _retiredGenerations = [];

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
                    slab = AddPoolSlabLocked(generation, length, "pool growth", _lifecycle);
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
                managedValues: slab?.ManagedValues,
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
                        nuint segmentBytes = ChooseBumpSegmentBytes(generation, byteLength);
                        NativeSegment segment = NativeSegment.AllocateZeroed(
                            segmentBytes,
                            _ownerKind,
                            generation.Number,
                            scoped ? "scoped growth" : "allocation growth",
                            _lifecycle);
                        createdSegment = new NativeBumpSegment(segment);
                        bumpSegment = createdSegment;
                        generation.BumpSegments.Add(createdSegment);
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
                Array? managedValues = ContainsReferences(elementType) && length > 0
                    ? Array.CreateInstance(elementType, length)
                    : null;
                NativeAllocation allocation = new(
                    allocationId,
                    slab: null,
                    bumpSegment,
                    offset,
                    length,
                    length,
                    byteLength,
                    managedValues,
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
        lock (_gate)
        {
            NativeGeneration generation = EnsureActiveLocked(operation, generationNumber, allocationId);
            NativeAllocation allocation = GetActiveAllocationLocked(generationNumber, allocationId, operation, generation);
            generation.ActiveOperations++;
            allocation.ActiveOperations++;
            token = new NativeOperationToken(this, generation.Owner, allocation, generationNumber, operation);
        }

        NativeMemoryTestHooks.NotifyOperationEntered(operation, this, generationNumber, allocationId);
        return token;
    }

    internal void ExitOperation(long generationNumber, NativeAllocation allocation, string operation)
    {
        lock (_gate)
        {
            NativeGeneration? generation = FindGenerationLocked(generationNumber);
            if (generation is null)
            {
                return;
            }

            if (allocation.ActiveOperations > 0)
            {
                allocation.ActiveOperations--;
            }

            if (generation.ActiveOperations > 0)
            {
                generation.ActiveOperations--;
            }

            if (generation != _current && generation.ActiveOperations == 0)
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

            allocation.Lifecycle = NativeAllocationLifecycle.Returning;
            generation.LeaseReturnsInProgress++;
            try
            {
                EnsureNoInjectedClearFailureLocked("Pooled.Dispose", afterStateChange: true);
                ClearAllocationStorage(allocation);
                if (allocation.Slab is not null && allocation.Length > 0)
                {
                    generation.AvailableSlabs.Add(allocation.Slab);
                }

                allocation.Lifecycle = NativeAllocationLifecycle.Returned;
                generation.ScopedPending.Remove(allocation);
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

            EnsureNoInjectedClearFailureLocked("RecycleScoped");
            foreach (NativeAllocation allocation in generation.ScopedPending.ToArray())
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
                    generation.AvailableSlabs.Add(allocation.Slab);
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
                    && !generation.Allocations.Values.Any(allocation =>
                        allocation.IsScoped && ReferenceEquals(allocation.BumpSegment, segment)))
                {
                    segment.HighCursor = segment.Segment.ByteLength;
                }
            }

            generation.ScopedPending.Clear();
            generation.ScopedBaseline = null;
            generation.ScopeEpoch = checked(generation.ScopeEpoch + 1);
        }
    }

    internal nuint TrimRetainedMemory()
    {
        lock (_gate)
        {
            EnsureActiveLocked("TrimRetainedMemory");
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
            EnsureActiveLocked("TrimRetainedMemoryByBytes");
            return TrimRetainedMemoryLocked(bytesToRelease);
        }
    }

    internal nuint TrimRetainedMemoryByLeaseSize(int leaseLength, int elementSize, Type elementType)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(leaseLength);
        nuint requested = CalculateByteLength(leaseLength, ContainsReferences(elementType) ? IntPtr.Size : elementSize, "lease-sized trim");
        lock (_gate)
        {
            EnsureActiveLocked("TrimRetainedMemoryByLeaseSize");
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

            if (_lifecycle == NativeOwnerLifecycle.Unleased)
            {
                _lifecycle = NativeOwnerLifecycle.Disposed;
                GC.SuppressFinalize(this);
                return;
            }

            if (_lifecycle == NativeOwnerLifecycle.Returned)
            {
                _lifecycle = NativeOwnerLifecycle.Disposed;
                GC.SuppressFinalize(this);
                return;
            }

            NativeGeneration generation = _current!;
            if (generation.ActiveOperations != 0 || generation.LeaseReturnsInProgress != 0)
            {
                throw CreateInUseException("Dispose", generation.Number, 0, generation.ActiveOperations, "No generation was invalidated or freed.");
            }

            EnsureNoInjectedClearFailureLocked("Dispose");
            ClearGenerationAllocationsLocked(generation, skipActiveOperations: false);
            _lifecycle = NativeOwnerLifecycle.Returning;
            InvalidateGenerationLocked(generation);
            _current = null;
            _generation = checked(_generation + 1);
            if (_returnMemoryOnDispose == NativeMemoryReturn.ToNativeMemory)
            {
                generation.Owner.ReleaseToNative();
            }
            else
            {
                generation.Owner.Detach();
            }

            _lifecycle = NativeOwnerLifecycle.Disposed;
            GC.SuppressFinalize(this);
        }
    }

    private void ReserveInitialStorageLocked(NativeGeneration generation, string operation, NativeOwnerLifecycle observedLifecycle)
    {
        if (_kind == NativeOwnerKind.Pool && _initialReservation > 0)
        {
            int capacity = checked((int)_initialReservation);
            generation.Slabs.Add(AddPoolSlabLocked(generation, capacity, operation, observedLifecycle));
            generation.AvailableSlabs.Add(generation.Slabs[^1]);
        }
        else if (_kind is NativeOwnerKind.Region or NativeOwnerKind.Arena && _initialReservation > 0)
        {
            NativeSegment segment = NativeSegment.AllocateZeroed(_initialReservation, _ownerKind, generation.Number, operation, observedLifecycle);
            NativeBumpSegment bump = new(segment);
            generation.BumpSegments.Add(bump);
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
            return new NativeSlab(segment, capacity, _elementType!, _containsReferences);
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
        IEnumerable<NativeBumpSegment> segments = scoped ? generation.BumpSegments.AsEnumerable().Reverse() : generation.BumpSegments;
        foreach (NativeBumpSegment segment in segments)
        {
            if (scoped)
            {
                if (segment.HighCursor < segment.LowCursor || byteLength > segment.HighCursor - segment.LowCursor)
                {
                    continue;
                }

                nuint candidate = AlignDown(segment.HighCursor - byteLength, alignment);
                if (candidate >= segment.LowCursor && byteLength <= segment.HighCursor - candidate)
                {
                    return segment;
                }
            }
            else
            {
                nuint candidate = AlignUp(segment.LowCursor, alignment);
                if (candidate <= segment.HighCursor && byteLength <= segment.HighCursor - candidate)
                {
                    return segment;
                }
            }
        }

        return null;
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

            NativeGeneration generation = _current!;
            if (generation.LeaseReturnsInProgress != 0 || (policy == NativeMemoryReturn.ToNativeMemory && generation.ActiveOperations != 0))
            {
                throw CreateInUseException(operation, generation.Number, 0, generation.ActiveOperations, "No lease was invalidated, detached, or freed.");
            }

            EnsureNoInjectedClearFailureLocked(operation);
            ClearGenerationAllocationsLocked(generation, skipActiveOperations: true);
            _lifecycle = NativeOwnerLifecycle.Returning;
            InvalidateGenerationLocked(generation);
            _current = null;
            _generation = checked(_generation + 1);
            if (policy == NativeMemoryReturn.ToNativeMemory)
            {
                generation.Owner.ReleaseToNative();
            }
            else
            {
                generation.MemoryDetached = true;
                generation.Owner.Detach();
                if (generation.ActiveOperations != 0)
                {
                    _retiredGenerations.Add(generation);
                }
            }

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
            _lifecycle = NativeOwnerLifecycle.RollingOver;
            NativeGeneration next = new(checked(current.Number + 1));
            bool tolerant = policy == NativeMemoryReturn.ToGarbageCollector;
            try
            {
                foreach (NativeAllocation allocation in current.Allocations.Values)
                {
                    if (!tolerant || allocation.ActiveOperations == 0)
                    {
                        ClearAllocationStorage(allocation);
                    }
                }

                foreach (NativeSlab slab in current.Slabs.ToArray())
                {
                    bool busy = current.Allocations.Values.Any(allocation => ReferenceEquals(allocation.Slab, slab) && allocation.ActiveOperations != 0);
                    if (!busy)
                    {
                        TransferSegmentLocked(current, next, slab.Segment);
                        current.Slabs.Remove(slab);
                        next.Slabs.Add(slab);
                        next.AvailableSlabs.Add(slab);
                    }
                }

                foreach (NativeBumpSegment bump in current.BumpSegments.ToArray())
                {
                    bool busy = current.Allocations.Values.Any(allocation => ReferenceEquals(allocation.BumpSegment, bump) && allocation.ActiveOperations != 0);
                    if (!busy)
                    {
                        bump.LowCursor = 0;
                        bump.HighCursor = bump.Segment.ByteLength;
                        TransferSegmentLocked(current, next, bump.Segment);
                        current.BumpSegments.Remove(bump);
                        next.BumpSegments.Add(bump);
                    }
                }

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
                next.Owner.ReleaseToNative();
                throw;
            }
        }
    }

    private void DrainRetiredGenerationLocked(NativeGeneration generation)
    {
        _retiredGenerations.Remove(generation);
        if (generation.MemoryDetached)
        {
            ClearGenerationAllocationsLocked(generation, skipActiveOperations: false);
            generation.Owner.ReleaseToNative();
            return;
        }

        ClearGenerationAllocationsLocked(generation, skipActiveOperations: false);
        if (generation.RetiredNativeBytes != 0)
        {
            NativeMemoryTestHooks.RecordRetiredBytes((nuint)generation.RetiredNativeBytes, add: false, metricsEpoch: generation.Owner.MetricsEpoch);
            generation.RetiredNativeBytes = 0;
        }

        NativeGeneration? current = _current;
        if (current is null || _lifecycle == NativeOwnerLifecycle.Disposed)
        {
            generation.Owner.ReleaseToNative();
            return;
        }

        foreach (NativeSlab slab in generation.Slabs)
        {
            ClearSlabStorage(slab);
            TransferSegmentLocked(generation, current, slab.Segment);
            current.Slabs.Add(slab);
            current.AvailableSlabs.Add(slab);
        }

        foreach (NativeBumpSegment bump in generation.BumpSegments)
        {
            bump.LowCursor = 0;
            bump.HighCursor = bump.Segment.ByteLength;
            TransferSegmentLocked(generation, current, bump.Segment);
            current.BumpSegments.Add(bump);
        }

        generation.Owner.ReleaseToNative();
    }

    private NativeGeneration? FindGenerationLocked(long generationNumber)
    {
        if (_current?.Number == generationNumber)
        {
            return _current;
        }

        return _retiredGenerations.FirstOrDefault(generation => generation.Number == generationNumber);
    }

    private void TransferSegmentLocked(NativeGeneration source, NativeGeneration destination, NativeSegment segment)
    {
        source.Owner.RemoveSegment(segment);
        destination.Owner.AddSegment(segment);
    }

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
        if (allocation.Slab is not null)
        {
            ClearSlabStorage(allocation.Slab);
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

    private static void ClearSlabStorage(NativeSlab slab)
    {
        if (slab.ManagedValues is not null)
        {
            Array.Clear(slab.ManagedValues, 0, slab.ManagedValues.Length);
        }

        unsafe
        {
            NativeMemory.Clear((byte*)slab.Segment.Pointer, slab.Segment.ByteLength);
        }
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
            while (current.AvailableSlabs.Count > 0 && (requestedBytes is null || released < requestedBytes.Value))
            {
                NativeSlab slab = current.AvailableSlabs[^1];
                current.AvailableSlabs.RemoveAt(current.AvailableSlabs.Count - 1);
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
        }

        return released;
    }

    private static nuint GetBusySegmentBytes(NativeGeneration generation)
    {
        HashSet<NativeSegment> segments = [];
        foreach (NativeAllocation allocation in generation.Allocations.Values)
        {
            if (allocation.ActiveOperations != 0 && allocation.Segment is NativeSegment segment)
            {
                segments.Add(segment);
            }
        }

        nuint total = 0;
        foreach (NativeSegment segment in segments)
        {
            total = checked(total + segment.ByteLength);
        }

        return total;
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
        if (previous > growth)
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

    private int ActiveOperationCountLocked() => _current?.ActiveOperations ?? 0;

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
            generation?.Owner.ReleaseToNative();
            foreach (NativeGeneration retired in _retiredGenerations)
            {
                retired.Owner.ReleaseToNative();
            }
        }
        catch
        {
        }
    }

    ~NativeOwnerKernel() => DisposeFromFinalizer();
}
