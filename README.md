# Supprocom.NativeAllocationManagement

`Supprocom.NativeAllocationManagement` gives C# code explicit ownership of native
storage. `NativePool<T>` reuses typed slabs. `NativeRegion` provides one lexical
heterogeneous lifetime. `NativeArena` provides reusable heterogeneous storage.

`NativeBuilder<T>` writes growable unmanaged sequences without managed intermediate
arrays. `NativeTransfer<T>` moves heap-storable ownership across thread boundaries.
`NativeWorkspace<T>` reuses one fixed typed range in a worker hot loop.

The runtime checks owner state, generation identity, allocation identity, and active
operations. The bundled Roslyn analyzer checks ownership and bounded-view rules in the
consumer source. The package targets .NET 10.

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
[LICENSE.md](LICENSE.md) for the complete terms and project-specific source offer.
