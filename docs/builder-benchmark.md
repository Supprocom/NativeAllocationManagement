# Builder benchmarks

Two benchmarks live in `Supprocom.NativeAllocationManagement.Performance`. Run them in Release from the repository root.

## Builder against managed output

It generates two `uint` sequences (an opaque and a transparent packed-word stream), hands both through a one-slot bounded channel and computes the same byte checksum on the receiving side. Each side is run in its own process, alternating which goes first.

```text
dotnet run --project Supprocom.NativeAllocationManagement.Performance -c Release -- --native-builder --samples 10 --prelease 1024 --output native-builder.json
```

| Option | Default | Meaning |
|---|---|---|
| `--elements` | 262144 | total elements across both sequences (75% opaque, 25% transparent) |
| `--prelease` | 1024 | `NativeBuilder<T>` `preLease`, and the initial capacity of the managed `List<uint>`; `NativeBuilderBudgetedDirect` ignores it |
| `--batch-size` | 256 | elements per generation chunk, 1 to 1024 |
| `--iterations` | 128 | measured iterations; one iteration builds both sequences, hands them over and consumes them |
| `--warmup` | 16 | warm-up batches |
| `--samples` | 10 | paired runs; must be even |
| `--seed` | 29123 | seed of the generated values |
| `--managed-baseline` | `ManagedExactArray` | the managed side (below) |
| `--native-baseline` | `NativeBuilderBudgeted` | the native side (below) |
| `--native-budget-bytes` | `Int64.MaxValue` | byte ceiling of the budget used by budgeted native sides |
| `--output` | none | also write the report to this file |

An option needs a value and may appear once; a missing or repeated value is an error.

| Managed baseline | What it does |
|---|---|
| `ManagedExactArray` | writes each output into an exact-size array and reads its bytes through spans (the strongest managed comparison for known sizes) |
| `ManagedListPrefix` | grows a `List<uint>` and hands its live prefix on, with no `ToArray` or copy |
| `ManagedList` | `List<uint>`, `ToArray`, then a byte-array copy |

| Native baseline | What it does |
|---|---|
| `NativeBuilderBudgeted` | `NativeBuilder<uint>` growing inside a `NativeMemoryBudget` |
| `NativeBuilder` | the same builder without a budget |
| `NativeBuilderBudgetedDirect` | known size: each output is created with its exact capacity and written once through `Write` |

Use the growing baselines for output of unknown size. A growable builder is not automatically the right owner for known-size output, so compare `NativeBuilderBudgetedDirect` with `ManagedExactArray` for that case.

### Read the report

The process exits 0 when every pair produced identical output and the order was balanced; 3 otherwise.

| Field | Meaning |
|---|---|
| `ExactParity`, `BalancedOrder` | both must be `true` for the numbers to mean anything |
| `ManagedMeanMilliseconds`, `NativeMeanMilliseconds` | mean time of the inner producer/consumer loop (setup and warm-up excluded). For the complete measured phase use `LifecycleEvidence.MeasuredBatch.ElapsedMilliseconds` |
| `MeanPairedSpeedup` | managed time divided by native time; above 1 means native was faster |
| `PairedSpeedupConfidenceLower95` | lower end of the 95% confidence interval of that ratio |
| `PerformanceAdvantage` | `true` only if the mean is above 1 and the interval's lower end is above 1; it is a time-only result |
| `ManagedMeanAllocatedBytes`, `NativeMeanAllocatedBytes` | GC allocation only; native bytes are not in it |
| `ManagedMeanPeakWorkingSetBytes`, `NativeMeanPeakWorkingSetBytes` | the OS's peak working set of the worker process since it started, including startup and warm-up |
| `Pairs[]` | per-run evidence for both sides |

To compare total allocation, add the native side's GC allocation
plus successful requested backing bytes, and compare that with the managed side's GC allocation. The backing bytes are `AcquiredBackingBytes` plus `ReplacementBackingBytes` in the budgeted native worker's `LifecycleEvidence.MeasuredBatch.After.Budget`, minus the same fields in `Before.Budget` (a replacement counts its whole new size, not the growth). `Budget` is `null` for managed and unbudgeted workers.

Things the numbers do not include: allocator bookkeeping and the real physical size of a reallocation. Pooled managed buffers and application-specific reuse need their own workload.

Each worker's `LifecycleEvidence` has four phases (`PreparationAndValidation`, `Warmup`, `MeasuredBatch`, `SeparatePhaseProbe`), each with `Before`, `After` and `ElapsedMilliseconds`. Do not add the phases together. Process CPU is shared by all threads of the process, and `ManagedHeapBytes` is the heap size after the last GC, not live data.

Both sides run with `DOTNET_TieredCompilation=0` and `DOTNET_TieredPGO=0`.

## Write in place against repeated Append

It compares millions of two-word `Append` calls with one `Write` call per builder, on 24 independent builders, and checks that the output is identical.

```text
dotnet run --project Supprocom.NativeAllocationManagement.Performance -c Release -- --native-builder-write --samples 6 --records-per-builder 100000 --enforce --output native-builder-write.json
```

| Option | Default | Meaning |
|---|---|---|
| `--samples` | 6 | paired runs; must be even |
| `--records-per-builder` | 100000 | two-word records per builder |
| `--workers` | 24 | builders |
| `--enforce` | off | exit 4 if the result does not meet the gate below |
| `--output` | none | also write the report to this file |

The program starts the workers itself with tiered compilation and PGO off.

### Read the report

| Field | Meaning |
|---|---|
| `meanSpeedup`, `aggregateSpeedup` | `Append` time divided by `Write` time (mean of runs, and total over total); above 1 means `Write` was faster |
| `confidenceLower95` | lower end of the 95% confidence interval of the per-run ratio |
| `repeatedAppendMeanRecordsPerSecond`, `boundedWriteMeanRecordsPerSecond` | throughput of each side |
| `exactParity`, `balancedOrder`, `cleanupPassed`, `boundedNativeAllocationsPassed`, `runtimeConfigurationPassed`, `productionShapePassed`, `binaryIdentityPassed` | checks; all must be `true`. `productionShapePassed` needs `--workers 24` and at least 2,000,000 records in total |
| `gatePassed` | all seven checks passed, both speedups are at least 1.20 and `confidenceLower95` is above 1 |

Exit code: 0 on success, 3 if parity or order failed, 4 if `--enforce` is set and `gatePassed` is `false` (this includes a parity failure). Lowering `--workers` or `--records-per-builder` so the total is under 2,000,000 records makes `--enforce` fail.
