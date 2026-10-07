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
native growth requests, retained extents, metadata preparation, or backend
overhead. This comparison does not yet establish shared-cap equivalence, managed
pooling, the published previous-NAM matrix, normal-compilation confidence, or
complete four-phase allocation acceptance. Those remain required 0.3.0 gates.

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
