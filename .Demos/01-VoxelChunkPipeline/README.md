# Voxel chunk pipeline

This demo compares expert safe C# with expert safe C# using Native Allocation
Management in a constrained voxel pipeline. It is intended to answer a useful
question: after the managed implementation has applied pooling, exact sizing,
prompt returns, and memory-aware backpressure, does explicit native ownership
still provide an advantage? An out-of-memory failure caused by retaining too
much pooled storage is not treated as the managed strategy.

The `SharedContract` project owns every workload, protocol, input, output,
telemetry, and report type. `SafeCSharp` and `NAM` contain only their execution
strategies. `Harness` owns the external compile and runtime gates, while
`Pressure/run-constrained.ps1` builds the fixed artifacts and invokes those
gates in the required order.

## The common pipeline

Both implementations generate the same ordered 32 by 32 by 32 chunks from the
same seed and runtime block registry. They classify the same sections, derive
the same opaque and transparent face records, construct the same transparent
masks, render the same vertices and indices, and prerender the same payload
descriptors and upload bytes. The upload layout includes each block type's
payload, alignment, stage mask, encoded vertices and indices, and a fixed GPU
command area. Alignment and command padding are zero-filled and verified.

Each chunk contributes its complete cell storage, face records, transparent
masks, vertices, indices, payload descriptors, and upload bytes to cumulative
logical allocator demand. A profile ends only after that demand reaches the
requested percentage of the 256 MiB cgroup cap. Thus, 1000 percent means about
2.5 GiB of real pipeline allocation requests through bounded storage. The
10000-percent profile means about 25 GiB of requests. These values do not
specify simultaneous resident storage, and they do not include synthetic
garbage.

The workload repeats one fixed 16-chunk heterogeneous cycle. The cycle contains
all eight chunk archetypes and every section representation kind. Each
50-percent step adds one complete cycle. Thus every profile has the same type
mix and logical work per byte. Later profiles change only cumulative turnover.

Each cycle uses the same worker assignment. Thus, a larger turnover profile
cannot create a new worker-local batch shape after warmup.

The separate exact verification creates canonical evidence in chunk order.
Typed vertices and indices are checked against independently derived values.
Retained descriptors and upload bytes are checked again at the GPU consumer
boundary. This check includes every encoded field and zero-filled byte.

A SHA-256 stream includes every logical typed value, descriptor, byte, length,
partition boundary, and chunk boundary. The hash reads each typed stage range
in canonical order. Safe C# and NAM must produce identical per-chunk evidence
and the same final evidence stream.

Measured profiles use the predeclared plan and terminal completion values.
They do not create per-chunk evidence or a canonical evidence hash.

## The corrected managed baseline

The managed implementation uses unsafe-disabled C#, `Span<T>`,
`ReadOnlySpan<T>`, exact logical slices, stack storage for small bounded data,
and worker-local `ArrayPool<T>` instances. Its size classes retain bounded
capacities for each typed output. This matches each array's true lifetime and
removes avoidable per-batch allocation after warmup. Value buffers are fully
overwritten in their logical ranges. The pools return these buffers without a
clear operation on unused bucket space.

The managed worker materializes the five exact GPU stage types in worker-local
pools. At the upload boundary, it writes these ranges into one persistent
mapped GPU buffer through `UnmanagedMemoryStream`. Unsafe code remains
disabled. This transfer is necessary because safe managed spans cannot
reference external native storage.

The requested in-flight horizon is twenty chunks. Under the 256 MiB profile,
the runtime plan counts the mapped sink, CLR storage, typed outputs, and pool
metadata. It selects the largest batch that fits the retained-memory limit.
All outputs remain live through the mapped transfer and consumer boundary. The
worker then returns each array to its bounded size class. No chunk, output, or
byte is dropped.

In simplified form, the managed flow is equivalent to the following code.

```text
read the effective managed-memory limit
derive the bounded admission depth
while cumulative demand remains
    build one bounded batch
    rent each exact typed output from a worker-local pool
    render and prerender every unique output
    transfer the five GPU stage ranges into mapped storage
    complete the mapped handoff
    return every buffer at its true consumer boundary
```

This is deliberately not a baseline that waits for `OutOfMemoryException`.
Every failure is still preserved in the raw report, but proactive admission is
the expected path.

## The NAM implementation

The NAM process keeps one persistent execution worker and its owners alive
across warmup and every profile. One heterogeneous arena uses a persistent
mapped GPU buffer as its external backing. The runtime plan selects the largest
batch that fits the native retained-memory limit. The public requested horizon
remains twenty.

The arena retains source cells and face records. Section ranges, including
transparent masks, share one scoped phase in the same bounded backing. Final
output ranges use a second scoped phase.

Section state and per-material masks use one Y-fast traversal. Mask slots
follow stable runtime block-registry order. Safe C# uses the same fused
computation because direct comparison shows that it is faster for both paths.

Warmup establishes the complete physical capacity. Later batches recycle the
same native ranges without geometric growth.

The final phase creates `Vertex`, `int`, and `PayloadSlice` ranges in one
grouped operation. A persistent native payload table supplies the payload
fragments.

Each logical GPU stage references its payload, vertex, index, and zero-padding
ranges. NAM does not copy vertices or indices into duplicate stage records.

These native ranges share one generation boundary. Publication keeps every
output lease valid through the mapped handoff. The measured handoff transfers
ownership without entering or scanning the native ranges.

Exact verification uses one composite access for all related ranges. It reads
every logical stage byte and compares the result with the canonical hash.

