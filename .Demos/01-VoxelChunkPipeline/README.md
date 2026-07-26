# Voxel chunk pipeline

This demo compares expert safe C# with safe C# using Native Allocation
Management (NAM) for a deterministic, memory-pressure-heavy voxel build,
prerender, mesh-packing, and upload-staging pipeline. The tree is split into
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
per-cell object creation. Every value-only managed array is fully overwritten in
its logical range, so the safe baseline returns it without clearing rounded
unused bucket capacity; the transparent-mask logical range is explicitly cleared
before use. The NAM path owns cell data, opaque and transparent face records,
vertices, and indices in typed `NativePool<T>` leases. It uses a worker-local
`NativeArena` for variable transparent masks, slice descriptors, and exact
upload-byte staging with heterogeneous runtime-defined shapes. `NativeLeaseOperations.Access`
exposes direct bounded views to useful processing callbacks; NAM does not copy a
managed mirror into native storage and back. Small bounded masks remain
`stackalloc` in both implementations.

The stage boundaries are explicit. Cell coordinates, density, material IDs, face
masks, section classification, and transparent masks are consumed before
packing. The managed baseline returns its cell array immediately before the
combined output pack. The NAM cell lease is ended and recycled as soon as face
derivation and mask construction finish, before the output pack begins. Face
records remain live through vertex, index, descriptor, and upload packing. Each
recyclable stage uses a lexical scoped lease followed by one analyzer-proven
`RecycleScoped` completion, so a stale scoped handle cannot be used after its
boundary. Both implementations write the same complete output layout during
correctness mode; the harness compares every fixture element and byte, while
timed pressure runs retain the same materialized work and compare a canonical
SHA-256 hash over every output element, descriptor, byte, length, and counter.
Worker-local owners remain alive across measured chunks, and terminal owner
disposal requires the process to return to its zero native baseline.

The raw benchmark records generation and face derivation separately from
transparent-mask, opaque-packing, and transparent-packing time, and records
coordinate, face, mask, and packing recycle boundaries with their clearing work.
Typed-pool slab reuse is reported separately from arena reclaimed-range reuse.
Only the latter proves that `RecycleScoped` made an arena byte range available
and that a later scoped acquisition overlapped that reclaimed range; the demo
never treats a second allocation in an untouched bump segment as recycling
evidence.

Build the four C# projects with these commands.

```text
dotnet restore .Demos/01-VoxelChunkPipeline/Harness/Harness.csproj
dotnet build .Demos/01-VoxelChunkPipeline/SharedContract/SharedContract.csproj -c Release --no-restore
dotnet build .Demos/01-VoxelChunkPipeline/SafeCSharp/SafeCSharp.csproj -c Release --no-restore
dotnet build .Demos/01-VoxelChunkPipeline/NAM/NAM.csproj -c Release --no-restore
dotnet build .Demos/01-VoxelChunkPipeline/Harness/Harness.csproj -c Release --no-restore
```

Run correctness parity with
`dotnet .Demos/01-VoxelChunkPipeline/Harness/bin/Release/net10.0/VoxelChunkPipeline.Harness.dll
--correctness-only`. Correctness mode serializes the complete canonical input
cell sequence and the complete independent opaque and transparent output fixture
outside the measured interval. The harness compares both implementation inputs,
every fixture element and byte, full-work SHA-256 hashes, lengths, and counters.

The controlled benchmark starts one isolated child per implementation for each
paired sample. Each child warms its worker-local state inside the same process,
resets logical counters, and then measures repeated chunks. The harness
alternates implementation order, requires at least thirty paired samples, stores
every raw sample, reports latency standard deviations and p50/p95/p99 values, and
calculates a paired Student-t 95% interval over Safe/NAM speedups. Cold
end-to-end time includes owner construction and the same warmup; steady-state
time begins at the shared post-warmup start gate.

The optional `--enforce` switch returns failure unless all correctness counters
match, NAM mean throughput is at least five percent higher, the paired
confidence interval is entirely above 1.00, cold managed allocation and backing
creation are materially lower, and final physical native bytes are zero. Each
child reports warm steady-state allocation separately from cold allocation and
backing creation. A failed gate is reported honestly. The benchmark does not
shorten the safe workload or treat a logical capacity estimate as physical native
memory. The demo is local verification only and does not publish or version-bump
a NuGet or GitHub package.

The backing-byte scopes are explicit. SafeCSharp reports the sum of its concurrent
worker peak and cold backing values, while NAM reports the absolute process-global
physical native high-water mark from `NativeMemoryDiagnostics` and a separate
process-global managed backing value. The native child asserts zero outstanding
bytes before setup and after terminal disposal. Coordinate, face, and packing
stage fields are per-worker stage-budget diagnostics rather than a cross-allocator
physical-memory comparison: Safe values include actual `ArrayPool<T>` bucket
sizes, while NAM values are exact native lease capacities.

The shared input contract carries the registry, workload options, every measured
chunk, the observed pre-mutation cell count, and a SHA-256 hash over the canonical
little-endian values. Correctness mode also serializes the complete observed cell
sequence. Pressure runs carry the same observed count and hash without retaining
the full cell array after timing. The shared output contract carries one
`FaceRecord` layout, vertices, indices, `PayloadSlice` descriptors, upload bytes,
lengths, and counters. Its SHA-256 stream includes every canonical little-endian
element, byte, length, and stream boundary, so equal aggregate counts cannot hide
a changed element or byte. NAM also reports each owner's requested bytes, peak
physical segment capacity, idle retained capacity, retired bytes, growth slack
observed while a request was live, trim work, and fresh-segment regrowth. Idle
retained capacity is not mislabeled as geometric-growth slack.

The constrained experiment is opt-in and uses
`Pressure/run-constrained.ps1`. It builds a pinned Linux runtime image before
measuring, uses identical Docker memory and swap limits with CPU limits for both
children, and predeclares an unconstrained control plus 1 GiB, 768 MiB, 640 MiB,
and 512 MiB profiles. Four worker-local pipelines overlap variable chunks and
iterations, so the cap covers legitimate in-flight stage pressure rather than a
single sequential chunk. Each child reports cold end-to-end time including owner
construction and steady-state time after the same in-process warmup. The raw
report records image identity, real start and finish times, completion,
capacity/OOM/timeout status, cgroup limit and peak, GC memory availability and
collection deltas, pause duration, heap/LOH/fragmentation facts, native
peak/final bytes, every paired sample, and the Student-t gate. A constrained
throughput result is valid only when SafeCSharp demonstrably sees collection
pressure and reaches the declared cap regime; an OOM, timeout, or invalid
no-pressure run is preserved as its capacity or validity result rather than
substituted with another profile.
