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

## Release and development status

Install the released `0.2.3` package for the stable package boundary. This `main`
checkout contains **unreleased 0.3.0 development**; its new APIs and value-based
ownership examples are not a description of the 0.2.3 package. The retained
0.2.3 packaging version is not permission to publish these changed bytes as that
release. See the [version and migration status][version-status] before adopting
the development source. 0.3.0 requires rebuilding consumers; it is not binary
compatible with the former `NativeTransfer<T>` class representation.

Budgets, prepared execution, typed layouts and unique/shared/weak smart pointers
are implemented in development, but their presence is not a performance verdict.
Complete-cost comparisons against expert managed C# and previous NAM, every
essential diagnostic, final target validation and exact package review remain
release requirements. Prefer managed storage until the useful workload supplies
a measured memory or performance justification for NAM's native lifetime risk.

[version-status]: https://github.com/Supprocom/NativeAllocationManagement/blob/main/docs/version-status.md

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

These are historical, workload-specific estimates, not an accepted 0.3.0
performance claim. Current full-cost feature comparisons include unfavorable
or inconclusive results against expert managed C#. Correct output, zero GC
collections or a warmed-loop speedup alone cannot waive those release gates.
The [voxel pipeline guide][voxel-guide] contains the workload method and commands;
new release claims require retained evidence for the exact final source and
package, including deployment-default compilation settings.

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
