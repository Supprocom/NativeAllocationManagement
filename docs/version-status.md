# Version status and 0.3.0 migration

## Released package versus development source

`0.2.3` is the released package. `main` contains unreleased 0.3.0 development.
Use the matching release-tag documentation for installed 0.2.3 applications;
the new APIs described on `main` belong to 0.3.0.

Both 0.2.3 and 0.3.0 target .NET 10. Source builds use .NET SDK 10.0.302
and C# 13.

## Transfer representation and lifetime migration

`NativeTransfer<T>` changes from a class in 0.2.3 to a readonly value capability
in 0.3.0. Rebuild consumers, including dependent libraries and assemblies that
store transfer fields, channels or parameters. This is not binary compatibility.
Review serialization, reflection and generic constraints that relied on the
former reference type. No automatic schema migration is promised.

A `NativeTransfer<T>?` is now `Nullable<NativeTransfer<T>>`. Check presence before
using `binding.Value`, or use `binding?.Dispose()` in a verified cleanup boundary.
Presence is not proof of live ownership: a stale non-null value has no authority.
Destructive `Move(ref source)` clears its nullable source; failure also consumes
that source. Assignment does not acquire another owner. Never dispose copied
bindings as if each acquired independent ownership. Default values are
uninitialized; even `Dispose` rejects a default value. Copies of stale bindings
can retain the small acquisition control, but deterministic cleanup by the
current binding releases its payload. Emergency finalization is not timely
budget relief.

Shared and weak bindings require explicit fixed-capacity preparation. Sharing is
not imposed on ordinary unique or thread-confined allocations. A small shared
slice retains the complete original backing; explicit detach needs admission for
the new extent while the source remains alive. Weak observers do not own payload
storage. See [unique ownership](unique-ownership.md) and
[immutable sharing](immutable-sharing.md) for the exact failure/cleanup contracts.

## Memory and diagnostic limits

The budget bounds complete known NAM-requested native extents, including known
headers and backend padding, retained, retired and quarantined owned backing.
It does not bound process RSS, managed metadata, CLR object headers, opaque
backend bookkeeping or provider-owned external storage. Producer permission,
initialized logical bytes and physical backing are separate domains; moving or
sharing a binding does not manufacture another charge or physical allocation.

Optional tracing uses bounded managed event storage and has its own cost/loss
history. Disabled tracing does not disable required counters. Unavailable
observations are not measured zero. See [diagnostics](diagnostics.md) for units,
snapshot consistency and lifetime rules, and [memory budgets](memory-budgets.md)
for admission and accounting behavior.

## Measuring your application

Compare NAM with managed storage using the same output, concurrency, memory cap
and retention policy. Include preparation, processing and cleanup, managed
metadata, native backing and temporary growth overlap. The
[builder benchmark](builder-benchmark.md) and [voxel pipeline](../.Demos/01-VoxelChunkPipeline/README.md)
provide runnable comparisons and explain their reported measurements.
