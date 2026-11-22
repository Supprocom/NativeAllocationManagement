# Diagnostics

Each NAM analyzer diagnostic has a page with a wrong and a right example.

| ID | Severity | What it catches |
|---|---|---|
| [NAM1001](NAM1001.md) | error | A pool, arena or region assigned to a second variable |
| [NAM1002](NAM1002.md) | error | A lease copied to another variable |
| [NAM1003](NAM1003.md) | error | A lease not disposed on some path |
| [NAM1004](NAM1004.md) | error | A lease used after it was disposed, reset or returned |
| [NAM1005](NAM1005.md) | error | `Dispose`, memory return or `LeaseFromMemory` called on an owner declared with `using` |
| [NAM1006](NAM1006.md) | error | A region not declared as a `using` resource |
| [NAM1007](NAM1007.md) | error | `Reset` or a memory return while a lease is still live |
| [NAM1009](NAM1009.md) | error | A call the owner's current state does not allow |
| [NAM1011](NAM1011.md) | error | A lease held across `await` or `yield` |
| [NAM1012](NAM1012.md) | error | A region lease that leaves its region |
| [NAM1013](NAM1013.md) | error | A pooled lease returned, stored or discarded |
| [NAM1015](NAM1015.md) | error | A field pool using `NativeMemoryReturn.Free` with no `Dispose` for it |
| [NAM1016](NAM1016.md) | error | A lease passed to a method the analyzer cannot read |
| [NAM1017](NAM1017.md) | warning | Warning: garbage-collector return or release while a lease is live |
| [NAM1018](NAM1018.md) | error | A `ScratchScoped` result not stored in a `scoped` local |
| [NAM1019](NAM1019.md) | warning | Warning: a `scoped` local initialized by `Scratch` |
| [NAM1020](NAM1020.md) | warning | Warning: a scoped lease with no `RecycleScoped()` on some path |
| [NAM1021](NAM1021.md) | error | A transfer copied instead of moved |
| [NAM1022](NAM1022.md) | error | A transfer used after `Dispose` or `Move` |
| [NAM1023](NAM1023.md) | error | `Move` of a transfer that was already moved or disposed |
| [NAM1024](NAM1024.md) | error | A transfer view or span that leaves its callback |
| [NAM1025](NAM1025.md) | error | A transfer not disposed or moved on every path |
| [NAM1026](NAM1026.md) | error | A transfer not assigned to a local |
| [NAM1027](NAM1027.md) | error | A transfer parameter declared `in`, `ref` or `out` |
| [NAM1028](NAM1028.md) | error | A builder copied, boxed or stored |
| [NAM1029](NAM1029.md) | error | A builder used after `Complete` or `Dispose` |
| [NAM1030](NAM1030.md) | error | `Complete` on a builder already completed or disposed |
| [NAM1031](NAM1031.md) | error | A builder never completed or disposed |
| [NAM1032](NAM1032.md) | error | A builder not created directly into a local |
| [NAM1033](NAM1033.md) | error | A builder passed as a parameter |
| [NAM1034](NAM1034.md) | error | A `Complete` result discarded, widened or stored in a field |
| [NAM1035](NAM1035.md) | warning | Warning: managed allocation inside a region's scope |
| [NAM1036](NAM1036.md) | error | A workspace copied or boxed |
| [NAM1037](NAM1037.md) | error | A workspace used after `Dispose` |
| [NAM1038](NAM1038.md) | error | A workspace not disposed on every path |
| [NAM1039](NAM1039.md) | error | A workspace not created directly into a local |
| [NAM1040](NAM1040.md) | error | A workspace parameter that is not `scoped in` |
| [NAM1041](NAM1041.md) | error | A builder `Write` span that leaves the callback |
| [NAM1042](NAM1042.md) | error | A builder writer copied, or its callback passed through a stored delegate |
| [NAM1043](NAM1043.md) | error | A builder borrow returned, stored or captured |
| [NAM1044](NAM1044.md) | error | A builder borrow parameter that is not `scoped ref`, or the builder used during a borrow |
| [NAM1045](NAM1045.md) | error | `Write` state captured, stored or passed on |
| [NAM1046](NAM1046.md) | error | `Write` state or callback that is not direct plain data |
| [NAM1047](NAM1047.md) | error | A `Process` callback on a concurrent lease that captures variables |
| [NAM1048](NAM1048.md) | error | A native owner inside `Process` state |
| [NAM1049](NAM1049.md) | error | `Process` state stored, boxed or returned |
| [NAM1050](NAM1050.md) | error | Outputs of a `Try...` call used without checking the result |
| [NAM1051](NAM1051.md) | error | A span used after its lease, arena or region ended |
| [NAM9001](NAM9001.md) | error | The bundled analyzer is missing from the build |
