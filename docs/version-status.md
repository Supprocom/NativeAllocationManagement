# Version status and 0.3.0 migration

## Released package versus development source

`0.2.3` is the released package boundary. `main` contains unreleased 0.3.0
development, not an accepted or published 0.3.0 package. The project temporarily
retains `<Version>0.2.3</Version>`; do not publish development bytes under that
already released identity. The final shipping version, public API and diagnostic
release baselines, release notes, installation guide and feed must agree before
publication. Use the matching release-tag documentation for installed 0.2.3.

Both the accepted 0.2.3 source and current development target .NET 10.
Development uses pinned .NET SDK 10.0.302 and stable C# 13. Do not describe the
.NET 10 target as a new 0.3.0 change from 0.2.3, or infer NativeAOT, trimming or
ARM64 readiness from a library analyzer build. Actual final-source consumers
must publish and run on each required Windows/Linux and x64/ARM64 target.

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
history. Disabled tracing does not disable required counters. Every essential
diagnostic must have a definition, units, scope, real update/derivation, consistency
and lifetime rules, positive/negative tests and retained evidence. Unavailable
observations are not measured zero; zero requires a verified invariant. No stub,
fabricated success, disabled essential test or placeholder satisfies completion.

## Performance and release acceptance

Historical voxel percentages are workload-specific evidence, not an accepted
0.3.0 performance claim. Current local complete-cost admission, layout, prepared
and smart-pointer comparisons include failed or inconclusive required gates.
Do not relabel them as accepted because correctness passes, native GC allocation
is low, or a warmed loop is faster. Prefer expert managed C# until NAM removes
more real work or memory demand than it introduces under the same useful-output,
concurrency, cap and retention contract.

Compare preparation, warm-up, admission/refusal, initialization, access, movement,
sharing, growth, retention, enabled/disabled tracing and cleanup—not only the hot
loop. Include metadata and temporary overlapping backing; keep original samples,
uncertainty and unfavorable outcomes. Both deployment-default and controlled
compilation settings and the maintained NAM baseline are required. A failed
essential feature cannot be renamed optional or hidden to pass the release gate.

Complete API/lifecycle/diagnostic inventory, all demos and actual PowerShell
coverage, clean enabled analyzers, final target consumers and immutable
packages/symbols/source/evidence are required. Local development checkpoints are
not release acceptance. Independent Review must accept the exact final boundary
before the later eligible release/deployment handoff; publication then requires
feed and isolated-install verification. No current claim of complete 0.3.0,
universal native superiority or final platform readiness is made here.
