# Region initialization

`NativeRegion` hands out several native ranges, possibly of different types, and frees them all when its `using` block ends. Each range comes back as a `Local<T>`. A `Local<T>` is usable only after its initializer has written every element.

```csharp
using Supprocom.NativeAllocationManagement;

float total;
using (NativeRegion region = new())
{
    Local<int> ids = region.Lease<int>(64, static writer =>
    {
        for (int index = 0; index < writer.Length; index++)
        {
            writer.Write(index);
        }
    });

    Local<float> weights = region.Lease<float>(64, 0.5f);

    total = weights.Read(static view => view[0]) + ids.Read(static view => view[1]);
}
```

The steps are always the same: create the region, lease and initialize, use, dispose.

## Initialize

| Form | Result |
|---|---|
| `region.Lease<T>(length, value)` | every element set to `value` |
| `region.Lease<T>(length, writer => ...)` | you fill the range through the writer |

The writer callback must write all `length` elements, in order: `Write(value)` one at a time, or `Fill(value)`. The region cannot be used inside the callback, because `NativeRegion` is a `ref struct` and a lambda cannot capture it.

```csharp
using (NativeRegion region = new())
{
    // WRONG: throws InvalidOperationException (the writer wrote 1 of 4 elements)
    Local<int> partial = region.Lease<int>(4, static writer => writer.Write(1));
}
```

If the callback throws, or leaves elements unwritten, no `Local<T>` is returned. Leases you already hold stay valid.

## Use

Call `Read`, `Access` or `Process` on a `Local<T>`. Each checks the region once, then gives you a view that is valid only inside the callback. For a hot loop, call `view.AsSpan()` once before it.

A span taken from a `Local<T>` must not outlive its region:

```csharp
// WRONG, does not compile: NAM1051 (span used after the region ended)
Span<int> kept;
using (NativeRegion region = new())
{
    kept = region.Lease<int>(4, 0).AsSpan();
}

kept[0] = 1;
```

Avoid managed allocations (strings, boxing, new arrays) inside the `using` block: the analyzer warns about them with NAM1035. Do string formatting and printing after the block.

## Dispose

Leaving the `using` block frees every range at once. There is no per-range free. Keep the region on the thread that created it.

## Diagnostics

| ID | What it catches | How to fix |
|---|---|---|
| [NAM1051](diagnostics/NAM1051.md) | a span used after its region ended | take the span inside the block, or copy the data out |
| [NAM1006](diagnostics/NAM1006.md) | a region held in a plain local, a parameter, or an unbraced `using (...) stmt;` | write `using (NativeRegion region = new()) { ... }` or a `using` declaration |
| [NAM1012](diagnostics/NAM1012.md) | `return region.Lease(...)`, assigning a `Local<T>` to an outer variable, or passing it to a helper | keep it inside the block |
| [NAM1035](diagnostics/NAM1035.md) | a managed allocation inside the region block | move it outside the block |

For more region examples, see [Group allocations in a region](getting-started.md#group-allocations-in-a-region).

## Terms

- **Region**: a one-shot owner of several native ranges that share one lifetime.
- **Local**: a handle to one range inside a region.
- **Writer**: the object an initializer callback uses to write the new range.
