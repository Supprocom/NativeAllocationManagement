# Typed layouts

A `NativeLayout` puts several native arrays of different types into one block with one owner. Use it only when the arrays are created, moved and freed together: one surviving owner or slice keeps the whole block alive.

```csharp
using Supprocom.NativeAllocationManagement;

NativeLayoutBuilder builder = new(2);
NativeLayoutField<int> ids = builder.Add<int>(64);
NativeLayoutField<float> weights = builder.Add<float>(64, alignment: 16);
NativeLayout layout = builder.Build();

NativeMemoryBudget budget = new(layout.BackingBytes);
if (!layout.TryReserve(budget, out NativeLayoutReservation? permission, out _))
    return;

try
{
    permission.Value.PrepareBacking();
    using NativeLayoutOwner owner = NativeLayoutReservation.Activate(
        ref permission, (ids, weights), static (writer, fields) =>
        {
            writer.Region(fields.ids).Fill(1);
            writer.Region(fields.weights).Fill(0.5f);
        });

    float sum = owner.Read(weights, static (view, field) =>
    {
        float total = 0;
        foreach (float value in view.Region(field)) total += value;
        return total;
    });

    NativeLayoutStatistics stats = owner.CaptureSnapshot();
    Console.WriteLine($"{sum} {stats.LogicalInitializedBytes} {stats.AlignmentSlackBytes}");
}
finally { permission?.Dispose(); }
```

The steps are always the same: describe, reserve, prepare, fill, use, dispose.

## 1. Describe

`new NativeLayoutBuilder(n)` takes the number of fields (at most 64). `Add<T>(length, alignment)` adds one and returns its `NativeLayoutField<T>` token. `Build()` freezes the layout; build it once and reuse it.

| Call | Throws |
|---|---|
| `new NativeLayoutBuilder(n)` with `n` above 64 | `ArgumentOutOfRangeException` |
| `Add` beyond `n` fields, or after `Build()` | `InvalidOperationException` |
| `alignment` that is not a power of two, is below the type's own alignment, or is above 64 | `ArgumentOutOfRangeException` |

`layout.BackingBytes` is the size to budget. It includes padding between fields and up to `Alignment - 1` spare bytes to align the start. `layout.Describe(field)` shows one field's offset and size.

## 2. Reserve and prepare

`layout.TryReserve(budget, out permission, out reason)` sets aside `BackingBytes` from a [`NativeMemoryBudget`](memory-budgets.md). It returns `false` if they do not fit, and allocates nothing. `TryReserve` already holds the bytes. `permission.Value.PrepareBacking()` is optional: it only does the native allocation now instead of inside `Activate`.

## 3. Fill

`Activate` consumes the permission, runs your initializer and returns the owner. The initializer must fill every field.

```csharp
// WRONG: throws InvalidOperationException (weights was never initialized)
using NativeLayoutOwner partial = NativeLayoutReservation.Activate(
    ref permission, ids, static (writer, id) => writer.Region(id).Fill(1));
```

If `Activate` throws, no owner exists and the reserved bytes are released. The initializer's writer must not leave the callback.

## 4. Use

`Read` and `Access` take a state value and a static callback. Each field is checked once when you ask for its span, not per element. A token from another layout, or with the wrong element type, throws `ArgumentException`.

```csharp
NativeLayoutField<int> foreign = new NativeLayoutBuilder(1).Add<int>(4);

// WRONG: throws ArgumentException (the field belongs to another layout)
owner.Read(foreign, static (view, field) => view.Region(field).Length);

// WRONG, does not compile: NAM1024 (the span leaves the callback)
owner.Access(weights, static (view, field) => Keep(view.Region(field)));

static void Keep(Span<float> values) { }
```

Move an owner with `NativeLayoutOwner.Move(ref nullableOwner)`; the old variable becomes `null`.

## 5. Copy one field out

`owner.DetachField(field, destinationBudget)` copies one field into its own `NativeTransfer<T>` ([Unique ownership](unique-ownership.md)). Do this when one field must outlive the rest.

```csharp
NativeMemoryBudget destination = new(64 * sizeof(float));
NativeTransfer<float> copy = owner.DetachField(weights, destination);
try { Console.WriteLine(copy.Length); }   // 64
finally { copy.Dispose(); }
```

If `destination` cannot hold the copy, it throws `NativeMemoryBudgetExceededException`, and the owner is unchanged. A field cannot be disposed by itself. The block stays alive while its owner does.

## 6. Dispose

Dispose the owner once. Dispose the permission in `finally`; after `Activate` it is `null`, so that is a no-op.

```csharp
// WRONG, does not compile: NAM1025 (owner is never disposed)
NativeLayoutOwner leaked = NativeLayoutReservation.Activate(
    ref permission, weights, static (writer, field) => writer.Region(field).Fill(1f));
```

## Look at its state

`owner.CaptureSnapshot()` returns a `NativeLayoutStatistics`.

| Field | Meaning |
|---|---|
| `LogicalInitializedBytes` | bytes of field data |
| `InterRegionPaddingBytes` | padding between fields |
| `AlignmentSlackBytes` | spare bytes used to align the start |
| `LayoutExtentBytes` | field data plus padding |
| `InitializedRegionCount`, `RegionCount` | fields filled and fields declared |
| `CopiedBytes`, `DetachedOwnerCount` | bytes copied by `DetachField`, and how many succeeded |
| `Ownership` | the `NativeTransferStatistics` of the owner |

## Diagnostics

| ID | What it catches | How to fix |
|---|---|---|
| [NAM1050](diagnostics/NAM1050.md) | using the permission without checking the `TryReserve` result (an unguarded permission also fires NAM1025) | use it only in the `true` branch |
| [NAM1024](diagnostics/NAM1024.md) | a view or writer escaping its callback | copy the data out |
| [NAM1025](diagnostics/NAM1025.md) | an owner that is never disposed or moved | `using` or `Dispose()` |

## Terms

- **Field**: one typed array inside the layout, named by a `NativeLayoutField<T>` token. A token is an identity, not an address.
- **Backing**: the single native block that holds all fields.
- **Reservation (permission)**: the right to build one owner, charged to a budget before any memory is touched.
