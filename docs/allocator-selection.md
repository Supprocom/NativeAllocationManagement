# Choose managed storage first

NAM must remove useful-work or retention costs that an expert managed equivalent
cannot remove under the same output, capacity, ownership and concurrency contract.
Use arrays, spans, direct destination writes and appropriate managed pooling when
they satisfy that contract. Zero GC allocation alone does not justify native risk.
Include control/shape metadata, retained capacity, initialization, preparation,
handoff and teardown in the comparison. Do not compare NAM to a deliberately
materializing, copying or allocating managed path.

| Real requirement | NAM candidate | Required tradeoff |
| --- | --- | --- |
| One lexical unmanaged allocation boundary | NativeRegion | No escape or suspension; direct using construction and complete initialization. |
| Fixed-shape reusable buffers on one thread | NativePreparedPool | Prove simultaneous slot bounds; sparse pages and retained capacity still cost memory. |
| Variable-size same-type buffers on one thread | NativePool | Explicit size-class reuse and growth; return policy controls physical cleanup. |
| Heterogeneous ranges with one thread-affine lifetime | NativeArena | Use prepared bounds and required composite APIs; reset/recycle cannot invalidate entered work. |
| Actual concurrent owner operations | Concurrent pool/arena | Measure coordination and generation retention; do not select it solely for an API gap. |
| Grow unknown-size output into one contiguous result | NativeBuilder | Admit conservative resize overlap; copying/reallocation and peak capacity matter. |
| Persistent mutable application state | NativeWorkspace | Pay preparation once over a real amortization boundary and prove owner-local teardown. |
| Independently moved initialized storage | NativeTransfer | One acquisition-time control, no shared refcount; source versions become stale. |
| Immutable fan-out needing independent owners | NativeShared/NativeWeak | Explicit transitions and prepared binding limits; weak observers must not retain payload. |
| Variable-sized production under a shared native cap | NativeMemoryBudget reservation | Reserve before production; prepare backing before a no-growth producer; distinguish metadata/backend errors. |
| Several regions with genuinely coincident lifetime | NativeLayout | Reuse checked shape; count padding/slack/metadata; independent fields require explicit overlap-admitted copy. |

The budget covers complete NAM-requested native extents, including retained,
retired and deferred-owned storage until real physical release. It is not RSS,
opaque backend headers, managed control memory or an OS-residency guarantee.
Borrowed mapped backing is reported separately and requires the provider's proven
lifetime. A native-byte ceiling cannot stand in for a process/container cap.

Preparation must cover backing, metadata and handle/slot capacity for the actual
simultaneously live shapes. Within that declared boundary, use the supported
nonallocating refusal path instead of hidden growth. Preparing bytes alone does
not prove independently available slots. Reusing already charged storage is not
a new budget grant.

Unique ownership is the default. Sharing is justified only when it eliminates
more copying or coordination than its binding/control transitions add. A small
shared slice retains full backing until explicit detach; lingering weak observers
retain control state, not payload. Grouping unrelated layout lifetimes can worsen
retention even when it reduces the number of handles.

Use actual snapshots and correlated bounded traces to explain ownership, peaks,
rejections, copies, clear work and eventual return. Do not substitute zeros for
private counters or claim RSS relief from a NAM backend free. Compare controlled
and production tiering/PGO, paired ordering, identical outputs, uncertainty and
preserved workload regressions before calling a workload superior.

Start with [budget contracts](memory-budgets.md), [prepared pools](prepared-pools.md),
[arenas](prepared-arenas.md), [admission](application-admission.md),
[unique ownership](unique-ownership.md), [sharing](immutable-sharing.md),
[layouts](typed-layouts.md), and [all diagnostics](diagnostics/README.md).
