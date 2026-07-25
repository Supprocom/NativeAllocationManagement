# Voxel chunk pipeline

This demo is a deterministic, memory-pressure-heavy comparison of expert safe C# and NAM for a compact voxel build, prerender, mesh-packing, and upload-staging pipeline. It is self-contained and does not reference the VoxelEngine repository. The five subfolders make the ownership boundaries explicit: `SharedContract` defines the workload and validation contract, `SafeCSharp` is the safe managed baseline, `NAM` is the safe C#++ implementation, `PACSharp` is the PAC# counterpart fixture, and `Harness` runs isolated children and the paired statistical gate.

The workload follows the source-backed VoxelEngine shape. Runtime block metadata is setup-time data with custom `ushort` identifiers beginning at 256, while measured cells carry only those identifiers. The registry varies payload size, alignment, opacity, face-tile stage mask, transparent-palette cardinality, and frequency. Each chunk deterministically exercises Empty, Uniform, Expanded, Packed, and MultiPacked section representations, including dominant and residual transparent paths. The source references are `MVGE-INF/Models/Terrain/BlockType.cs:9-23`, `MVGE-INF/Loaders/TerrainLoader.cs:18-29,215-289,384-387`, `MVGE-INF/Models/Generation/Section.cs:12-80`, and `MVGE-GEN/Terrain/ChunkGeneration.cs:41-108,236-567` at VoxelEngine commit `4969faeb07af77f5cfaad21b06f680490014aac3`.

Both C# paths retain the expert techniques that are already appropriate for bounded voxel work: worker-local reuse, `ArrayPool<T>`, pre-sized typed output, value-oriented descriptors, stack-based section counters and masks, and zero per-cell object creation. The Safe C# path rents its variable transparent-mask backing from `ArrayPool<ulong>`. NAM adds typed `NativePool<T>` leases for coordinate, face, vertex, index, and upload descriptor stages, uses `LeaseScoped` at the actual stage boundaries, and invokes `RecycleScoped` only after the analyzer-proven lexical borrow ends. A reusable `NativeArena` stores the heterogeneous transparent-mask and upload staging ranges; it is not used to replace bounded stack storage or to force every value through one allocator. The benchmark keeps runtime block metadata and any managed payload-object accounting separate from managed backing/container bytes.

The measured lifetime is generation-local and remains inside each worker. Coordinates, density, and material IDs are no longer needed after face derivation, face records end after vertex/index emission, and the variable descriptor and upload streams end after the deterministic upload sink consumes and hashes their bytes. Each worker disposes its owners with the configured native return policy, and the result requires final NAM native bytes to be zero. No NAM handle or ref-struct lease crosses a worker boundary.

Build the C# children and harness with:

```text
dotnet restore .Demos/01-VoxelChunkPipeline/Harness/Harness.csproj
dotnet build .Demos/01-VoxelChunkPipeline/SafeCSharp/SafeCSharp.csproj -c Release --no-restore
dotnet build .Demos/01-VoxelChunkPipeline/NAM/NAM.csproj -c Release --no-restore
dotnet build .Demos/01-VoxelChunkPipeline/Harness/Harness.csproj -c Release --no-restore
```

Run correctness parity with `dotnet .Demos/01-VoxelChunkPipeline/Harness/bin/Release/net10.0/VoxelChunkPipeline.Harness.dll --correctness-only`. The controlled benchmark uses two warmups followed by at least thirty alternating Safe/NAM pairs. It records every child result and writes raw JSON under the ignored `artifacts` directory. The optional `--enforce` switch returns failure unless all counters match, NAM throughput is at least five percent higher, the paired 95% speedup interval is entirely above 1.00, managed allocation is materially lower, and final native bytes are zero. The default closeout command reports an honest failed gate when the measured hypothesis is not met; it does not weaken or shorten the Safe C# work.

The PAC# file is intentionally package-driven. The installed PACSharp.NET SDK compiler can compile the deterministic runtime-ID fixture through its explicit `PacSharpCompile` target, but the installed package binaries reject the current pool/arena owner declaration surface even though the PACSharp source checkout contains that newer syntax. The fixture and report record this exact semantic/toolchain gap instead of presenting managed PAC# output as native lifecycle parity. Normal `dotnet build` for that package currently fails in the SDK wrapper because `CreateManifestResourceNames` is missing; the reproducible command is `dotnet msbuild .Demos/01-VoxelChunkPipeline/PACSharp/VoxelChunkPipeline.pacproj -t:PacSharpCompile -p:Configuration=Release`.

This demo is local verification only. It does not publish or version-bump a NuGet or GitHub package.
