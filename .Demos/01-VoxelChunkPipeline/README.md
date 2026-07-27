# Voxel chunk pipeline

This demo compares expert safe C# with the same application design using
Native Allocation Management.

Both implementations build, render, prerender, and upload the same voxel
chunks. They produce equal typed outputs and equal upload bytes.

The comparison asks one question. Does safe explicit native ownership improve
performance after the managed implementation applies its available memory
optimizations?

## Current estimates

The 50-percent control profile estimates that NAM is 53.82 percent faster in a
non-memory-constrained environment.

The 1000-percent profile estimates that NAM is 111.83 percent faster in a very
memory-constrained environment.

A separate 10000-percent pilot measured a 139.71 percent improvement. This
pilot processed 25.07 GiB of realized logical demand in each implementation.

| Profile | Safe-to-NAM ratio | Estimated improvement |
|---:|---:|---:|
| 50% | 1.5382x | 53.82% |
| 1000% | 2.1183x | 111.83% |
| 10000% pilot | 2.3971x | 139.71% |

These values are workload and system estimates. Use the included command to
measure the result on the applicable target system.

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

The 1000-percent warmup establishes all allocation shapes and stable
capacities. Each later canonical cycle uses the same worker assignment.

Thus, the 10000-percent profile adds storage turnover without adding a new
resident allocation shape.

## Measurement controls

The compiler gate runs before the runtime matrix. The NAM compiler time and
complete build time must each remain within 1.10 times the Safe result.

Runtime containers receive equal 256 MiB memory limits, swap policy, CPU
access, PID limits, GC mode, and GC heap limits.

The default profiles are 50, 100, 200, 500, 1000, and 10000 percent of the
binary cgroup cap.

The 10000-percent entry is one complete stress sample. The smaller profiles
use the configured paired sample count and confidence calculation.

Each profile uses new Safe and NAM containers. Container startup and the fixed
1000-percent warmup occur outside measured processing.

The child waits at `ProcessingReady`. The host starts its timer before it sends
`BeginProcessing`.

The host stops its timer at `ProcessingCompleted`. The child does not run a
benchmark timer or scan allocator statistics during processing.

Untimed warmup and exact verification use a separate 60-second host timeout.

Each result records completion, deadline, correctness, process, GC, cgroup,
CPU, and native-memory data. A failed result remains in the artifact.

## Run the matrix

Restore and build the solution first.

```powershell
dotnet restore Supprocom.NativeAllocationManagement.slnx
dotnet build Supprocom.NativeAllocationManagement.slnx -c Release --no-restore
```

Run the fixed matrix from the repository root.

```powershell
& .Demos/01-VoxelChunkPipeline/Pressure/run-constrained.ps1 `
  -RepoRoot E:\source\Supprocom\NativeAllocationManagement `
  -CompilationOutputPath artifacts\voxel-compilation-final.json `
  -OutputPath artifacts\voxel-pressure-final.json `
  -SamplesPerProfile 11 `
  -Enforce
```

The script uses hard timeouts for compilation, profile execution, and the
complete command.

The command creates local evidence only. It does not change or publish the
package.
