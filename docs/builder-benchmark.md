# Builder comparison domains

The builder worker generates two known-sized uint sequences, hands ownership
through a bounded channel, and computes the same byte checksum without requiring
an independently materialized byte array. Use the strongest applicable managed
comparison; a growable native builder is not automatically the right owner for
known-sized output.

The paired command defaults to `ManagedExactArray`: directly initialize exact
arrays and consume their bytes through spans. `--managed-baseline ManagedListPrefix`
retains geometric List growth but hands its live prefix to the consumer without
ToArray or upload copies. `--managed-baseline ManagedList` preserves the historical
List/ToArray/byte-array materialization comparison; that is not sufficient by
itself to justify NAM. All sides retain identical generated values, sequence
ordering, checksum, batch sizes and the one-slot channel. Managed value packets
hold storage references without an additional per-packet heap owner.

The paired native default is `NativeBuilderBudgeted`; `--native-baseline
NativeBuilder` retains the explicit historical unbudgeted path. The budgeted
worker creates one shared domain inside its preparation clock and gives it to
both builders for correctness validation, warm-up, the measured batch and the
separate probe. `--native-budget-bytes` sets its immutable NAM-requested extent
ceiling (default `Int64.MaxValue`), not a managed-allocation or process-RSS cap.
No admission, allocation, growth, handoff or release cost is omitted from the
applicable complete phase. Tracing is disabled for this comparison.

`--native-baseline NativeBuilderBudgetedDirect` is a separate known-size
integration of the existing bounded writer API. Each output gets its exact
capacity, then one static state writer initializes the destination directly,
with identical batch seed resets and output. It removes geometric growth,
stack-to-builder batch copies and per-batch builder entry checks. It does not
remove ownership/control objects, budget admission, publication or cleanup.
`PreLease` remains the reported input but is not used by this explicit exact-size
path. Growing workers and the paired default are unchanged; this path is not an
unknown-size workload or an accepted performance/allocation advantage. Compare
its complete measured batch against `ManagedExactArray`, counting GC allocation
plus successful requested backing bytes. Fewer copies or zero replacements alone
do not establish the required simultaneous gain.

An explicitly supplied option requires a nonblank value and may occur only once.
A trailing `--native-budget-bytes`, another option in place of its value or a
duplicate fails before any payload allocation; it never selects an unlimited
budget. Negative and nonnumeric budget values also fail.

Every worker boundary requires a `Budget` property. It contains all 19 real
budget fields after budgeted preparation; it is `null` (unavailable), not
invented zero, before preparation and on managed/unbudgeted paths. The worker
reader rejects missing fields, false availability, changing domains or
capacity, overflow, unresolved charge and failed/refused successful-work claims.
Successful `AcquiredBackingBytes` and `ReplacementBackingBytes` are disjoint
requested-extent histories. Their phase deltas plus process-wide GC allocation
provide a defined allocation domain; allocation-event counts and live endpoint
gauges cannot reconstruct it. Replacement history counts the full successful
target, not net growth. It does not reveal opaque realloc's newly allocated
physical bytes, private allocator metadata or transient resident-memory peak.

Example: run the performance executable with `--native-builder
--managed-baseline ManagedExactArray --elements 262144 --prelease 1024
--batch-size 256 --iterations 128 --warmup 16 --samples 12`. Each persisted worker
identity names the implementation actually used. The isolated paired helper
currently forces controlled tiering/PGO; normal-compilation runs require direct
worker invocation with those environment settings unset.

The existing steady-state time/GC-allocation number excludes setup and warm-up;
phase observations are separate measurements, not components to add to that
time. Its `PerformanceAdvantage` field is a time-only observation, not the full
performance-and-allocation release verdict. A smaller GC number does not include
native growth requests. Use actual budget histories on budgeted workers for
that defined domain; legacy unbudgeted request volume is unavailable. This does
not measure opaque backend overhead or establish shared-cap equivalence, managed
pooling, the published previous-NAM matrix, normal-compilation confidence, or
complete four-phase allocation acceptance. Those remain required 0.3.0 gates.

`LifecycleEvidence` and every nested measurement are required in newly serialized
worker evidence: absent observations cannot deserialize into invented zero
measurements; explicit null is rejected by the worker reader. It separates
preparation plus correctness validation, warm-up, the complete measured batch
(including channel/task setup), and the later one-packet phase probe. Each has
wall time and absolute before/after process CPU, process-wide GC allocation,
collection counts, last-observed GC heap size, and implemented native accounting
counters. CPU sampling overhead is excluded from the GC allocation boundaries;
accounting/CPU observers are initialized before preparation on both sides.
These are development-worker observations, not cold process startup costs.

The legacy GC allocation/collection/heap fields now correspond only to the
complete measured batch, ending before RSS queries and the separate probe.
Its legacy elapsed time still covers the inner producer/consumer loop, so use
`LifecycleEvidence.MeasuredBatch.ElapsedMilliseconds` for complete invocation
time. Process CPU is shared by all process threads and has OS timer granularity;
GC heap size is the last collection's value, not instantaneous live storage.
Native observations preserve the epoch and saturation indicator: only compare
event deltas within the same nonsaturated epoch. Outstanding, detached and
retired byte fields are current gauges, not cumulative allocated byte volume.
The probe's managed `GC.KeepAlive` boundary is not physical GC reclamation. Do not
sum these four phases into an application-lifetime or terminal-reclamation
claim, or substitute this evidence for the full performance/memory release gates.

`WorkingSetBeforeBytes` and `WorkingSetAfterBytes` are endpoint resident-memory
observations. `PeakWorkingSetBytes` is the OS-reported process-lifetime working-set
high-water mark through the post-measurement refresh, before the separate phase
probe and report serialization. It includes startup, setup and warm-up; it is not
a resettable steady-state peak, native-only extent or complete process budget.
The pooled worker's `PeakObservedWorkingSetBytes` has the same lifetime definition,
ending before its separately measured disposal. Endpoints cannot reconstruct
these peaks. Isolated builder runs preserve the worker's own observation without
parent RSS polling. A missing/nonpositive OS peak fails evidence collection,
instead of inventing zero; actual platform readiness remains a release gate.
