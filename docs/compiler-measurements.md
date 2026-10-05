# Compiler measurement boundaries

`Supprocom.NativeAllocationManagement.CompilerHost` is a nonshipping executable
for measuring the actual pinned SDK compiler. It has no Roslyn reference: loading
the performance runner's Roslyn version before the SDK compiler can prevent that
compiler from discovering the bundled NAM analyzers.

Run the built host through the pinned `dotnet` executable:

```text
dotnet Supprocom.NativeAllocationManagement.CompilerHost.dll --compiler-cost-worker --compiler <sdk>/Roslyn/bincore/csc.dll --response <exported-compiler-arguments>
```

The input is the UTF-8, one-argument-per-line stream exported from MSBuild's
`CscCommandLineArgs`, not an arbitrary shell command. The host passes those
arguments directly to the compiler, preserving top-level `/noconfig` semantics.
Use the original working directory and retain the input, its length/hash, the
compiler identity, complete streams, exit code and emitted binary identities.
An analyzer-discovery warning, compiler failure or missing observation is not a
successful performance measurement.

The JSON observation measures whole-process managed allocation, CPU, elapsed
time and collection deltas while loading and running that compiler, including
argument-file reading and assembly-load-context setup. It is not NAM-only cost;
the already-started host and reporting/hashing after compilation are outside the
measurement. Compare paired analyzer-on/off runs that differ only in the NAM
analyzer argument. Retain both default tiered-compilation/PGO settings and the
separate controlled settings. Include cold consumer builds, actual unchanged
builds and one-source-edit rebuilds; a synthetic analysis cache is insufficient.

The separate performance runner's `--analyzer-cancellation-worker` executes the
NAM analyzers on a real consumer compilation. A private observer cancels when an
authentic native operation block is entered, then requires cancellation and a
complete uncancelled follow-up. This observes the shared driver while NAM analysis
is active; it does not claim interruption inside a NAM callback or cancellation
of an SDK compiler process. Missing native operations cannot yield a passing
cancellation observation.
