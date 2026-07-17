using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

internal enum NativeOwnerKind
{
    Pool,
    Region
}

/// <summary>Describes the lifecycle gate state of a native owner.</summary>
public enum NativeOwnerLifecycle
{
    /// <summary>The owner accepts allocations and bounded native operations.</summary>
    Active,
    /// <summary>A lifecycle transition has claimed the owner gate.</summary>
    Returning,
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
    long DetachedNativeBytes);

internal static class NativeMemoryTestHooks
{
    private static long _allocationCount;
    private static long _zeroedAllocationCount;
    private static long _freeCount;
    private static long _detachedGenerationCount;
    private static long _outstandingNativeBytes;
    private static long _detachedNativeBytes;
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
        Interlocked.Exchange(ref _forcedFailures, 0);
        Interlocked.Exchange(ref _forcedClearFailures, 0);
        Volatile.Write(ref _operationEntered, null);
        Volatile.Write(ref _beforeOperationEntry, null);
        Volatile.Write(ref _beforeOperationEntryWithKernel, null);
        Volatile.Write(ref _operationEnteredWithAllocation, null);
    }

    internal static long CurrentMetricsEpoch => Volatile.Read(ref _metricsEpoch);

    internal static void FailNextAllocation()
    {
        Interlocked.Increment(ref _forcedFailures);
    }

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

    internal static void FailNextClear()
    {
        Interlocked.Increment(ref _forcedClearFailures);
    }

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
        if (metricsEpoch != CurrentMetricsEpoch)
        {
            return;
        }

        Interlocked.Increment(ref _detachedGenerationCount);
    }

    internal static void RecordDetachedBytes(nuint byteLength, long metricsEpoch)
    {
        if (metricsEpoch != CurrentMetricsEpoch)
        {
            return;
        }

        Interlocked.Add(ref _detachedNativeBytes, checked((long)byteLength));
    }

    internal static NativeMemoryTestMetrics Snapshot()
    {
        return new NativeMemoryTestMetrics(
            Volatile.Read(ref _allocationCount),
            Volatile.Read(ref _zeroedAllocationCount),
            Volatile.Read(ref _freeCount),
            Volatile.Read(ref _detachedGenerationCount),
            Volatile.Read(ref _outstandingNativeBytes),
            Volatile.Read(ref _detachedNativeBytes));
    }

    internal static void SetOperationEntered(Action<string>? callback)
    {
        Volatile.Write(ref _operationEntered, callback);
    }

    internal static void SetBeforeOperationEntry(Action<string>? callback)
    {
        Volatile.Write(ref _beforeOperationEntry, callback);
    }

    internal static void SetBeforeOperationEntryWithKernel(Action<string, NativeOwnerKernel>? callback)
    {
        Volatile.Write(ref _beforeOperationEntryWithKernel, callback);
    }

    internal static void SetOperationEnteredWithAllocation(Action<string, NativeOwnerKernel, long, long>? callback)
    {
        Volatile.Write(ref _operationEnteredWithAllocation, callback);
    }

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

    internal void MarkDetached()
    {
        Volatile.Write(ref _detached, 1);
    }

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

                long metricsEpoch = NativeMemoryTestHooks.RecordAllocation(byteLength, zeroed: true);
                return new NativeSegment(pointer, byteLength, metricsEpoch);
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
    internal NativeSlab(NativeSegment segment, int capacity)
    {
        Segment = segment;
        Capacity = capacity;
    }

    internal NativeSegment Segment { get; }

    internal int Capacity { get; }
}

internal sealed class NativeAllocation
{
    internal NativeAllocation(
        long id,
        NativeSlab? slab,
        NativeSegment? segment,
        nuint offsetBytes,
        int length,
        int capacity)
    {
        Id = id;
        Slab = slab;
        Segment = segment;
        OffsetBytes = offsetBytes;
        Length = length;
        Capacity = capacity;
        Lifecycle = NativeAllocationLifecycle.Active;
    }

