# Supprocom.NativeAllocationManagement

Fast C# usually turns into a negotiation with the garbage collector. You rent arrays
from `ArrayPool`, cache buffers in static fields, avoid allocating in hot loops, and hope
the GC's machine spirit stays calm during the frame or request that matters. Those
rituals reduce allocations, but the collector still decides when memory is scanned,
moved and given back. Your program never really owns its memory.

Native Allocation Management (NAM) gives that ownership back. Your buffers live in
native memory, outside the managed heap, so the GC never scans, moves or collects them.
You decide when storage is reused and when it is freed. You get the speed of manual
memory management without its usual cost: a bundled Roslyn analyzer turns ownership
mistakes, such as using a buffer after it was returned, into compile errors, and
runtime checks reject stale handles instead of letting them read reused memory.

For raw speed, unsafe C# is probably the better option until proven otherwise. NAM aims
to let you stay comfortably trapped in safe C#'s pit of success while still enjoying
potentially massive gains.

## What it is good for

NAM helps when memory work is a real part of your run time:

- **Hot loops that need temporary buffers.** Parsing, serialization, image and audio
  processing, and simulation steps can borrow a buffer per call without allocating.
- **Output whose size you don't know in advance.** Build it in native memory and hand
  it on, without growing and copying managed arrays.
- **Handing data to native code, the GPU or another thread.** Write results once,
  directly where they are consumed, instead of building them in managed arrays and
  copying them over.
- **Large working sets.** Data held in native memory adds no work to garbage
  collections, and it is freed exactly when you say so.
- **A hard memory limit.** A budget can cap how much native memory a component may
  use and refuse work before it starts, rather than failing halfway through.

Plain C# is the better choice when your data holds object references, or when
allocation is not a measured bottleneck. NAM's fast types store unmanaged values
(numbers and structs without references). The
[allocator selection guide][allocator-selection] starts with the managed options and
says when NAM is worth it.

## Quick example

Install the released `0.3.0` package (it targets .NET 10):

```powershell
dotnet add package Supprocom.NativeAllocationManagement --version 0.3.0
```

Borrow a temporary buffer for one call. It returns to the pool when the `using` scope
ends:

```csharp
using Supprocom.NativeAllocationManagement;

static float Peak(ReadOnlySpan<short> pcm)
{
    using Pooled<float> samples = NativePool<float>.Shared.Rent(pcm.Length, 0f);
    Span<float> buffer = samples.AsSpan();

    float peak = 0f;
    for (int index = 0; index < pcm.Length; index++)
    {
        buffer[index] = pcm[index] / 32768f;
        peak = MathF.Max(peak, MathF.Abs(buffer[index]));
    }

    return peak;
}
```

Give several buffers of different types one lifetime, freed together at the end of a
block:

```csharp
using (NativeRegion region = new())
{
    Local<int> ids = region.Lease<int>(1_024, 0);
    Local<double> weights = region.Lease<double>(1_024, 0.5);

    Span<int> idSpan = ids.AsSpan();
    for (int index = 0; index < idSpan.Length; index++)
    {
        idSpan[index] = index;
    }
}
```

The analyzer rejects code that would touch memory after its owner is done with it:

```csharp
Span<int> kept;
using (NativeRegion region = new())
{
    kept = region.Lease<int>(16, 0).AsSpan();
}

kept[0] = 1; // error NAM1051: Span 'kept' comes from a NAM lease and is used after 'region.Dispose()'.
```

## The building blocks

| You need | Use | Closest plain C# |
|---|---|---|
| Typed buffers you rent and return, over and over | `NativePool<T>` | `ArrayPool<T>` |
| Buffers of several types, freed together at the end of a block | `NativeRegion` | a stack frame |
| Buffers of several types, reused every frame, request or batch | `NativeArena` | resetting your own buffers |
| A list that grows while you write it | `NativeBuilder<T>` | `List<T>` |
| One fixed buffer reused by a worker loop | `NativeWorkspace<T>` | a cached array |
| A buffer with one owner that can move between threads | `NativeTransfer<T>` | passing an array and promising not to touch it |
| Read-only data shared between threads | `NativeShared<T>`, `NativeWeak<T>` | a shared immutable array |
| A cap on the native memory a component may use | `NativeMemoryBudget` | none |

