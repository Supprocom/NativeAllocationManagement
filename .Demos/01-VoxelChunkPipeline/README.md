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
very memory-constrained environment. Selected higher-turnover runs exceed 110 percent.

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
  -SamplesPerProfile 11 `
  -SkipBuild `
  -SkipImageBuild `
  -Enforce
```

The setup and matrix commands use hard timeouts for compilation and profile
execution. The matrix command returns a nonzero exit code when any gate fails.

The command creates local evidence only. It does not change or publish the
package.