    internal long Id { get; }

    internal NativeSlab? Slab { get; }

    internal NativeSegment? Segment { get; }

    internal nuint OffsetBytes { get; }

    internal int Length { get; }

    internal int Capacity { get; }

    internal NativeAllocationLifecycle Lifecycle { get; set; }

    internal int ActiveOperations { get; set; }

    internal Span<T> AsSpan<T>()
        where T : unmanaged
    {
        if (Length == 0)
        {
            return Span<T>.Empty;
        }

        if (Segment is null)
        {
            throw new InvalidOperationException("The native allocation has no segment.");
        }

        nuint byteLength = checked((nuint)Length * (nuint)Unsafe.SizeOf<T>());
        if (OffsetBytes > Segment.ByteLength || byteLength > Segment.ByteLength - OffsetBytes)
        {
            throw new InvalidOperationException("The native allocation bounds are invalid.");
        }

        unsafe
        {
            byte* pointer = (byte*)Segment.Pointer + checked((nint)OffsetBytes);
            return new Span<T>(pointer, Length);
        }
    }
}

internal sealed class NativeGenerationOwner
{
    private readonly object _gate = new();
    private List<NativeSegment>? _segments = new();
    private int _released;
    private int _detached;
    private readonly long _metricsEpoch;

    internal NativeGenerationOwner(long generation)
    {
        Generation = generation;
        _metricsEpoch = NativeMemoryTestHooks.CurrentMetricsEpoch;
    }

    internal long Generation { get; }

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

    internal List<NativeSlab> AvailableSlabs { get; } = new();

    internal NativeSegment? RegionSegment { get; set; }

    internal nuint RegionOffset { get; set; }

    internal int ActiveOperations { get; set; }

    internal int LeaseReturnsInProgress { get; set; }
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

    internal Span<T> GetSpan<T>()
        where T : unmanaged
    {
        return _allocation.AsSpan<T>();
    }

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
    private const nuint DefaultRegionSegmentBytes = 4096;

