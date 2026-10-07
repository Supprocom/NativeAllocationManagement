# Supprocom.NativeAllocationManagement

`Supprocom.NativeAllocationManagement` gives C# code explicit ownership of native
storage. `NativePool<T>` reuses typed slabs. `NativeRegion` provides one lexical
heterogeneous lifetime. `NativeArena` provides reusable heterogeneous storage.

`NativeBuilder<T>` writes growable unmanaged sequences without managed intermediate
arrays. `NativeTransfer<T>` moves heap-storable ownership across thread boundaries.
`NativeWorkspace<T>` reuses one fixed typed block in a worker hot loop.

Each bounded operation checks the state required by its allocator contract. The bundled
Roslyn analyzer checks ownership and bounded-view rules in consumer source. The package
targets .NET 10.

## Installation and source builds

Install the released `0.2.3` package. This `main`
checkout contains **unreleased 0.3.0 development**, including budgets, prepared
execution, typed layouts and unique/shared/weak ownership. Use the matching
release-tag documentation for 0.2.3 applications. See the [migration guide][version-status]
when moving to 0.3.0; the value-based `NativeTransfer<T>` requires rebuilding
consumers.

[version-status]: https://github.com/Supprocom/NativeAllocationManagement/blob/main/docs/version-status.md

Build the source with .NET SDK 10.0.302 and C# 13. The repository pins the SDK
without feature-band roll-forward.

## Documentation

See [demo process/GC observations](docs/demo-runtime-metrics.md) for their exact
units, sources, lifetimes and availability; cold telemetry is not an allocator budget.

The [getting-started guide][getting-started] contains installation instructions,
complete examples, lifecycle rules, analyzer diagnostics, and cleanup requirements.

The guide covers typed pools, fixed workspaces, lexical regions, reusable arenas,
growable builders, cross-thread transfers, scoped recycling, statistics, and trimming.
The [demo kernel-metrics contract](docs/demo-kernel-metrics.md) distinguishes
actual cgroup sources and units from native budgets and unavailable observations.

[getting-started]: https://github.com/Supprocom/NativeAllocationManagement/blob/main/docs/getting-started.md

## Measured performance

Historical voxel runs estimated a 50 to 85 percent performance improvement
over expert safe C# in non-memory-constrained control profiles.

Very memory-constrained profiles estimate 90 to 130 percent. Selected
higher-turnover runs exceed 150 percent.

Both implementations process equal inputs and outputs. The expert safe C# path uses
pooling, exact sizing, bounded retention, and proactive memory admission.

The matrix treats constrained-memory qualification as information. It verifies equal
binary limits, no swap, and cumulative demand. It does not require garbage collection
or a resident-memory threshold.

Results depend on workload, memory limits and runtime configuration. The
[voxel pipeline guide][voxel-guide] contains the method and runnable commands.
For known-size and growing output, use the [builder benchmark](docs/builder-benchmark.md).
Compare elapsed time and total managed/native allocation for your workload.

[voxel-guide]: https://github.com/Supprocom/NativeAllocationManagement/blob/main/.Demos/01-VoxelChunkPipeline/README.md

## License

This project uses the GNU Affero General Public License, version 3 only. See
[LICENSE](LICENSE) for the complete terms. See [NOTICE](NOTICE) for the project
notice and source offer.

# Ownership diagnostics

Use the [complete diagnostic index](docs/diagnostics/README.md),
[verified cleanup fixes](docs/analyzer-usability.md), and
[managed-first allocator-selection guide](docs/allocator-selection.md).
Every shipped rule has its own help page. Compiler analysis remains required;
IDE fixes do not disable ownership checks or change a memory budget.
