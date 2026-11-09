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

Install the released `0.3.2` package (it targets .NET 10):

```powershell
dotnet add package Supprocom.NativeAllocationManagement --version 0.3.2
```

The full walkthrough is in [Getting started][getting-started], and every rule has a page in
the [diagnostic index][diagnostics].

For raw speed, unsafe C# is probably the better option until proven otherwise. NAM aims
to let you stay comfortably trapped in safe C#'s pit of success while still enjoying
potentially massive gains.

## What it is good for

- **Hot loops that need temporary buffers.** Parsing, serialization, image and audio
  processing, and simulation steps can borrow a buffer per call without allocating.
- **Output whose size you don't know in advance.** Build it in native memory and hand
  it on, without growing and copying managed arrays.
- **Handing data to native code, the GPU or another thread.** Write results once,
  directly where they are consumed, instead of building them in managed arrays and
  copying them over.
- **Large working sets.** Data held in native memory adds no work to garbage
  collections, and you decide when it is reused or freed.
- **A hard memory limit.** A budget can cap how much native memory a component may
  use and refuse work before it starts, rather than failing halfway through.

Plain C# is the better choice when your data holds object references, or when
allocation is not a measured bottleneck. NAM's fast types store unmanaged values
(numbers and structs without references). The
[allocator selection guide][allocator-selection] starts with the managed options and
says when NAM is worth it.

## How much faster?

It depends on how much of your time goes into memory. The repository's voxel-meshing
demo does the same work twice, once in carefully optimized safe C# and once with NAM,
and checks that both produce identical output.

| | Time per request (median of 9) |
|---|---|
| Safe C# | 103.4 ms |
| NAM | 50.6 ms |
| Safe / NAM | 2.05x (1.88x to 2.16x across rounds) |

Measured on NAM 0.3.0: one worker, 256 MiB requests, process CPU time per request, on a
4-core i5-7600K under Linux. Much of the gain comes from NAM writing results straight
into the buffer that is uploaded to the GPU; the safe version builds them in managed
arrays and copies them over.

Reproduce it from the repository root (Linux, about a minute):

```bash
dotnet run --project .Demos/01-VoxelChunkPipeline/InProcessBenchmark/Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.InProcessBenchmark.csproj -c Release -- compare
```

Results depend on workload, memory limits and runtime configuration. Measure your
own: the [voxel demo][voxel-guide] has runnable commands, and the
[builder benchmark][builder-benchmark] compares known-size and growing output.
Compare elapsed time and total managed/native allocation for your workload.

## The building blocks

| You need | Use | Closest plain C# |
|---|---|---|
| Typed buffers you rent and return, over and over | `NativePool<T>` | `ArrayPool<T>` |
| Buffers of several types, freed together at the end of a block | `NativeRegion` | a stack frame |
| Buffers of several types, reused every frame, request or batch | `NativeArena` | resetting your own buffers |
| A sequence that grows while you write it | `NativeBuilder<T>` | `List<T>` |
| One fixed buffer reused by a worker loop | `NativeWorkspace<T>` | a cached array |
| A buffer with one owner that can move between threads | `NativeTransfer<T>` | passing an array and promising not to touch it |
| Read-only data shared between threads | `NativeShared<T>`, `NativeWeak<T>` | a shared immutable array |
| A cap on the native memory a component may use | `NativeMemoryBudget` | none |

`NativePool<T>` and `NativeArena` have thread-safe versions (`NativeConcurrentPool<T>`,
`NativeConcurrentArena`). `NativeTransfer<T>`, `NativeShared<T>` and `NativeWeak<T>`
are unique, shared and weak ownership, like C++'s `unique_ptr`, `shared_ptr` and
`weak_ptr`. All of them store unmanaged values only.

## Mistakes the analyzer rejects

The bundled analyzer turns ownership mistakes into compile errors.

```csharp
// WRONG, does not compile: NAM1025 (transfer never disposed or moved)
NativeTransfer<uint> output = builder.Complete();

// WRONG, does not compile: NAM1029 (builder already completed)
builder.Append(5u);

// WRONG, does not compile: NAM1031 (builder never completed or disposed)
NativeBuilder<uint> leaked = new();
```

| ID | What it catches | How to fix |
|---|---|---|
| [NAM1051](https://github.com/Supprocom/NativeAllocationManagement/blob/main/docs/diagnostics/NAM1051.md) | A span used after its lease ended | Take a new span from a current lease |
| [NAM1025](https://github.com/Supprocom/NativeAllocationManagement/blob/main/docs/diagnostics/NAM1025.md) | A transfer not disposed or moved | Use a `using` declaration |
| [NAM1029](https://github.com/Supprocom/NativeAllocationManagement/blob/main/docs/diagnostics/NAM1029.md) | A builder used after `Complete` or `Dispose` | Do all appends before `Complete` |
| [NAM1031](https://github.com/Supprocom/NativeAllocationManagement/blob/main/docs/diagnostics/NAM1031.md) | A builder never completed or disposed | Use a `using` declaration |

Every rule has a page in the [diagnostic index][diagnostics]. IDE code fixes can apply
the [common fixes][analyzer-usability]; they never switch the checks off.

## Documentation

- [Getting started][getting-started]: installation, examples for every type, cleanup.
- [Allocator selection][allocator-selection]: when to stay managed and which NAM type fits.
- [Memory budgets][memory-budgets]: capping native memory and admitting work.
- [Diagnostics and statistics][statistics]: what NAM measures and how to read it.

## Building from source

Build with .NET SDK 10.0.403 and C# 13. `global.json` pins that SDK with roll-forward disabled.

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