    private readonly object _gate = new();
    private readonly NativeOwnerKind _kind;
    private readonly string _ownerKind;
    private readonly NativeReturn _returnOnDispose;
    private readonly int _elementSize;
    private readonly int _initialCapacity;
    private readonly nuint _initialRegionBytes;
    private long _generation;
    private long _nextAllocationId;
    private NativeGeneration? _current;
    private NativeOwnerLifecycle _lifecycle;

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
        NativeReturn returnOnDispose,
        int elementSize,
        int initialCapacity,
        nuint initialRegionBytes,
        bool doNotLeaseOnDeclaration)
    {
        _kind = kind;
        _ownerKind = ownerKind;
        _returnOnDispose = returnOnDispose;
        _elementSize = elementSize;
        _initialCapacity = initialCapacity;
        _initialRegionBytes = initialRegionBytes;
        _generation = 0;
        _lifecycle = doNotLeaseOnDeclaration
            ? NativeOwnerLifecycle.Unleased
            : NativeOwnerLifecycle.Active;

        if (doNotLeaseOnDeclaration)
        {
            return;
        }

        NativeGeneration generation = new(_generation);
        _current = generation;

        try
        {
            if (_kind == NativeOwnerKind.Pool && _initialCapacity > 0)
            {
                generation.AvailableSlabs.Add(AddPoolSlabLocked(generation, _initialCapacity, "initial pool reservation", _lifecycle));
            }
            else if (_kind == NativeOwnerKind.Region && _initialRegionBytes > 0)
            {
                NativeSegment segment = NativeSegment.AllocateZeroed(
                    _initialRegionBytes,
                    _ownerKind,
                    _generation,
                    "initial region reservation",
                    _lifecycle);
                generation.Owner.AddSegment(segment);
                generation.RegionSegment = segment;
            }
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
        int elementSize,
        string ownerKind,
        NativeReturn returnOnDispose,
        bool doNotLeaseOnDeclaration)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(elementSize);
        NativeReturnValidation.Validate(returnOnDispose, nameof(returnOnDispose));
        return new NativeOwnerKernel(
            NativeOwnerKind.Pool,
            ownerKind,
            returnOnDispose,
            elementSize,
            initialCapacity,
            initialRegionBytes: 0,
            doNotLeaseOnDeclaration);
    }

    internal static NativeOwnerKernel CreateRegion(
        nuint preAllocateBytes,
        string ownerKind,
        NativeReturn returnOnDispose,
        bool doNotLeaseOnDeclaration)
    {
        NativeReturnValidation.Validate(returnOnDispose, nameof(returnOnDispose));
        return new NativeOwnerKernel(
            NativeOwnerKind.Region,
            ownerKind,
            returnOnDispose,
            elementSize: 0,
            initialCapacity: 0,
            preAllocateBytes,
            doNotLeaseOnDeclaration);
    }

    internal NativePoolLease Rent(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        lock (_gate)
        {
            NativeGeneration generation = EnsureActiveLocked("Rent");
            if (_kind != NativeOwnerKind.Pool)
            {
                throw CreateStateException("Rent", "A region does not expose typed pool leases.", allocationId: 0);
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
            NativeAllocation allocation = new(
                allocationId,
                slab,
                slab?.Segment,
                offsetBytes: 0,
                length,
                slab?.Capacity ?? 0);
            generation.Allocations.Add(allocationId, allocation);
            return new NativePoolLease(generation.Number, allocationId, length, allocation.Capacity);
        }
    }

    internal NativeRegionAllocation LeaseRegion(int length, int elementSize, nuint alignment)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(elementSize);
        if (alignment == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(alignment));
        }

        nuint byteLength = CalculateByteLength(length, elementSize, "region allocation");

        lock (_gate)
        {
            NativeGeneration generation = EnsureActiveLocked("Lease");
            if (_kind != NativeOwnerKind.Region)
            {
                throw CreateStateException("Lease", "A pool does not expose heterogeneous region allocations.", allocationId: 0);
            }

            NativeSegment? segment = null;
            nuint offset = 0;
            if (byteLength > 0)
            {
                segment = generation.RegionSegment;
                if (segment is not null)
                {
                    offset = AlignUp(generation.RegionOffset, alignment);
                }

                if (segment is null || offset > segment.ByteLength || byteLength > segment.ByteLength - offset)
                {
                    nuint segmentBytes = ChooseRegionSegmentBytes(generation, byteLength);
                    NativeSegment nextSegment = NativeSegment.AllocateZeroed(
                        segmentBytes,
                        _ownerKind,
                        generation.Number,
                        "region growth",
                        _lifecycle);
                    generation.Owner.AddSegment(nextSegment);
                    segment = nextSegment;
                    offset = 0;
                }

                generation.RegionSegment = segment;
                generation.RegionOffset = checked(offset + byteLength);
            }

            long allocationId = NextAllocationIdLocked();
            NativeAllocation allocation = new(
                allocationId,
                slab: null,
                segment,
                offset,
                length,
                length);
            generation.Allocations.Add(allocationId, allocation);
            return new NativeRegionAllocation(generation.Number, allocationId, length, length);
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
        NativeOperationToken token;
        NativeMemoryTestHooks.NotifyBeforeOperationEntry(operation, this);
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
            if (allocation.ActiveOperations > 0)
            {
                allocation.ActiveOperations--;
            }

            NativeGeneration? generation = _current;
            if (generation is not null && generation.Number == generationNumber && generation.ActiveOperations > 0)
            {
                generation.ActiveOperations--;
            }
        }
    }

    internal void ReturnLease(long generationNumber, long allocationId)
    {
        lock (_gate)
        {
            if (_lifecycle is NativeOwnerLifecycle.Disposed or NativeOwnerLifecycle.Returned)
            {
                return;
            }

            if (_lifecycle == NativeOwnerLifecycle.Returning)
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
                throw CreateInUseException(
                    "Pooled.Dispose",
                    generation.Number,
                    allocationId,
                    allocation.ActiveOperations,
                    "The lease has an active native operation. No slab was cleared or requeued.");
            }

            if (allocation.Slab is not null && allocation.Length > 0)
            {
                generation.AvailableSlabs.EnsureCapacity(generation.AvailableSlabs.Count + 1);
            }

            allocation.Lifecycle = NativeAllocationLifecycle.Returning;
            generation.LeaseReturnsInProgress++;
            try
            {
                if (allocation.Slab is not null && allocation.Length > 0)
                {
                    ClearAllocation(allocation);
                    generation.AvailableSlabs.Add(allocation.Slab);
                }

                allocation.Lifecycle = NativeAllocationLifecycle.Returned;
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

    internal void ReturnToNativeMemory()
    {
        ReturnGeneration(NativeReturn.ToNativeMemory, "ReturnToNativeMemory");
    }

    internal void ReturnToGarbageCollector()
    {
        ReturnGeneration(NativeReturn.ToGarbageCollector, "ReturnToGarbageCollector");
    }

    internal void LeaseFromMemory()
    {
        lock (_gate)
        {
            EnsureNotDisposedLocked("LeaseFromMemory", 0);
            if (_lifecycle == NativeOwnerLifecycle.Active)
            {
                throw CreateStateException("LeaseFromMemory", "The owner is already active; return its current generation first.", 0);
            }

            if (_lifecycle == NativeOwnerLifecycle.Returning)
            {
                throw CreateInUseException("LeaseFromMemory", _generation, 0, 0, "The previous generation is still returning.");
            }

            if (_kind == NativeOwnerKind.Region && _lifecycle == NativeOwnerLifecycle.Returned)
            {
                throw CreateStateException("LeaseFromMemory", "NativeRegion is a single-generation lexical owner and cannot be re-leased.", 0);
            }

            if (_lifecycle is not (NativeOwnerLifecycle.Unleased or NativeOwnerLifecycle.Returned))
            {
                throw CreateStateException("LeaseFromMemory", "The owner has no leasable generation.", 0);
            }

            NativeGeneration candidate = new(_generation);
            try
            {
                if (_kind == NativeOwnerKind.Pool && _initialCapacity > 0)
                {
                    candidate.AvailableSlabs.Add(AddPoolSlabLocked(candidate, _initialCapacity, "pool re-lease", _lifecycle));
                }
                else if (_kind == NativeOwnerKind.Region && _initialRegionBytes > 0)
                {
                    NativeSegment segment = NativeSegment.AllocateZeroed(
                        _initialRegionBytes,
                        _ownerKind,
                        candidate.Number,
                        "initial region reservation",
                        _lifecycle);
                    candidate.Owner.AddSegment(segment);
                    candidate.RegionSegment = segment;
                }
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

    internal void Dispose()
    {
        lock (_gate)
        {
            if (_lifecycle == NativeOwnerLifecycle.Disposed)
            {
                return;
            }

            if (_lifecycle == NativeOwnerLifecycle.Returning)
            {
                throw CreateInUseException("Dispose", _generation, 0, 0, "Another lifecycle transition is already returning the generation.");
            }

            if (_lifecycle == NativeOwnerLifecycle.Returned)
            {
                _lifecycle = NativeOwnerLifecycle.Disposed;
                GC.SuppressFinalize(this);
                return;
            }

            if (_lifecycle == NativeOwnerLifecycle.Unleased)
            {
                _lifecycle = NativeOwnerLifecycle.Disposed;
                GC.SuppressFinalize(this);
                return;
            }

            NativeGeneration generation = _current!;
            _lifecycle = NativeOwnerLifecycle.Returning;
            if (generation.ActiveOperations != 0 || generation.LeaseReturnsInProgress != 0)
            {
                _lifecycle = NativeOwnerLifecycle.Active;
                throw CreateInUseException(
                    "Dispose",
                    generation.Number,
                    0,
                    generation.ActiveOperations,
                    "No lease was invalidated and no segment was freed.");
            }

            InvalidateGenerationLocked(generation);
            _current = null;
            _generation = checked(_generation + 1);
            ReleaseGenerationLocked(generation, _returnOnDispose);
            _lifecycle = NativeOwnerLifecycle.Disposed;
            GC.SuppressFinalize(this);
        }
    }

    private void ReturnGeneration(NativeReturn policy, string operation)
    {
        lock (_gate)
        {
            EnsureNotDisposedLocked(operation, 0);
            if (_lifecycle == NativeOwnerLifecycle.Returned)
            {
                throw CreateReturnedException(operation, _generation, _generation, 0, "The current generation has already been returned.");
            }

            if (_lifecycle == NativeOwnerLifecycle.Returning)
            {
                throw CreateInUseException(operation, _generation, 0, 0, "Another lifecycle transition is already returning the generation.");
            }

            if (_lifecycle == NativeOwnerLifecycle.Unleased)
            {
                throw CreateStateException(operation, "The owner has not been leased; activate it with LeaseFromMemory first.", 0);
            }

            NativeGeneration generation = _current!;
            _lifecycle = NativeOwnerLifecycle.Returning;
            if (generation.ActiveOperations != 0 || generation.LeaseReturnsInProgress != 0)
            {
                _lifecycle = NativeOwnerLifecycle.Active;
                throw CreateInUseException(
                    operation,
                    generation.Number,
                    0,
                    generation.ActiveOperations,
                    "No lease was invalidated, detached, or freed.");
            }

            InvalidateGenerationLocked(generation);
            _current = null;
            _generation = checked(_generation + 1);
            ReleaseGenerationLocked(generation, policy);
            _lifecycle = NativeOwnerLifecycle.Returned;
        }
    }

    private void ReleaseGenerationLocked(NativeGeneration generation, NativeReturn policy)
    {
        if (policy == NativeReturn.ToNativeMemory)
        {
            generation.Owner.ReleaseToNative();
        }
        else
        {
            generation.Owner.Detach();
        }
    }

    private void InvalidateGenerationLocked(NativeGeneration generation)
    {
        foreach (NativeAllocation allocation in generation.Allocations.Values)
        {
            allocation.Lifecycle = NativeAllocationLifecycle.Returned;
        }

        generation.AvailableSlabs.Clear();
    }

    private NativeGeneration EnsureActiveLocked(
        string operation,
        long generationNumber = 0,
        long allocationId = 0)
    {
        EnsureNotDisposedLocked(operation, allocationId);
        if (_lifecycle == NativeOwnerLifecycle.Returned)
        {
            throw CreateReturnedException(operation, generationNumber, _generation, allocationId, "The owner generation is returned.");
        }

        if (_lifecycle == NativeOwnerLifecycle.Returning)
        {
            throw CreateInUseException(operation, generationNumber, allocationId, 0, "The owner is returning and rejects new operations.");
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
                currentLifecycle: NativeOwnerLifecycle.Disposed);
        }
    }

    private NativeAllocation GetActiveAllocationLocked(
        long generationNumber,
        long allocationId,
        string operation,
        NativeGeneration? knownGeneration = null)
    {
        NativeGeneration generation = knownGeneration ?? EnsureActiveLocked(operation, generationNumber, allocationId);
        if (generation.Number != generationNumber || !generation.Allocations.TryGetValue(allocationId, out NativeAllocation? allocation))
        {
            throw CreateReturnedException(
                operation,
                generationNumber,
                _generation,
                allocationId,
                "The handle belongs to an earlier or unpublished generation.");
        }

        if (allocation.Lifecycle != NativeAllocationLifecycle.Active)
        {
            throw CreateReturnedException(
                operation,
                generationNumber,
                _generation,
                allocationId,
                "The derived allocation has already been returned.");
        }

        return allocation;
    }

    private NativeSlab AddPoolSlabLocked(
        NativeGeneration generation,
        int capacity,
        string operation,
        NativeOwnerLifecycle currentLifecycle)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        nuint byteLength = CalculateByteLength(capacity, _elementSize, operation);
        NativeSegment segment = NativeSegment.AllocateZeroed(
            byteLength,
            _ownerKind,
            generation.Number,
            operation,
            currentLifecycle);
        try
        {
            generation.Owner.AddSegment(segment);
        }
        catch
        {
            segment.FreeNow();
            throw;
        }

        return new NativeSlab(segment, capacity);
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

    private void ClearAllocation(NativeAllocation allocation)
    {
        if (allocation.Length == 0 || allocation.Segment is null)
        {
            return;
        }

        if (NativeMemoryTestHooks.ConsumeForcedClearFailure())
        {
            throw new InvalidOperationException("Injected native clear failure before the staging slab was changed.");
        }

        nuint byteLength = checked((nuint)allocation.Length * (nuint)_elementSize);
        unsafe
        {
            NativeMemory.Clear((byte*)allocation.Segment.Pointer + checked((nint)allocation.OffsetBytes), byteLength);
        }
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

    private static nuint ChooseRegionSegmentBytes(NativeGeneration generation, nuint requiredBytes)
    {
        nuint previousBytes = generation.RegionSegment?.ByteLength ?? 0;
        nuint growthBytes = DefaultRegionSegmentBytes;
        if (previousBytes > growthBytes)
        {
            try
            {
                growthBytes = checked(previousBytes * 2);
            }
            catch (OverflowException)
            {
                growthBytes = requiredBytes;
            }
        }

        return Math.Max(requiredBytes, Math.Max(DefaultRegionSegmentBytes, growthBytes));
    }

    private static nuint AlignUp(nuint value, nuint alignment)
    {
        nuint remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }

    private long NextAllocationIdLocked()
    {
        return checked(++_nextAllocationId);
    }

    private int ActiveOperationCountLocked()
    {
        return _current?.ActiveOperations ?? 0;
    }

    private NativeAllocationReturnedException CreateReturnedException(
        string operation,
        long generation,
        long currentGeneration,
        long allocationId,
        string reason)
    {
        return new NativeAllocationReturnedException(
            $"{_ownerKind}.{operation} cannot access native storage. {reason} The handle belongs to generation {generation}; the owner is currently at generation {currentGeneration} (lifecycle {_lifecycle}).",
            _ownerKind,
            generation,
            currentGeneration,
            operation,
            ActiveOperationCountLocked(),
            allocationId,
            currentLifecycle: _lifecycle);
    }

    private NativeAllocationInUseException CreateInUseException(
        string operation,
        long generation,
        long allocationId,
        int activeOperationCount,
        string reason)
    {
        return new NativeAllocationInUseException(
            $"{_ownerKind}.{operation} could not complete for generation {generation} because native ownership is in use (lifecycle {_lifecycle}). {reason}",
            _ownerKind,
            generation,
            _generation,
            operation,
            activeOperationCount,
            allocationId,
            currentLifecycle: _lifecycle);
    }

    private NativeAllocationStateException CreateStateException(string operation, string reason, long allocationId)
    {
        return new NativeAllocationStateException(
            $"{_ownerKind}.{operation} is invalid for the current owner state (lifecycle {_lifecycle}). {reason}",
            _ownerKind,
            _generation,
            _generation,
            operation,
            ActiveOperationCountLocked(),
            allocationId,
            currentLifecycle: _lifecycle);
    }

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
            if (generation is not null)
            {
                InvalidateGenerationLocked(generation);
            }

            _current = null;
            _lifecycle = NativeOwnerLifecycle.Disposed;
        }

        try
        {
            generation?.Owner.ReleaseToNative();
        }
        catch
        {
        }
    }

    ~NativeOwnerKernel()
    {
        DisposeFromFinalizer();
    }
}
