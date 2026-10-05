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

The canonical source build uses .NET SDK 10.0.302 and stable C# 13, with no
SDK feature-band roll-forward. Isolated package-consumer tests also compile with
C# 13. Library builds require trimming and NativeAOT analysis; actual deployment
compatibility additionally requires publishing and running the package consumers
on each supported native target.

## Documentation

The [getting-started guide][getting-started] contains installation instructions,
complete examples, lifecycle rules, analyzer diagnostics, and cleanup requirements.

The guide covers typed pools, fixed workspaces, lexical regions, reusable arenas,
growable builders, cross-thread transfers, scoped recycling, statistics, and trimming.

[getting-started]: https://github.com/Supprocom/NativeAllocationManagement/blob/main/docs/getting-started.md

## Measured performance

The included voxel benchmark estimates a 50 to 85 percent performance improvement
over expert safe C# in non-memory-constrained control profiles.

Very memory-constrained profiles estimate 90 to 130 percent. Selected
higher-turnover runs exceed 150 percent.

Both implementations process equal inputs and outputs. The expert safe C# path uses
pooling, exact sizing, bounded retention, and proactive memory admission.

The matrix treats constrained-memory qualification as information. It verifies equal
binary limits, no swap, and cumulative demand. It does not require garbage collection
or a resident-memory threshold.

These estimates apply only to the included workload and test system. The
[voxel pipeline guide][voxel-guide] contains the method, commands, and current
evidence.

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
