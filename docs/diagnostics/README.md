# Ownership diagnostics

Every enabled descriptor has a dedicated help page. IDs and severities are
maintained independently of the optional IDE cleanup provider.

| Diagnostic | Default severity | Rule |
| --- | --- | --- |
| [NAM1001](NAM1001.md) | error | Native owner alias is not permitted |
| [NAM1002](NAM1002.md) | error | Owner-derived handle cannot be copied |
| [NAM1003](NAM1003.md) | error | Native ownership must end before lexical exit |
| [NAM1004](NAM1004.md) | error | Native value belongs to a returned generation |
| [NAM1005](NAM1005.md) | error | Using-owner lifecycle is automatic |
| [NAM1006](NAM1006.md) | error | Region construction must be lexical |
| [NAM1007](NAM1007.md) | error | Native return has live generation state |
| [NAM1009](NAM1009.md) | error | Native owner lifecycle transition is invalid |
| [NAM1011](NAM1011.md) | error | Native value cannot cross an asynchronous boundary |
| [NAM1012](NAM1012.md) | error | Region-local value escapes its region |
| [NAM1013](NAM1013.md) | error | Pooled value escapes its owner |
| [NAM1015](NAM1015.md) | error | Deterministic field pool requires disposal |
| [NAM1016](NAM1016.md) | error | Pooled value crosses an unknown call |
| [NAM1017](NAM1017.md) | warning | Deferred pool return has live generation state |
| [NAM1018](NAM1018.md) | error | Scoped acquisition must initialize a scoped local |
| [NAM1019](NAM1019.md) | warning | Ordinary native acquisition does not recycle scoped storage |
| [NAM1020](NAM1020.md) | warning | Scoped native storage is not recycled on every exit |
| [NAM1021](NAM1021.md) | error | Transfer ownership cannot be copied |
| [NAM1022](NAM1022.md) | error | Inactive transfer cannot be used |
| [NAM1023](NAM1023.md) | error | Transfer move requires active ownership |
| [NAM1024](NAM1024.md) | error | Native transfer view cannot escape |
| [NAM1025](NAM1025.md) | error | Transfer ownership must end |
| [NAM1026](NAM1026.md) | error | Transfer acquisition requires a local source |
| [NAM1027](NAM1027.md) | error | Transfer parameter must own its value |
| [NAM1028](NAM1028.md) | error | Native builder ownership cannot be copied |
| [NAM1029](NAM1029.md) | error | Inactive native builder cannot be used |
| [NAM1030](NAM1030.md) | error | Native builder completion requires active ownership |
| [NAM1031](NAM1031.md) | error | Native builder ownership must end |
| [NAM1032](NAM1032.md) | error | Native builder acquisition requires one local owner |
| [NAM1033](NAM1033.md) | error | Native builder parameter is not permitted |
| [NAM1034](NAM1034.md) | error | Native builder completion requires a typed transfer destination |
| [NAM1035](NAM1035.md) | warning | NativeRegion scope has a managed allocation |
| [NAM1036](NAM1036.md) | error | Native workspace ownership cannot be copied |
| [NAM1037](NAM1037.md) | error | Native workspace operation requires active authority |
| [NAM1038](NAM1038.md) | error | Native workspace ownership must end |
| [NAM1039](NAM1039.md) | error | Native workspace acquisition requires one proven owner |
| [NAM1040](NAM1040.md) | error | Native workspace parameter must be a scoped read-only borrow |
| [NAM1050](NAM1050.md) | error | Prepared lease requires a successful acquisition guard |
| [NAM9001](NAM9001.md) | error | Bundled analyzer is required |

See [safe fixes](../analyzer-usability.md) and the
[managed-first allocator guide](../allocator-selection.md).
