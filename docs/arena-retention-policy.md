# Arena retention policy

The required-argument NativeArena budget/policy constructor opts into cold
growth and whole-idle-segment retention. Existing, prepared and external-backed
constructors keep their existing policies. The initial preAllocateBytes is an
exact usable reservation, not an implicit change to the growth threshold.

OrdinarySegmentCeilingBytes bounds preferred geometric segment capacity.
Larger requests receive their exact checked minimum, including required range
alignment, and are classified as outliers by actual usable capacity. An outlier
does not become the next ordinary growth basis. Native headers and backend
padding remain fully charged; the ceiling does not limit live payload demand.
The existing shared budget can choose a tighter minimum when growth slack would
otherwise refuse a valid near-cap request.

Reset, scoped recycle and MaintainRetention first release idle outliers, then
retain a fitting ordinary prefix within IdleRetentionBytes, shared across both
lanes. The limit includes headers and alignment. A partly used bump segment is
not idle: all its storage remains charged until its generation or scope ends.
Live scratches and entered callbacks cannot be evicted to satisfy the target.
Explicit maintenance does not invalidate live scratches, but rejects entered
callbacks and initialization before any release. A zero idle target is valid.

CaptureRetentionSnapshot is thread-confined, allocation-free and does not reset
history. RetainedBytes includes all physically retained native extent. IdleBytes
counts reusable whole owned segments, excluding external units and partial slack;
closed owners expose zero reusable authority. OversizedBytes is a current subset
of retained extent; PeakOversizedBytes records its actual historical maximum.
ReleasedBytes records only physical policy-maintenance frees; unrelated trim and
final disposal receive no invented policy-release credit. MaintenanceCount counts
actual enabled reset/recycle/maintenance invocations, even if nothing is released.
History saturates with the owner HistoryOverflowed flag; current gauges stay exact.
Disabled policy snapshots have Enabled=false and invariant-zero policy histories.

Selection, scans and observations occur at cold growth or explicit maintenance,
not per element or on a warm bump reservation. No timer or global controller is
introduced. Reduced outlier retention is not proof of RSS reduction or a general
speedup. Final measurements must include maintenance, preparation and teardown,
and compare equivalent managed sizing/retention policies and previous NAM.
