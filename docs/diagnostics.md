# Native owner diagnostics

`NativePool<T>`, `NativeRegion`, `NativeArena`, `NativeConcurrentPool<T>` and
`NativeConcurrentArena`, `NativeBuilder<T>` and `NativeWorkspace<T>` expose
`CaptureDiagnosticSnapshot()` with actual runtime
structural state. The snapshot is a value containing no owner, allocation,
pointer or borrowing authority. Keeping it does not retain native storage.
The voxel NAM serializer copies these fields directly; it does not infer
private counters from the smaller storage-statistics record.

Synchronized capture runs under the owner's lifecycle gate. Fast capture validates
the construction thread and can observe terminal state without authorizing payload
use. Entered synchronized callbacks and the fast lane can continue payload/cursor
work independently. Use a quiescent maintenance
boundary for reconciliation with other snapshots. Capture is not intended for
the per-element or timed processing path. It does not reset any counter.

## Fields and scope

All fields describe the named owner, except `MetricsEpoch`, which identifies the
process accounting measurement scope. Counts are integers; bytes are segment
extent bytes, not process resident memory. Traversal fields are search frontiers,
not an assertion that a segment is occupied at that index.

| Field | Definition and lifetime |
| --- | --- |
| `Model` | Actual thread-confined or synchronized pool, arena or lexical-region model. `Unspecified` is a caller-created default value, not an observed runtime owner. The demo maps the real model and uses `null` when unavailable. |
| `OwnerId` | Stable positive process-local allocator identity, assigned once without a global owner registry. It remains the same across generations and disposal; it is not an address or borrowing capability. The demo copies it directly and uses `null` when no NAM owner is available. |
| `Lifecycle` | Actual owner gate state at capture, including unleased, returned and disposed owners. |
| `Generation` | Current or most recent owner generation identity, starting at zero. Generation transitions precompute overflow before changing authority; identifiers are not addresses. |
| `ScopeEpoch` | Current generation's scoped epoch, advanced by scoped-lifetime transitions. Zero when there is no current generation. Overflow is checked before committing a transition. |
| `MetricsEpoch` | Actual process accounting epoch. Internal measurement tests advance it when resetting observations; ordinary production capture does not reset it. Zero is a valid initial epoch. |
| `ActiveRecords` | Current active allocation-table and materialized composite metadata records plus live arena transfer slots and one grouped fast scoped lane when active. Ordinary fast bump ranges need no individual record until materialized for composite access and are not relabeled as logical lease counts. |
| `ScopedRecords` | Current scoped allocation-table and materialized composite records, plus the grouped fast scoped lane when active. Repeated access reuses its materialized record; recycle closes the scope and its records. |
| `ReferenceRoots` | Current non-null managed roots held by the native reference-slot table. Unmanaged payloads genuinely require no roots. |
| `OrdinaryTraversalIndex` | Index of the current fast ordinary candidate, otherwise the forward bump-search frontier, which can equal the segment-bank length. Minus one without a current generation; zero is the initial frontier of an empty active bank. |
| `ScopedTraversalIndex` | Index of the current fast scoped candidate, otherwise the reverse scoped-search frontier. Minus one means no candidate is available. It is never manufactured as occupied segment zero. |
| `RetainedSegmentCount` | Current-generation slab and bump-bank count, including attached external segments. Returning a logical range does not necessarily release a segment. |
| `AvailableSegmentCount` | Available current-generation slabs plus completely idle bump segments. Partly used backing is not a completely idle segment. |
| `RetiredGenerationCount` | Generations waiting for entered operations to drain, excluding quarantined generations. Drain removes or rejoins their storage. |
| `RetiredSegmentCount` | Retired and quarantined segment banks combined. It overlaps `QuarantinedSegmentCount`; do not add them. |
| `RetiredBytes` | Sum of complete known NAM-owned extents in retired and quarantined banks, including backend size-alignment padding. Provider-owned ranges are excluded and reported through `NativeOwnerStatistics.RetiredBorrowedBytes`. It includes storage that cannot safely be reused yet, not merely live logical payload. |
| `QuarantinedGenerationCount` | Failed-drain generations prevented from normal reuse, until physical release/detachment under the chosen cleanup policy. |
| `QuarantinedSegmentCount` | Segments in those quarantined banks; a subset of `RetiredSegmentCount`. |
| `CurrentGenerationQuarantined` | The current generation's actual quarantine flag, or false without a current generation. A quarantined old generation does not mark its healthy replacement as quarantined. |

Synchronized definitions are implemented by `NativeOwnerKernel.GetDiagnosticSnapshot`,
not by the demo. Runtime tests cover reference/scoped record changes, real traversal,
missing-generation sentinels, retirement, failed-drain quarantine, and cleanup.
Repeated capture allocates no managed object. A snapshot is an observation,
not a memory reservation or permission to access a stale lease.

## Thread-confined field interpretation

The model tag is essential when interpreting an owner-independent saved value.
Fast pool `ActiveRecords` counts its actual live/initializing slab metadata;
a zero-length lease is a real metadata record but is not a physical segment.
Retained/available segment counts exclude that zero-extent record. Its ordinary
traversal index is the actual returned-slab cache candidate, or minus one with
no candidate/active owner; it is not a bump frontier.

