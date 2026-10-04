# Region initialization and entered operations

`NativeRegion` keeps one construction-thread lifetime and publishes a `Local<T>`
only after its initializer completes the requested range. While an initializer
is active, nested initialization and disposal are invalid. They reject before
another reservation or backing release, with `NativeAllocationInUseException`.
Its `ActiveOperationCount` includes that initializer and any entered bounded
callbacks; the internal admission encoding is never reported as a count.

An entered ordinary callback can acquire another range when no initializer is
already active. Existing published storage remains valid while initialization
runs. A failing or incomplete initializer restores its cursor checkpoint and
does not publish logical demand; its already acquired backing remains retained
and charged for later reuse or final cleanup. These checks remain effective
when a consumer disables analyzer diagnostics.

The implementation reuses the existing thread-local admission integer. It adds
no owner field, per-range allocation record, reference count or synchronization.
Initialization admission and rollback, required accounting and final cleanup
belong in comparisons with equally bounded managed storage. Allocation-free
execution is a structural guarantee, not a performance-superiority claim.
