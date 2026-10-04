# Explicit copy accounting

NativeMemoryStatistics.CopiedBytes is a completed-work history for NAM-controlled
unmanaged bulk copying. It records actual source length times representation size
after a successful copy. Initializer and sequential writer ranges, builder span
append and bounded copy-in/out are included. Delegated calls count once, not at
both wrapper and storage boundaries. Partial initialization failure does not erase
copying already performed; this is not a published-live-data gauge.

Empty copies and same-start memmove no-ops add no bytes. Distinct overlapping
ranges retain normal span memmove semantics and record their moved length.
Precondition failures add no bytes. Reallocation is opaque: its requests and
successful calls are measured separately, but no payload-copy volume is invented.
Arbitrary callbacks, consumer span writes and managed-reference/root assignment
are excluded. Assigning an object reference does not copy its managed payload.
These counters do not measure all process memory traffic or physical bus writes.

History uses the existing bounded thread metrics, completed-thread accumulator and
atomic fallback. It resets only with the internal quiescent measurement epoch and
saturates with HistoryOverflowed. Counting adds a range-boundary identity check,
one history scalar per metric/aggregate and a saturating update, not per-element
work, metadata growth or a new ownership/control service. Unmanaged allocation
copy-out now uses one bulk span operation instead of repeated GetValue calls.
Structural zero-allocation checks and existing performance regressions are required
but do not establish the final old-NAM/expert-managed comparative advantage.
