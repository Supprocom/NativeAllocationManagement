# Measure the analyzer's compile cost

`Supprocom.NativeAllocationManagement.CompilerHost` is a console program in this repository (not in the NuGet package). It runs your SDK's C# compiler (`csc.dll`) with your project's exact compiler arguments and prints what the run cost. Run it twice, once with the NAM analyzer in the arguments and once without, to see what the analyzer adds.

## 1. Export the compiler arguments

Build your project once with `ProvideCommandLineArgs` and write `CscCommandLineArgs` to a file, one argument per line:

```xml
<Target Name="ExportCscArgs" AfterTargets="CoreCompile">
  <WriteLinesToFile File="$(MSBuildProjectDirectory)/csc.rsp" Lines="@(CscCommandLineArgs)" Overwrite="true" />
</Target>
```

```text
dotnet build -c Release --no-incremental -p:ProvideCommandLineArgs=true -p:LangVersion=13
```

For the comparison, delete the `/analyzer:` line that ends in `Supprocom.NativeAllocationManagement.Analyzers.dll` from a copy of the file.

## 2. Run it

```text
dotnet build Supprocom.NativeAllocationManagement.CompilerHost -c Release
dotnet Supprocom.NativeAllocationManagement.CompilerHost/bin/Release/net10.0/Supprocom.NativeAllocationManagement.CompilerHost.dll \
  --compiler-cost-worker \
  --compiler <sdk>/Roslyn/bincore/csc.dll \
  --response csc.rsp
```

- Start it with the `dotnet` of the SDK you are measuring, in the project's working directory.
- Rebuild with `--no-incremental` each time you export; an up-to-date build runs the target with no arguments and overwrites `csc.rsp` with an empty file.
- `-p:LangVersion=13` is only needed for the cancellation check below.
- The host passes the arguments to the compiler unchanged. It does not reference Roslyn itself, so the compiler can find the analyzers.

## 3. Read the output

The compiler prints its own messages, then one JSON line:

| Field | Meaning |
|---|---|
| `ExitCode` | the compiler's exit code; non-zero means the run is not a measurement. An analyzer-discovery warning in the compiler output also makes the run invalid |
| `AllocatedBytes` | managed bytes allocated while the compiler loaded and ran (the whole process, not only NAM) |
| `Gen0Collections`, `Gen1Collections`, `Gen2Collections` | GCs during the run |
| `CpuMilliseconds`, `WallMilliseconds` | process CPU time and elapsed time |
| `TieredCompilation`, `TieredPGO` | the `DOTNET_` settings of the run (`unset` if absent) |
| `Compiler`, `ResponseFile` | path, length and SHA-256 of the two inputs |

Compare runs that differ only in the analyzer argument, with the same `TieredCompilation` and `TieredPGO` values. Repeat for a clean build, an unchanged build and a one-file edit. Starting the host and hashing the inputs are outside the measurement.

## Analyzer cancellation check

```text
dotnet run --project Supprocom.NativeAllocationManagement.Performance -c Release -- --analyzer-cancellation-worker --response csc.rsp --project-directory <project-dir>
```

This runs the NAM analyzers on your compilation, cancels as soon as one starts analyzing a NAM operation, then runs them again without cancellation. Exit code 0 and `"Cancelled": true`, `"AnalysisWasInFlight": true` and an empty `FollowUpDiagnosticIds` mean cancellation worked and the second run was clean. The step-1 build must use a `LangVersion` that Roslyn 4.14 understands (`-p:LangVersion=13`, as shown). Your sources need at least one method that uses a NAM type.
