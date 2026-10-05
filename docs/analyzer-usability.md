# Safe ownership correction

Every shipped diagnostic has a [dedicated help page](diagnostics/README.md).
The compiler analyzer remains independent of IDE workspace dependencies. The
package includes a separate exported code-fix companion for Roslyn hosts with
matching workspace support; compiler-only consumers still run the ownership gate.

The provider supports NAM1006, NAM1025, NAM1031 and NAM1038 only where one directly
constructed local owner can safely become a using declaration. It authenticates
the runtime type, rejects already-using/multiple/field declarations, reassignment,
ref/out/in consumption, captured locals, explicit lifecycle cleanup, await/yield and ambiguous
control transfer. It recompiles and re-runs NAM before offering the correction:
the requested defect must disappear without a compiler error or any new NAM
diagnostic. Budget values, payload, sharing and copying are unchanged.

```csharp
// Before: NAM1006, no proven lexical region.
NativeRegion region = new();

// After: only when the remaining body passes the semantic checks.
using NativeRegion region = new();
```

The cleanup scope retains the original declaration's enclosing block. Exceptions
and early returns receive normal C# using cleanup. The fix never adds sharing,
copies native payload, moves authority into storage, enlarges a cap or suppresses
analysis. If automatic cleanup conflicts with a later ownership operation, no
fix is offered. A source lookalike does not receive a runtime-type exemption.

Batch Fix All is intentionally unsupported: transformations need independent
semantic verification and a combined edit could alter another owner's lifetime.
Other diagnostics require explicit application decisions. In particular, NAM1050
cannot select the application's refusal path or safely guess that a result is
successful. Fix missing guards/ownership paths directly and rerun compilation.

See the [allocator-selection guide](allocator-selection.md) before adding NAM
risk to a workload. Passing ownership analysis is necessary, not performance
evidence, process-memory admission or proof of an unsafe external provider.
