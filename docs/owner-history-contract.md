# Owner event histories and authority

`FreshSegmentAllocationCount`, `TrimCallCount`, and `TrimmedBytes` are lifetime
observations, not allocation authority. Histories saturate at `long.MaxValue` and
the owner reports `HistoryOverflowed` after an event would exceed that value.
Reaching the maximum exactly remains an exact observation until another event
occurs. No history overflow can interrupt completed acquisition, publication,
physical release or final cleanup.

Current retained, retired and requested-byte gauges remain exact and are not
derived by subtracting saturated histories. Allocation ordinals, generation
epochs and handle versions remain separately checked, non-reusing authority;
exhaustion rejects before producer work or publication instead of wrapping or
reusing a saturated diagnostic count. Traces preserve the real acquisition
ordinal even when the completed-acquisition count has saturated.

These histories update at their existing physical-event or maintenance boundary.
They add no budget admission, reference count, registry, synchronization or event
payload construction to warmed lease or element access. The added saturation
comparison and lifetime overflow state still belong in the complete performance
and memory comparison. Production/process accounting and the full field
inventory are separate essential work; this contract is not a claim that those
remaining requirements are implemented.
