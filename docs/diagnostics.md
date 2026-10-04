# Native owner diagnostics

`NativeConcurrentPool<T>.CaptureDiagnosticSnapshot()` and
`NativeConcurrentArena.CaptureDiagnosticSnapshot()` return actual runtime
structural state. The snapshot is a value containing no owner, allocation,
pointer or borrowing authority. Keeping it does not retain native storage.
The voxel NAM serializer copies these fields directly; it does not infer
private counters from the smaller storage-statistics record.

Capture runs under the owner's lifecycle gate. Entered callbacks and the fast
lane can continue payload/cursor work independently. Use a quiescent maintenance
boundary for reconciliation with other snapshots. Capture is not intended for
the per-element or timed processing path. It does not reset any counter.

## Fields and scope

All fields describe the named owner, except `MetricsEpoch`, which identifies the
process accounting measurement scope. Counts are integers; bytes are segment
extent bytes, not process resident memory. Traversal fields are search frontiers,
not an assertion that a segment is occupied at that index.

| Field | Definition and lifetime |
| --- | --- |
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

Definitions are implemented by `NativeOwnerKernel.GetDiagnosticSnapshot`, not
by the demo. Runtime tests cover reference/scoped record changes, real traversal,
missing-generation sentinels, retirement, failed-drain quarantine, and cleanup.
Repeated capture allocates no managed object. A snapshot is an observation,
not a memory reservation or permission to access a stale lease.

`NativeOwnerStatistics` separately reports exposed owned payload capacity,
complete retained backing, and borrowed external ranges. See
[native backing budgets](memory-budgets.md) for backend differences, units and
the distinction between known extents, opaque allocator bookkeeping and RSS.

The fast typed pool's internal initialization and entered-operation probes scan
actual slab states and borrow counts at capture; they do not maintain redundant
hot-path counters. Its metadata capacity probe reports the allocated slab-bank
length, not live/available slab usage. Free-list membership is stored inside that
same bank, so both slab-capacity fields describe the same capacity. There are
genuinely no bump bank, separate segment-owner bank, reference-root bank or
generational quarantine bank in this unmanaged thread-confined model. These
model-specific absences are not a license to zero-fill synchronized-owner fields.

See [bounded budget tracing](memory-budgets.md#owner-identities-and-optional-tracing)
for peaks, refused acquisitions, event units, overflow disclosure and copying.
