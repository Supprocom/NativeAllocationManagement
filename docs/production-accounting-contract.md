# Production accounting contract

Accounting is active in ordinary runtime operation, without enabling tests or
tracing. Fault injection can reject a backend operation but cannot disable its
accounting. Measurement reset is internal, quiescent, and advances a checked
non-reusing epoch; pre-epoch release is excluded, while successful resizing brings
that complete block into the current measurement epoch.

AllocationCount counts successful backing acquisitions, including realloc from
null and a successful resize of pre-epoch backing. ReallocationCount counts actual
successful realloc calls, not a fabricated allocate/free pair. FreeCount counts
measured physical releases. ZeroedAllocationCount is the zeroed subset of backing
acquisitions. DetachedGenerationCount counts actual measured generation detach
events, independently of payload size. Failed backend operations add no successful
event or outstanding extent.

OutstandingNativeBytes covers known NAM-owned native extents, including tracked
headers/alignment. It excludes provider-owned mapping capacity and opaque native
allocator overhead. PeakOutstandingNativeBytes records actual high-water usage,
including intermediate growth. DetachedNativeBytes and RetiredNativeBytes are
overlapping subsets, not additional allocations to add to outstanding bytes.
RetainedNativeBytes is outstanding minus detached. These gauges remain exact;
completed event and byte histories saturate with HistoryOverflowed rather than
wrapping or throwing after work has completed.

BumpTraversalVisitCount is actual segment search work. ReusedNativeSegmentCount
counts backing reuse, not physical acquisition. ReclaimedRangeReuseCount/Bytes are
actual reclaimed ranges consumed. No element access increments these counters.

StorageClearCount counts NAM-controlled nonempty clear ranges. StorageClearBytes
counts bytes visited by those operations; WrittenClearBytes counts bytes actually
zero-written. Direct native range clears write their full range. Reference-root
cleanup visits its declared slot range but skips already-empty slots, so visited
and written bytes can differ. Partial initialization cleanup counts only its
initialized prefix. Direct bounded-view Clear and workspace initial backing clears are included;
backend allocation zeroing is reported separately by ZeroedAllocationCount.
Arbitrary consumer AsSpan().Clear() or callback writes are not introspectable and
are excluded. Reference cleanup records a range once using root counts under its
existing gate, without adding a per-element counter.

Hot histories use an array with fixed capacity 64 plus one shared fallback object
and one value accumulator for completed history. Local metric objects are created
on cold slot claim, then reused; the array never expands. Slot claim folds an
exited thread's history before reuse. A local strong Thread wrapper reference
protects its weak liveness observation while the actual thread is alive. The bank
retains no owner and no strong historical Thread reference. Excess simultaneously
live threads share atomic histories instead of exhausting application capacity.
Owner construction prepares accounting for its thread before prepared execution.
Cold metric-object or weak-reference allocation failure uses the preexisting
atomic fallback instead of allowing diagnostics to fail a completed operation.
Partial cold registration keeps completed history folded exactly once. Narrow
internal probes inject OOM before selection, before folding and after folding;
fault state remains in the separate test-hook class, not in production counters.

Warm updates add a local/shared branch and saturation arithmetic; global physical
events and excess-thread fallback use saturating compare/exchange. Cold startup
adds the fixed reference array, lock, fallback object and claimed local objects;
each claim adds one weak reference. This removes an unbounded historical-thread
bag and allocating snapshot enumeration. Snapshot visits only the claimed prefix
and aggregates into a stack value
under the cold claim gate and allocates nothing after initialization. Counters can
advance concurrently, so reconciliation requires a quiescent measurement boundary.

The maintained [20-field process contract](../conformance/native-process-diagnostic-contracts.json)
records each field's units, scope, update/derivation source, consistency, reset,
overflow and linked positive/fault/cleanup proofs. Reflection checks require exact
agreement with the published schema and independently declared expected fields;
new fields cannot silently remain unmapped. The exact public-only oracle source
also compiles and runs in an isolated analyzer-enabled package consumer.

The oracle takes one quiescent initial baseline in an unsaturated measurement
epoch, then independently calculates complete workspace/builder/transfer extents,
actual copies and clears, successful realloc and final releases. It never builds
a production snapshot as its expected result or reads later observations into
its expectation. Generation/reclaim fields stay at their true direct-owner
invariants; separate positive retirement, detach, reuse, epoch and overflow tests
cover the mechanisms that can advance them.