`NativeLeaseOperations.Access` enters related same-owner views with one
failure-atomic composite operation. Scoped arena recycling uses recorded
watermarks and touched ranges rather than rescanning every allocation against
every segment. Generation release invalidates the complete batch together,
clears reusable ranges as required by NAM semantics, and retains only the
bounded backing for the next batch.

## Compilation is the first blocking gate

Runtime numbers are forbidden unless compilation passes first. The harness
alternates clean Release rebuilds of the Safe C# and NAM demo projects with
restore, dependency builds, compiler sharing, and build-server reuse disabled.
NAM consumes the already-built runtime and analyzer binaries during this gate,
which models a package consumer and avoids charging recursive source-project
evaluation to the analyzer.

The report stores every warmup and measured sample, command wall time, the
MSBuild `Csc` task duration, outcome, exit code, and bounded output tails. Both
the mean compiler-task ratio and the mean complete command wall-time ratio are
blocking. NAM must be no more than 1.10 times Safe C# on each ratio. A failed
compile, a missing timing, a timeout, or either ratio above 1.10 exits before
publish, image construction, or pressure measurement.

The gate can be run directly after building the harness.

```powershell
dotnet .Demos/01-VoxelChunkPipeline/Harness/bin/Release/net10.0/VoxelChunkPipeline.Harness.dll `
  --compile-gate `
  --repo E:\source\Supprocom\NativeAllocationManagement `
  --output artifacts\voxel-compilation.json `
  --warmup-pairs 1 `
  --pairs 5 `
  --compile-timeout-ms 30000
```

## Constrained execution

The runtime harness starts one Safe C# container and one NAM container for
each profile. They receive equal 256 MiB memory and swap limits. They also
receive swappiness zero, equal PID limits, the same CPU set, server GC, and
four GC heaps. Both use the same 90 percent GC heap hard limit. No CFS CPU
quota is applied. The harness runs each pair sequentially. It alternates the
first implementation across samples.

The child sends `ProcessingReady` after all workers reach their start gate.
The child then waits. The host records the start time and sends
`BeginProcessing`. Only then can the workers start pipeline work. The host
records the stop time when it receives `ProcessingCompleted`. This handshake
prevents pipe buffering from hiding processing time.

The predeclared profiles are 50, 100, 200, 500, 1000, and 10000 percent of the
binary cgroup cap. Fifty and one hundred percent are control profiles.

Each profile starts a new Safe container and a new NAM container. Each pair
receives the same fixed 1000-percent warmup before measurement.

The 1000-percent warmup reaches all allocation shapes and stable capacities.
The 10000-percent profile adds turnover but does not add an allocation shape.

The harness destroys both containers before it starts the next profile. Thus,
allocator, pool, CLR, JIT, thread, and cgroup state cannot pass between
profiles.

Container startup and warmup are outside the profile timer. The artifact
records their elapsed time and container names.

The final gate fails if a container name occurs in more than one profile.

Every implementation and profile receives an explicit
outcome such as `Completed`, `DeadlineExceeded`, `OutOfMemory`,
`IncorrectOutput`, `Crash`, or `HarnessFailure`. A failed outcome remains in
the raw artifact with its last completed chunk, logical bytes, pipeline stage,
exception or exit information, and available GC and cgroup state.

Elapsed processing time is measured by the host between child boundary
messages. The child does not run a benchmark stopwatch and does not call
`GetStatistics()` or scan owners during processing. The child sends one
completion boundary after it completes the mapped handoff. Owner and runtime
snapshots occur outside the processing boundary. Docker CPU and resident
samples are polled externally. The harness captures cgroup state at profile
boundaries.

Measured child results contain no per-chunk evidence. The outer worker checks
completed chunks, logical bytes, source bytes, stage, and chunk position
against each predeclared worker plan.

Both controls must complete with exact parity. Their mean paired NAM speedup
must be at least 1.50. The 200-percent profile requires at least 1.75.
The 500-percent profile uses a progressive minimum. The 1000-percent and
10000-percent profiles require at least 2.00. Each lower 95-percent confidence
bound must remain above 1.00.

The matrix also requires NAM mean milliseconds per realized GiB to decrease
at each later profile. A flat or increasing NAM cost fails the scaling gate.

Measured profiles do not scan output bytes for benchmark evidence. The Safe
path completes its checked mapped transfer. The NAM path completes a native
scatter handoff without copying its typed ranges. A separate maximum-demand
run reads and verifies every typed value and output byte.

A pressure profile also requires a Safe C# Gen2 collection, managed allocation
turnover above the effective heap limit, and resident evidence from either an
80 percent cgroup peak or a 90 percent GC high-memory load. NAM completing
inside six seconds while Safe C# times out, runs out of memory, crashes, or
produces incorrect output is recorded as a decisive result without inventing a
finite speedup for the censored Safe duration. A NAM failure always fails the
profile.

## Running the complete experiment

Restore and build the solution once before the constrained run.

```powershell
dotnet restore Supprocom.NativeAllocationManagement.slnx
dotnet build Supprocom.NativeAllocationManagement.slnx -c Release --no-restore
```

The runner performs the blocking five-pair compilation gate, publishes the two
Linux children, builds the pinned runtime image, and then runs the fixed matrix.
Every subprocess has a hard timeout, and the complete script has a 180-second
bound.

```powershell
& .Demos/01-VoxelChunkPipeline/Pressure/run-constrained.ps1 `
  -RepoRoot E:\source\Supprocom\NativeAllocationManagement `
  -CompilationOutputPath artifacts\voxel-compilation-final.json `
  -OutputPath artifacts\voxel-pressure-final.json `
  -Enforce
```

The command is local verification. It does not change the package version and
does not publish a NuGet or GitHub package.
