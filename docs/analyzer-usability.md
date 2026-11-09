# Automatic fixes

Every diagnostic has a [help page](diagnostics/README.md). The package also ships a code-fix provider for editors that support Roslyn code fixes. The analyzer itself runs in every build, with or without the provider.

The provider offers "Use verified lexical cleanup" for four diagnostics:

| ID | Owner it fixes |
|---|---|
| [NAM1006](diagnostics/NAM1006.md) | `NativeRegion` |
| [NAM1025](diagnostics/NAM1025.md) | `NativeTransfer<T>` |
| [NAM1031](diagnostics/NAM1031.md) | `NativeBuilder<T>` |
| [NAM1038](diagnostics/NAM1038.md) | `NativeWorkspace<T>` |

```csharp
static int Count()
{
    NativeRegion region = new();                     // error NAM1006
    Span<int> values = region.Lease<int>(16, 0).AsSpan();
    values[0] = 1;
    return values.Length;
}
```

The fix turns the declaration into a using declaration:

```csharp
using NativeRegion region = new();
```

The fix is offered only when the owner is a local declared with an initializer and the rest of the body keeps compiling without any new NAM diagnostic. It is not offered if the local:

- is already a `using`, is declared with others, or is a field;
- is reassigned, passed as `ref`, `out` or `in`, or captured;
- is already disposed or completed by hand;
- lives in a block that contains `goto`, `await` or `yield`, or is changed with `++` or `--`;
- is consumed by `Move`, `ReturnTo...` or `LeaseFromMemory`.

The fix never adds sharing, copies data, stores an owner, raises a cap or suppresses a diagnostic. "Fix all" is not offered, because each owner needs its own check.

Every other diagnostic needs a decision from you. For example [NAM1050](diagnostics/NAM1050.md) cannot know what your code does when an acquisition is refused. Fix those by hand and rebuild.

Before adding NAM to a workload, read the [allocator-selection guide](allocator-selection.md). A clean analyzer run is necessary, not sufficient: it does not show NAM is faster for your workload.
