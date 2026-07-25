# Voxel chunk pipeline

This demo compares expert safe C# with safe C# using Native Allocation Management
(NAM) for a deterministic, memory-pressure-heavy voxel build, prerender,
mesh-packing, and upload-staging pipeline. The tree is split into
`SharedContract`, `SafeCSharp`, `NAM`, and `Harness`. The contract owns the
machine-readable workload and parity rules, the safe project is the managed
baseline, the NAM project is the direct native-backed implementation, and the
harness runs isolated children and the paired statistical gate.

The workload follows the source-backed VoxelEngine shape. Runtime block metadata
is setup-time value data with custom `ushort` identifiers beginning at 256, while
measured cells carry only those identifiers. The registry varies payload size,
alignment, opacity, face-tile stage mask, transparent-palette cardinality, and
frequency. Every measured run exercises Empty, Uniform, Expanded, Packed, and
MultiPacked section representations, including dominant and residual transparent
paths. The source references are `MVGE-INF/Models/Terrain/BlockType.cs:9-23`,
`MVGE-INF/Loaders/TerrainLoader.cs:18-29,215-289,384-387`,
`MVGE-INF/Models/Generation/Section.cs:12-80`, and
`MVGE-GEN/Terrain/ChunkGeneration.cs:41-108,236-567` at VoxelEngine commit
`4969faeb07af77f5cfaad21b06f680490014aac3`.

Both implementations retain the expert safe techniques appropriate for bounded
voxel work: worker-local reuse, `ArrayPool<T>` where it remains the right fit,
pre-sized value storage, stack-based section counters and masks, and zero
per-cell object creation. The safe baseline clears returned arrays so its reuse
boundary has the same zeroing requirement as NAM's scoped recycle. The NAM path
owns cell data, opaque and transparent face records, vertices, and indices in
typed `NativePool<T>` leases. It uses a worker-local `NativeArena` for the
variable transparent masks, slice descriptors, and exact upload-byte staging
that have heterogeneous runtime-defined shapes. `NativeLeaseOperations.Access`
exposes direct bounded views to useful processing callbacks; NAM does not copy a
managed mirror into native storage and back. Small bounded masks remain
`stackalloc` in both implementations.

The stage boundaries are explicit. Cell coordinates, density, material IDs,
face masks, section classification, and transparent masks end before packing.
Opaque and transparent face output ends after vertex, index, and upload packing.
Each stage uses a lexical scoped lease followed by one analyzer-proven
`RecycleScoped` completion, so a stale scoped handle cannot be used after the
boundary. The completed opaque and transparent vertices, indices, descriptors,
and upload bytes are materialized in both paths; the harness compares a small
fixture of exact elements and bytes in addition to the full-work digest and
counters. Worker-local owners remain alive across measured chunks, and terminal
owner disposal requires the physical NAM native-byte delta to return to zero.

Build the four C# projects with these commands.

```text
dotnet restore .Demos/01-VoxelChunkPipeline/Harness/Harness.csproj
dotnet build .Demos/01-VoxelChunkPipeline/SafeCSharp/SafeCSharp.csproj -c Release --no-restore
dotnet build .Demos/01-VoxelChunkPipeline/NAM/NAM.csproj -c Release --no-restore
dotnet build .Demos/01-VoxelChunkPipeline/Harness/Harness.csproj -c Release --no-restore
```

Run correctness parity with
`dotnet .Demos/01-VoxelChunkPipeline/Harness/bin/Release/net10.0/VoxelChunkPipeline.Harness.dll
--correctness-only`. The controlled benchmark starts one isolated child per
implementation for each paired sample. Each child warms its worker-local state
inside the same process, resets logical counters, and then measures repeated
chunks. The harness alternates implementation order, requires at least thirty
paired samples, stores every raw sample, reports latency standard deviations and
p50/p95/p99 values, and calculates a paired Student-t 95% interval over
Safe/NAM speedups.

The optional `--enforce` switch returns failure unless all correctness counters
match, NAM mean throughput is at least five percent higher, the paired confidence
interval is entirely above 1.00, cold managed allocation and backing creation are
materially lower, and final physical native bytes are zero. Each child reports
warm steady-state allocation separately from cold allocation and backing creation
during the in-process warmup; warm allocation is a separate diagnostic rather
than part of the cold-pressure result. A failed gate is reported honestly. The
benchmark does not shorten the safe workload or treat a logical capacity estimate
as physical native memory. The demo is local verification only and does not
publish or version-bump a NuGet or GitHub package.

The backing-byte scopes are explicit. SafeCSharp reports the sum of its concurrent
worker peak and cold backing values, while NAM reports process-global physical
native bytes from `NativeMemoryDiagnostics` and a separate process-global managed
backing value. The values are therefore compared at aggregate process scope rather
than comparing one safe worker with both NAM workers. The independent fixture is a
hand-authored complete expected byte range for both opaque and transparent output;
the ordinary workload fixture remains a small captured parity sample, while the
full workload digest covers every materialized output buffer.
