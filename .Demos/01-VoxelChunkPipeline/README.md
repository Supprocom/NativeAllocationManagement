# Voxel chunk pipeline

This demo compares expert safe C# with the same application design using
Native Allocation Management.

Both implementations build, render, prerender, and upload the same voxel
chunks. They produce equal typed outputs and equal upload bytes.

The comparison asks one question. Does safe explicit native ownership improve
performance after the managed implementation applies its available memory
optimizations?

## Current estimates

The control profiles estimate that NAM is 50 to 85 percent faster in a
non-memory-constrained environment.

The constrained profiles estimate that NAM is 90 to 130 percent faster in a
very memory-constrained environment. Selected higher-turnover runs exceed 150
percent.

| Environment | Estimated improvement |
|---|---:|
| Non-memory-constrained controls | Approximately 50–85% |
| Very memory-constrained profiles | Approximately 90–130% |

These values are workload and system estimates. Use the included command to
measure the result on the applicable target system.

The matrix records constrained-memory qualification as an informational result.
The result confirms the equal binary cap, no swap, and cumulative demand.
It does not require a garbage collection or a resident-memory threshold.

## Equal work

`SharedContract` defines the input, output, workload, protocol, and result
types. The Safe and NAM projects contain only their memory strategies.

Both implementations process the same ordered 32 by 32 by 32 chunks. They
produce the same sections, faces, masks, vertices, indices, descriptors, and
upload bytes.

The workload contains eight chunk archetypes and all section representation
types. One fixed 16-chunk cycle keeps the type mix and work per byte equal.

The exact verifier reads every material output value and byte. Safe and NAM
must produce equal per-chunk evidence and the same SHA-256 evidence stream.

## Safe C# strategy

The Safe project disables unsafe code. It uses `Span<T>`, `ReadOnlySpan<T>`,
bounded stack storage, and worker-local `ArrayPool<T>` size classes.

It uses exact logical slices and prompt pool returns. Proactive memory
admission limits each live batch before the mapped GPU transfer.

The final transfer copies the five typed GPU stage ranges into persistent
mapped storage. Safe managed spans cannot directly own that external storage.

## NAM strategy

The NAM project uses persistent worker-local owners and one heterogeneous
arena. The arena uses a mapped GPU buffer as external backing.

Cells, faces, section ranges, masks, and final outputs use bounded native
ranges. Scoped recycling releases each phase with one generation operation.

Final GPU stages reference their native payload, vertex, index, and padding
ranges. The mapped handoff does not copy these ranges into duplicate stage
records.

Four 1000-percent warmup passes establish allocation shapes and stable
capacities. Each later canonical cycle uses the same worker assignment.

Thus, the 10000-percent profile adds storage turnover without adding a new
resident allocation shape.

## Measurement controls

The compiler gate runs before the runtime matrix. The NAM compiler time and
complete build time must each remain within 1.10 times the Safe result.

The gate uses one warmup pair and six measured pairs. Each implementation has
three first positions and three second positions.

Both warmup builds must complete with compiler timing and exit code zero. A
warmup failure writes the artifact and stops before measured builds.

Each child build disables tiered compilation and dynamic tiered PGO. The
artifact records these settings for every warmup and measured build.

The artifact records compiler standard deviation. It also records the compiler
mean for each implementation in each command position.

Runtime containers receive equal 256 MiB memory limits, swap policy, CPU
access, PID limits, GC mode, and GC heap limits.

Both containers disable tiered compilation and dynamic tiered PGO. Each worker
records these settings in its startup runtime data.

The default profiles are 50, 100, 200, 500, 1000, and 10000 percent of the
binary cgroup cap.

Requested subsets must keep this canonical order. The harness rejects
reversed, rotated, duplicate, or unknown profile lists.

Each profile uses six paired samples by default. The harness rejects odd
sample counts because each implementation must run first equally often.

Each paired sample uses new Safe and NAM containers. Startup and four fixed
1000-percent warmup passes occur outside measured processing.

Each worker then runs six selected-profile preparation requests. The timed
request always has request ordinal 11.

Safe and NAM use equal preparation counts. Elapsed-time noise does not change
the count or accept a different runtime state.

The output path contains an atomic progress checkpoint until the run
completes. The harness writes a checkpoint after each preparation series.

The harness writes another checkpoint after each isolated pair. This
checkpoint includes completed profiles, current pairs, initialization data,
commands, binary identities, and completed worker lifecycles.

Each preparation checkpoint includes all attempts and the current worker
state. The final report atomically replaces the latest checkpoint.

In enforce mode, the harness evaluates each completed profile immediately. A
failed gate writes a terminal artifact before another profile can start.

The terminal artifact records exact gates, identities, commands, lifecycles,
cleanup state, and the preserved profile state.

The worker resets all logical state after each request. The harness removes
both sample containers before it starts the next pair.

The child waits at `ProcessingReady`. The host starts its timer before it sends
`BeginProcessing`.

The host stops its timer at `ProcessingCompleted`. The child does not run a
benchmark timer or scan allocator statistics during processing.

Untimed warmup and exact verification use a separate 60-second host timeout.
Each completed warmup and preparation request updates a host activity file.

Each result records completion, deadline, correctness, process, GC, cgroup,
CPU, and native-memory data. A failed result remains in the artifact.

## Run the matrix

Restore and build the solution first.

```powershell
dotnet restore Supprocom.NativeAllocationManagement.slnx
dotnet build Supprocom.NativeAllocationManagement.slnx -c Release --no-restore
```

Prepare the worker image first. This setup command runs outside the measured
matrix and writes the compiler artifact.

```powershell
$commit = (git rev-parse HEAD).Trim()
$image = "nam-voxel-pressure:$($commit.Substring(0, 12))"

& .Demos/01-VoxelChunkPipeline/Pressure/run-constrained.ps1 `
  -RepoRoot E:\source\Supprocom\NativeAllocationManagement `
  -Image $image `
  -CompilationOutputPath artifacts\voxel-compilation-final.json `
  -SetupOnly
```

Run the fixed matrix from the repository root after setup.

```powershell
$commit = (git rev-parse HEAD).Trim()
$image = "nam-voxel-pressure:$($commit.Substring(0, 12))"

& .Demos/01-VoxelChunkPipeline/Pressure/run-constrained.ps1 `
  -RepoRoot E:\source\Supprocom\NativeAllocationManagement `
  -Image $image `
  -CompilationOutputPath artifacts\voxel-compilation-final.json `
  -OutputPath artifacts\voxel-pressure-final.json `
  -SamplesPerProfile 6 `
  -SkipBuild `
  -SkipImageBuild `
  -Enforce
```

The wrapper stops a harness that has no activity for 120 seconds. This limit
exceeds the longest internal operation limit.

The wrapper derives a separate absolute fail-safe from the selected profiles,
sample count, fixed warmups, fixed preparations, and operation limits.

A smaller supplied fail-safe is invalid. A timeout keeps the latest checkpoint
and writes its path, SHA-256, standard output, and standard error.

The matrix command returns a nonzero exit code when any gate fails.

The command creates local evidence only. It does not change or publish the
package.
