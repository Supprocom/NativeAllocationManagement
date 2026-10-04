# Prepared thread-confined heterogeneous arenas

`NativeArenaPreparation` declares exact usable byte bounds for the ordinary and
scoped lanes. Construction admits both complete native extents before acquiring
either lane. Headers and backend alignment padding count against the supplied
`NativeMemoryBudget`; inter-range alignment consumes the declared usable bytes.
It is not a process-RSS limit or a promise about native allocator bookkeeping.

```csharp
NativeMemoryBudget budget = new(4096);
using NativeArena arena = new(new NativeArenaPreparation(1024, 1024), budget);

if (arena.TryScratch<int>(128, static writer => writer.Fill(42), out ArenaLease<int> values))
{
    _ = values.Read(static view => view[127]);
}
else
{
    // The caller chooses how to handle a byte-capacity refusal.
}
```

`TryScratch` and `TryScratchScoped` require explicit preparation. Within their
bounds, NAM allocates no fresh backing, managed owner, lease wrapper or metadata
bank. Leases are existing thread-confined value capabilities. The initializer
must publish the complete requested range, exactly as on an ordinary arena.

Capacity refusal returns `false` and a default lease before the initializer or
backend runs. Invalid arguments, wrong-thread use, closed owners, nested
initialization and incomplete initialization remain errors. A default or stale
lease never grants payload authority. Prove the actual successful result before
using its output; [NAM1050](diagnostics/NAM1050.md) covers missing proof.

`Scratch` and `ScratchScoped` on a prepared owner use the same bounds and throw
on exhaustion; they do not silently grow. Zero-byte lanes and zero-length
requests are valid and do not manufacture backing allocations.

`Reset` invalidates both lanes and reuses their retained backing. `RecycleScoped`
invalidates only the scoped lane. These operations preserve construction-thread,
generation and active-borrow checks. Their non-reusing epochs do not wrap.

Scoped Try outputs must stay in an exclusive local owner's lexical batch. End
that output scope before `RecycleScoped`; an out declaration in an if condition
otherwise remains in its enclosing block. The analyzer tracks the complete
potentially acquired set, including a capacity-refused empty set, rather than
allowing a live lexical output to cross recycling.

Trimming can release a wholly idle prepared lane. Whole-lane release may exceed
the requested trim-byte target. Original preparation bounds remain diagnostic
history, not currently available authority: a trimmed lane never refills. A new
prepared owner is required to acquire capacity again. Disposal frees backing
deterministically; emergency finalization is not timely admission relief.

`CapturePreparedSnapshot` is allocation-free, construction-thread-only and does
not reset history. Used bytes include alignment and initializing reservations;
available bytes describe actual remaining retained lane capacity. Recorded peaks
include reservations later rolled back. Success counts advance only after full
publication, refusal counts advance before a producer runs, and initializer
failures include throwing and incomplete producers. Event histories saturate with
`HistoryOverflowed` instead of interrupting ownership or cleanup. Snapshots after
closure preserve history and identity but expose no available authority.

Prepared arenas have no allocation-record or slot bank. Their managed costs are
the owner/control and caller's initializer, not a fabricated per-lease array.
Those object costs, native headers, preparation and teardown belong in performance
comparisons with equivalently prepared managed storage. Zero allocation alone
does not establish a performance advantage.
