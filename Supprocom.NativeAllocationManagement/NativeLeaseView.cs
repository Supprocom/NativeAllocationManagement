using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

/// <summary>A scoped view for one active native lease operation.</summary>
/// <typeparam name="T">The value type or reference type in the owner.</typeparam>
public readonly ref struct NativeLeaseView<T>
{
    // Sixteen bytes, so a view argument travels in two registers and a loop
    // over its indexer runs like a loop over a span.
    //
    // Values without references: _reference is the first element, _length the
    // logical length and _capacity the physical capacity.
    //
    // Values that are or contain references live behind their record. Whether
    // T has references is a JIT-time constant, so those views reuse the fields:
    // _reference points at the record's Self field (never read as a T) and
    // _length is the requested length, or -1 for the record's own. A raw
    // pointer view of such values has a null reference and keeps its length;
    // any access to it hits the span type guard.
    private readonly ref T _reference;
    private readonly int _length;
    private readonly int _capacity;

    internal NativeLeaseView(NativeAllocation allocation)
        : this(allocation, -1)
    {
    }

    // The constructors assign fields only in statements, never through a
    // conditional expression: a branch inside a field assignment leaves the
    // view's address on the evaluation stack, which keeps the JIT from
    // holding the view in registers wherever the constructor is inlined.
    internal NativeLeaseView(
        NativeAllocation allocation,
        int length)
    {
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            Span<T> values = allocation.AsSpan<T>();
            int capacity = allocation.Capacity;
            if (length >= 0)
            {
                values = values[..length];
                capacity = length;
            }

            _reference = ref MemoryMarshal.GetReference(values);
            _length = values.Length;
            _capacity = capacity;
            return;
        }

        _reference = ref Unsafe.As<NativeAllocation, T>(ref allocation.Self);
        _length = length;
        _capacity = 0;
    }

    internal unsafe NativeLeaseView(
        IntPtr pointer,
        int length)
    {
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            _reference = ref Unsafe.AsRef<T>((void*)pointer);
        }
        else
        {
            _reference = ref Unsafe.NullRef<T>();
        }

        _length = length;
        _capacity = length;
    }

    /// <summary>Gets the logical element count.</summary>
    public int Length => RuntimeHelpers.IsReferenceOrContainsReferences<T>()
        ? RecordLength(Record, _length)
        : _length;

    /// <summary>Gets the physical capacity in elements.</summary>
    public int Capacity => RuntimeHelpers.IsReferenceOrContainsReferences<T>()
        ? _length >= 0 || Record is null ? _length : Record.Capacity
        : _capacity;

    /// <summary>Gets a direct span for storage without managed references.</summary>
    public Span<T> AsSpan() => RuntimeHelpers.IsReferenceOrContainsReferences<T>()
        ? RecordSpan()
        : MemoryMarshal.CreateSpan(ref _reference, _length);

    /// <summary>Reads or writes one value in the logical range.</summary>
    public T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                if ((uint)index >= (uint)_length)
                {
                    ThrowIndex(index, _length);
                }

                return Unsafe.Add(ref _reference, index);
            }

            return GetRecordValue(ref _reference, _length, index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set
        {
            if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                if ((uint)index >= (uint)_length)
                {
                    ThrowIndex(index, _length);
                }

                Unsafe.Add(ref _reference, index) = value;
                return;
            }

            SetRecordValue(ref _reference, _length, index, value);
        }
    }

    // The record of a view of values with references; null for a raw pointer
    // view or a default view.
    private NativeAllocation? Record => RecordOf(ref _reference);

    private static NativeAllocation? RecordOf(ref T reference) =>
        Unsafe.IsNullRef(ref reference) ? null : Unsafe.As<T, NativeAllocation>(ref reference);

    private static int RecordLength(NativeAllocation? record, int length) =>
        length >= 0 || record is null ? length : record.Length;

    // Static, so no view's address is taken and a view argument stays in
    // registers. Not NoInlining: the JIT then sees that it never returns and
    // keeps a caller's loop registers free of the call.
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowIndex(int index, int length)
    {
        ValidateIndex(index, length);
        throw new System.Diagnostics.UnreachableException();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static unsafe T GetRecordValue(ref T reference, int length, int index)
    {
        NativeAllocation? record = RecordOf(ref reference);
        int actualLength = RecordLength(record, length);
        ValidateIndex(index, actualLength);
        return record is null
            ? new Span<T>(null, actualLength)[index]
            : record.GetValue<T>(index);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static unsafe void SetRecordValue(ref T reference, int length, int index, T value)
    {
        NativeAllocation? record = RecordOf(ref reference);
        int actualLength = RecordLength(record, length);
        ValidateIndex(index, actualLength);
        if (record is null)
        {
            new Span<T>(null, actualLength)[index] = value;
            return;
        }

        record.SetValue(index, value);
    }

    // Values with references have no direct span: a record refuses, and a raw
    // pointer view hits the span type guard, as every direct access always did.
    private unsafe Span<T> RecordSpan()
    {
        NativeAllocation? record = Record;
        return record is null
            ? new Span<T>(null, _length)
            : record.AsSpan<T>()[..Length];
    }

    /// <summary>Clears the logical range.</summary>
    public void Clear()
    {
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            NativeMemoryAccounting.ClearStorage(MemoryMarshal.CreateSpan(ref _reference, _length));
            return;
        }

        NativeAllocation? record = Record;
        if (record is null)
        {
            NativeMemoryAccounting.ClearStorage(RecordSpan());
            return;
        }

        if (_length < 0)
        {
            record.ClearValues();
            return;
        }

        if (_length != 0)
        {
            record.ReferenceRoots!.ClearRange(record.Segment!, record.OffsetBytes, _length);
        }
    }

    /// <summary>Writes one value to the logical range.</summary>
    public void Fill(T value)
    {
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            MemoryMarshal.CreateSpan(ref _reference, _length).Fill(value);
            return;
        }

        NativeAllocation? record = Record;
        if (record is null)
        {
            RecordSpan().Fill(value);
            return;
        }

        if (_length < 0)
        {
            record.Fill(value);
            return;
        }

        for (int index = 0; index < _length; index++)
        {
            record.SetValue(index, value);
        }
    }

    /// <summary>Copies an exact source range into this view.</summary>
    public void CopyFrom(scoped ReadOnlySpan<T> source)
    {
        if (source.Length != Length)
        {
            throw new ArgumentException(
                "The source length must equal the lease logical length.",
                nameof(source));
        }

        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            NativeMemoryAccounting.CopyRange(source, MemoryMarshal.CreateSpan(ref _reference, _length));
            return;
        }

        NativeAllocation? record = Record;
        if (record is null)
        {
            NativeMemoryAccounting.CopyRange(source, RecordSpan());
            return;
        }

        record.CopyFrom(source);
    }

    /// <summary>Copies this view into a sufficient destination range.</summary>
    public void CopyTo(scoped Span<T> destination)
    {
        if (destination.Length < Length)
        {
            throw new ArgumentException(
                "The destination must contain the complete lease range.",
                nameof(destination));
        }

        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            NativeMemoryAccounting.CopyRange(
                (ReadOnlySpan<T>)MemoryMarshal.CreateSpan(ref _reference, _length),
                destination);
            return;
        }

        NativeAllocation? record = Record;
        if (record is null)
        {
            NativeMemoryAccounting.CopyRange((ReadOnlySpan<T>)RecordSpan(), destination);
            return;
        }

        if (_length < 0)
        {
            record.CopyTo(destination);
            return;
        }

        for (int index = 0; index < _length; index++)
        {
            destination[index] = record.GetValue<T>(index);
        }
    }

    private static void ValidateIndex(int index, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (index >= length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index),
                index,
                "The index is outside the logical lease range.");
        }
    }
}
