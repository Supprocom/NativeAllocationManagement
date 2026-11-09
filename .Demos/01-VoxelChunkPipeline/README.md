# Voxel chunk pipeline

Two implementations of one voxel chunk pipeline: expert safe C# (`Span<T>`, stack storage, worker-local `ArrayPool<T>`, no `unsafe`) and the same design with Native Allocation Management (NAM). Both build, render, prerender and upload the same 32 by 32 by 32 chunks, and produce equal typed outputs and equal upload bytes.

| Project | Contains |
|---|---|
| `SharedContract` | Input, output, workload and result types |
| `SafeCSharp` | The safe C# memory strategy |
| `NAM` | The NAM memory strategy |
| `Harness` | The container matrix driver |
| `InProcessBenchmark` | The one-command comparison below |

## Compare on one machine

Needs the .NET 10 SDK, no container. Run from the repository root:

```bash
dotnet run --project .Demos/01-VoxelChunkPipeline/InProcessBenchmark/Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.InProcessBenchmark.csproj -c Release -- compare
```

Output (`--rounds 1` shown; numbers depend on your machine):

```text
1 rounds, 9 timed requests, 256 MiB demand, 4 warmups; one process per mode per round, order alternates
Median per-request process CPU time in ms.
round  order      NAM     Safe   Safe/NAM
    1  NAM,Safe     51.5    111.5     2.165
median of NAM medians:  51.5 ms
median of Safe medians: 111.5 ms
Safe/NAM (medians):     2.165
Safe/NAM per-round range: 2.165 - 2.165
```

How to read it:

- Each mode runs in its own process, so neither inherits the other's heap. Per round, NAM and Safe each run once; the order alternates (NAM first, then Safe first).
- Each process runs 4 warmup requests, then times 9 requests of 256 MiB demand.
- The values are the median per-request process CPU time in milliseconds.
- `Safe/NAM` is Safe time divided by NAM time. Above 1, NAM used less CPU.
- The per-round range shows the spread between rounds.

Options (defaults shown):

```bash
dotnet run --project .Demos/01-VoxelChunkPipeline/InProcessBenchmark/Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.InProcessBenchmark.csproj -c Release -- compare --rounds 5 --requests 9 --demand-mib 256
```

## Check that both produce the same output

```bash
dotnet run --project .Demos/01-VoxelChunkPipeline/InProcessBenchmark/Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.InProcessBenchmark.csproj -c Release -- verify nam 128 4E414D
dotnet run --project .Demos/01-VoxelChunkPipeline/InProcessBenchmark/Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.InProcessBenchmark.csproj -c Release -- verify safe 128 4E414D
```

`verify <nam|safe> <demandMiB> <hexSeed>...` reads every output value and byte of 32 chunks for each seed. Both lines must be identical except for the leading mode:

```text
nam seed 4E414D correct True chunks 32 canonical 44A23E69DCDDC91471F1D5EF62FDACD34DA2094D543C08AAB7F1EA13D78F1C3D chunk-evidence 886614B8918E017F14F54443DF542B2485DB439CCE1313FF6277F60DF60F8B51
safe seed 4E414D correct True chunks 32 canonical 44A23E69DCDDC91471F1D5EF62FDACD34DA2094D543C08AAB7F1EA13D78F1C3D chunk-evidence 886614B8918E017F14F54443DF542B2485DB439CCE1313FF6277F60DF60F8B51
```

`canonical` and `chunk-evidence` are SHA-256 hashes of the output; equal hashes mean equal output.

## Memory-constrained matrix

Measures both implementations in Docker containers with the same 256 MiB memory cap, no swap, and the same CPU, PID and GC settings. Each profile sets the total memory demand as a percentage of the cap: 50, 100, 200, 500, 1000 and 10000. Needs Windows, Docker and PowerShell (`pwsh`).

Build, then prepare the worker image. Setup runs outside the measured matrix and writes the compiler-time artifact:

```powershell
dotnet restore Supprocom.NativeAllocationManagement.slnx
dotnet build Supprocom.NativeAllocationManagement.slnx -c Release --no-restore

$commit = (git rev-parse HEAD).Trim()
$image = "nam-voxel-pressure:$($commit.Substring(0, 12))"

& .Demos/01-VoxelChunkPipeline/Pressure/run-constrained.ps1 `
  -RepoRoot E:\source\Supprocom\NativeAllocationManagement `
  -Image $image `
  -CompilationOutputPath artifacts\voxel-compilation-final.json `
  -SetupOnly
```

Run the matrix from the repository root:

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

How to read it:

- Each profile runs `-SamplesPerProfile` paired samples (default 6, must be even). Each pair uses fresh Safe and NAM containers, and each implementation runs first equally often.
- `-OutputPath` receives a JSON report: per-sample correctness, timing, GC, cgroup, CPU and native-memory data. A failed sample stays in the report.
- Before the runtime matrix, a compiler gate requires NAM's compile time and complete build time to each stay within 1.10 times Safe's.
- With `-Enforce`, the command exits nonzero when any gate fails and writes a terminal report for the failed profile.
- The wrapper stops a run with no activity for 120 seconds and keeps the latest checkpoint.
- The command writes local files only.
