## Release 0.2.2

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
NAM1001 | Supprocom.NativeAllocationManagement | Error | Native owner alias is not permitted
NAM1002 | Supprocom.NativeAllocationManagement | Error | Owner-derived handle cannot be copied
NAM1003 | Supprocom.NativeAllocationManagement | Error | Native ownership must end before lexical exit
NAM1004 | Supprocom.NativeAllocationManagement | Error | Native value belongs to a returned generation
NAM1005 | Supprocom.NativeAllocationManagement | Error | Using-owner lifecycle is automatic
NAM1006 | Supprocom.NativeAllocationManagement | Error | Region construction must be lexical
NAM1007 | Supprocom.NativeAllocationManagement | Error | Native return has live generation state
NAM1009 | Supprocom.NativeAllocationManagement | Error | Native owner lifecycle transition is invalid
NAM1011 | Supprocom.NativeAllocationManagement | Error | Native value cannot cross an asynchronous boundary
NAM1012 | Supprocom.NativeAllocationManagement | Error | Region-local value escapes its region
NAM1013 | Supprocom.NativeAllocationManagement | Error | Pooled value escapes its owner
NAM1015 | Supprocom.NativeAllocationManagement | Error | Deterministic field pool requires disposal
NAM1016 | Supprocom.NativeAllocationManagement | Error | Pooled value crosses an unknown call
NAM1017 | Supprocom.NativeAllocationManagement | Warning | Deferred pool return has live generation state
NAM1018 | Supprocom.NativeAllocationManagement | Error | Scoped acquisition must initialize a scoped local
NAM1019 | Supprocom.NativeAllocationManagement | Warning | Ordinary native acquisition does not recycle scoped storage
NAM1020 | Supprocom.NativeAllocationManagement | Warning | Scoped native storage is not recycled on every exit
NAM1021 | Supprocom.NativeAllocationManagement | Error | Transfer ownership cannot be copied
NAM1022 | Supprocom.NativeAllocationManagement | Error | Inactive transfer cannot be used
NAM1023 | Supprocom.NativeAllocationManagement | Error | Transfer move requires active ownership
NAM1024 | Supprocom.NativeAllocationManagement | Error | Native transfer view cannot escape
NAM1025 | Supprocom.NativeAllocationManagement | Error | Transfer ownership must end
NAM1026 | Supprocom.NativeAllocationManagement | Error | Transfer acquisition requires a local source
NAM1027 | Supprocom.NativeAllocationManagement | Error | Transfer parameter must own its value
NAM1028 | Supprocom.NativeAllocationManagement | Error | Native builder ownership cannot be copied
NAM1029 | Supprocom.NativeAllocationManagement | Error | Inactive native builder cannot be used
NAM1030 | Supprocom.NativeAllocationManagement | Error | Native builder completion requires active ownership
NAM1031 | Supprocom.NativeAllocationManagement | Error | Native builder ownership must end
NAM1032 | Supprocom.NativeAllocationManagement | Error | Native builder acquisition requires one local owner
NAM1033 | Supprocom.NativeAllocationManagement | Error | Native builder parameter is not permitted
NAM1034 | Supprocom.NativeAllocationManagement | Error | Native builder completion requires a typed transfer destination
NAM1035 | Supprocom.NativeAllocationManagement | Warning | NativeRegion scope has a managed allocation
NAM1036 | Supprocom.NativeAllocationManagement | Error | Native workspace ownership cannot be copied
NAM1037 | Supprocom.NativeAllocationManagement | Error | Native workspace operation requires active authority
NAM1038 | Supprocom.NativeAllocationManagement | Error | Native workspace ownership must end
NAM1039 | Supprocom.NativeAllocationManagement | Error | Native workspace acquisition requires one proven owner
NAM1040 | Supprocom.NativeAllocationManagement | Error | Native workspace parameter must be a scoped read-only borrow
NAM1041 | Supprocom.NativeAllocationManagement | Error | Native builder write view cannot escape
NAM1042 | Supprocom.NativeAllocationManagement | Error | Native builder write authority must remain direct
NAM1043 | Supprocom.NativeAllocationManagement | Error | Exclusive native builder borrow cannot escape
NAM1044 | Supprocom.NativeAllocationManagement | Error | Exclusive native builder borrow requires scoped ref authority
NAM1045 | Supprocom.NativeAllocationManagement | Error | Native builder write state cannot escape
NAM1046 | Supprocom.NativeAllocationManagement | Error | Native builder write state requires direct authority
NAM1047 | Supprocom.NativeAllocationManagement | Error | Concurrent lease callback requires direct authority
NAM1048 | Supprocom.NativeAllocationManagement | Error | Concurrent lease state cannot contain native ownership
NAM1049 | Supprocom.NativeAllocationManagement | Error | Concurrent lease state cannot escape
NAM9001 | Supprocom.NativeAllocationManagement | Error | Bundled analyzer is required