Fast arena and lexical-region ranges do not acquire individual allocation-table
records. Their active/scoped record and managed-root counts are genuinely zero,
not substituted estimates of logical lease count. Ordinary/scoped indices locate
the actual current segment within each corresponding linked bank, or minus one
when absent. Region has no scoped bank. A newly reserved region segment is idle
until its cursor advances; its availability now agrees in both storage and
structural snapshots, including initializer-failure rollback.

Fast pool and lexical region have one nongenerational lifetime: generation and
scope counters are invariant zero. Fast arena reports its real checked UInt64
generation/scope epoch using the existing signed Int64 bit representation; use
`unchecked((ulong)value)` for the full sequence domain, rather than inferring an
overflow or missing value from a negative signed representation. It retains those
last epochs at terminal capture. Synchronized capture instead uses zero scope
without a current generation. The model distinguishes these contracts.

No fast owner has a generational-retirement or quarantine bank. Those fields are
invariant zero/false, with explicit invariant tests during active and terminal
states. GC-detached backing can remain in a closed fast kernel until finalization;
the retained bank remains observable while physically present, but availability
is zero for a closed owner. These snapshots do not retain that kernel. Captures
keep the kernel alive only until the native-header traversal completes.

`NativeOwnerStatistics` separately reports exposed owned payload capacity,
complete retained backing, and borrowed external ranges. See
[native backing budgets](memory-budgets.md) for backend differences, units and
the distinction between known extents, opaque allocator bookkeeping and RSS.

The fast typed pool's internal initialization and entered-operation probes scan
actual slab states and borrow counts at capture; they do not maintain redundant
hot-path counters. Its metadata capacity probe reports the allocated slab-bank
length, not live/available slab usage. Free-list membership is stored inside that
same bank, so both slab-capacity fields describe the same capacity. There are
genuinely no bump bank, reference-root bank or generational quarantine bank in
this unmanaged thread-confined model. Prepared fixed-shape mode has a real page
bank, reported by the segment-owner capacity probe. In that mode physical segment
counts are pages, available segments are completely idle pages, and active
records are occupied slots. The actual acquisition-ordinal probe reads retained
slab/page identities instead of returning an empty placeholder. These
model-specific absences are not a license to zero-fill synchronized-owner fields.

See [prepared page diagnostics](prepared-pools.md#prepared-snapshot-inventory)
for full retained capacity, historical occupancy, exhaustion, managed bank costs
and sparse-page retention.

See [bounded budget tracing](memory-budgets.md#owner-identities-and-optional-tracing)
for peaks, refused acquisitions, event units, overflow disclosure and copying.

## Direct builders and workspaces

The `SingleWriterBuilder` and `ThreadConfinedWorkspace` model tags identify direct
blocks, not generational segment banks. Each owned nonempty direct block is one
retained segment and one active control record. It is available only when its
owner is active, no workspace callback is entered, and no logical range is live.
These unmanaged models have no reference roots, scoped records, generations,
retired or quarantined banks, or detached backing. Their corresponding values
are invariant zero/false; both traversal indices are minus one. They never
borrow external backing. Disposal physically returns their direct block;
builder completion instead transfers it to the unique owning handle.

`GetStatistics()` and `CaptureDiagnosticSnapshot()` remain numeric observations
after completion or disposal. They do not restore `Count`, `Capacity`, `Length`,
append, processing, or payload authority. Builder capture takes its existing
exclusive-operation gate and rejects an entered operation without aborting that
writer. Workspace capture remains confined to its construction thread, including
after release, and is allowed during that thread's callback.

Backing fields count known acquired block extents in bytes. Builder reallocation
keeps one growable storage segment, so `FreshSegmentAllocationCount` counts its
first successful nonempty acquisition once, not reallocations. Budget and process
statistics separately record actual reallocations. `PeakOutstandingNativeBytes`
is the largest known acquired extent; it does not reconstruct opaque native
allocator-internal old/new overlap or RSS. Conservative budget overlap admission
remains separately enforced. Workspace backing is fixed at construction. Current
backing becomes zero after disposal or builder completion, while numeric peaks
survive. No snapshot holds a reference to the transferred payload.

Builder `RequestedBytes` and `InitializedPayloadBytes` are its successfully
published prefix, not reserved capacity or an unfinished writer. Its monotonic
published count and capacity preserve historical peaks without added append-time
accounting. A cancelled terminal operation preserves any prefix actually
committed before cancellation; an incomplete producer never earns publication
credit.

Workspace checked `Initialize` publishes a prefix only after full initialization
and cancellation checks. Its raw `Process` works within already zero-initialized
reusable storage: current demand is the bounded entered range during its
callback, then zero, without creating a persistent published range. A callback
failure still records that genuinely entered range as a peak; an invalid or
pre-cancelled request does not. `Reset` removes published visibility, not history
or the retained block. The existing active-use flag encodes `~length` for Process
and positive one for other uses, preserving nested-use and disposal guards even
for zero-length Process. One owner-local high-water field records initialized
demand. These are owner-lifetime histories, independent of process measurement
resets. Counts and byte products are bounded by checked capacity admission; there
is no wrapping history or optional-counter mode.