The pool and the arena have thread-safe versions (`NativeConcurrentPool<T>`,
`NativeConcurrentArena`) for storage used from several threads. `NativeTransfer<T>`,
`NativeShared<T>` and `NativeWeak<T>` are NAM's smart pointers: unique, shared and weak
ownership, the same ideas as C++'s `unique_ptr`, `shared_ptr` and `weak_ptr`.

## How much faster?

It depends on how much of your time goes into memory. The repository includes a
voxel-meshing demo that does the same work twice: once in carefully optimized safe C#
(pooling, exact sizing, capped buffer retention, and holding back work until memory
is available) and once with NAM. Both produce identical output. Earlier runs of the voxel demo
estimated NAM to be 50 to 85 percent faster without memory limits and 90 to 130
percent faster under tight memory limits. Much of the gain comes from NAM writing its
results straight into the buffer that is uploaded to the GPU, where the safe version
builds them in managed arrays and copies them over.

Results depend on workload, memory limits and runtime configuration. Measure your
own: the [voxel demo][voxel-guide] has runnable commands, and the
[builder benchmark][builder-benchmark] compares known-size and growing output.
Compare elapsed time and total managed/native allocation for your workload.

## Upgrading from 0.2.x

Version 0.3.0 adds memory budgets, prepared pools and arenas, typed layouts and
shared and weak ownership. `NativeTransfer<T>` is now a value type, so rebuild every
project that uses it.

## Ownership diagnostics

The bundled analyzer reports ownership mistakes as compile errors with IDs such as
`NAM1051`. Every rule has a help page in the [diagnostic index][diagnostics] that
explains why the code is unsafe and how to fix it. IDE code fixes can apply the
[common fixes][analyzer-usability]; they never switch the checks off.

## Documentation

- [Getting started][getting-started]: installation, complete examples for every type,
  lifetime rules and cleanup.
- [Allocator selection][allocator-selection]: when to stay managed and which NAM type
  fits.
- [Memory budgets][memory-budgets]: capping native memory and admitting work.
- [Diagnostics and statistics][statistics]: what NAM measures and how to read it.

## Building from source

Build with .NET SDK 10.0.302 and C# 13. The repository pins that SDK without
feature-band roll-forward.

## License

GNU Affero General Public License, version 3 only. See [LICENSE][license] for the
complete terms and [NOTICE][notice] for the project notice and source offer.

[getting-started]: https://github.com/Supprocom/NativeAllocationManagement/blob/main/docs/getting-started.md
[allocator-selection]: https://github.com/Supprocom/NativeAllocationManagement/blob/main/docs/allocator-selection.md
[diagnostics]: https://github.com/Supprocom/NativeAllocationManagement/blob/main/docs/diagnostics/README.md
[analyzer-usability]: https://github.com/Supprocom/NativeAllocationManagement/blob/main/docs/analyzer-usability.md
[memory-budgets]: https://github.com/Supprocom/NativeAllocationManagement/blob/main/docs/memory-budgets.md
[statistics]: https://github.com/Supprocom/NativeAllocationManagement/blob/main/docs/diagnostics.md
[builder-benchmark]: https://github.com/Supprocom/NativeAllocationManagement/blob/main/docs/builder-benchmark.md
[voxel-guide]: https://github.com/Supprocom/NativeAllocationManagement/blob/main/.Demos/01-VoxelChunkPipeline/README.md
[license]: https://github.com/Supprocom/NativeAllocationManagement/blob/main/LICENSE
[notice]: https://github.com/Supprocom/NativeAllocationManagement/blob/main/NOTICE
