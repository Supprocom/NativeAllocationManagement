using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Analyzers;

internal static class NativeAllocationDiagnosticDescriptors
{
    internal static readonly DiagnosticDescriptor OwnerAlias = Create(
        "NAM1001",
        "Native owner alias is not permitted",
        "Native owner '{0}' cannot be aliased to '{1}'. A local owner has one ownership binding; retain field ownership only through the declaring field.");

    internal static readonly DiagnosticDescriptor HandleAlias = Create(
        "NAM1002",
        "Owner-derived handle cannot be copied",
        "Owner-derived value '{0}' cannot be copied to '{1}'. Use its bounded Access or Read callback instead of creating a second owner-shaped name.");

    internal static readonly DiagnosticDescriptor LifetimeEscape = Create(
        "NAM1003",
        "Native ownership must end before lexical exit",
        "Native value '{0}' remains active when its lexical lifetime ends. Dispose the pooled lease or complete a whole-generation return on every exit path.");

    internal static readonly DiagnosticDescriptor ReturnedHandle = Create(
        "NAM1004",
        "Native value belongs to a returned generation",
        "Native value '{0}' belongs to a returned generation and cannot be used by '{1}'. A later LeaseFromMemory never revives old values.");

    internal static readonly DiagnosticDescriptor ScopedLifecycle = Create(
        "NAM1005",
        "Scoped pool lifecycle is automatic",
        "'{0}' cannot call '{1}' inside a using pool scope. The generated cleanup owns this lifecycle boundary.");

    internal static readonly DiagnosticDescriptor RegionMustBeUsing = Create(
        "NAM1006",
        "Region construction must be lexical",
        "NativeRegion '{0}' must be the direct resource of a braced using statement. A region is a non-transferable lexical allocation boundary.");

    internal static readonly DiagnosticDescriptor GenerationReturnLiveValue = Create(
        "NAM1007",
        "Native return has live generation state",
        "{0}.{1} cannot end the current native generation while '{2}' remains live. {3}");

    internal static readonly DiagnosticDescriptor InvalidLifecycle = Create(
        "NAM1009",
        "Native owner lifecycle transition is invalid",
        "'{0}' cannot call '{1}' in its current generation state. Return a current generation before LeaseFromMemory and do not use returned values.");

    internal static readonly DiagnosticDescriptor NestedRegion = Create(
        "NAM1010",
        "Nested NativeRegion is not permitted",
        "NativeRegion '{0}' overlaps active region '{1}'. PAC# regions are unnamed, non-nesting lexical owners.");

    internal static readonly DiagnosticDescriptor AcrossAsync = Create(
        "NAM1011",
        "Native value cannot cross an asynchronous boundary",
        "Native value '{0}' cannot remain active across await or yield. Finish the bounded operation before the continuation boundary.");

    internal static readonly DiagnosticDescriptor LocalEscape = Create(
        "NAM1012",
        "Region-local value escapes its region",
        "Region-local value '{0}' cannot be assigned to '{1}'. The destination can outlive the NativeRegion; copy the required data into independently owned managed storage.");

    internal static readonly DiagnosticDescriptor PooledEscape = Create(
        "NAM1013",
        "Pooled value escapes its owner",
        "Pooled value '{0}' cannot escape to '{1}'. Keep the root lexical and use Access or Read for bounded helper calls.");

    internal static readonly DiagnosticDescriptor FieldDisposal = Create(
        "NAM1015",
        "Deterministic field pool requires disposal",
        "Field pool '{0}' uses ToNativeMemory but its containing type does not expose a recognized IDisposable release path for that exact field.");

    internal static readonly DiagnosticDescriptor UnknownCall = Create(
        "NAM1016",
        "Pooled value crosses an unknown call",
        "Pooled value '{0}' cannot cross call '{1}'. The called API does not prove that it is non-retaining; pass copied managed data or invoke a span-based helper inside Access or Read.");

    internal static readonly DiagnosticDescriptor DeferredReturnLiveValue = Create(
        "NAM1017",
        "Deferred pool return has live generation state",
        "{0}.{1} ends its current pool generation while '{2}' is still live. {3}",
        DiagnosticSeverity.Warning);

    internal static readonly DiagnosticDescriptor AnalyzerMissing = Create(
        "NAM9001",
        "Bundled analyzer is required",
        "Supprocom.NativeAllocationManagement requires its bundled analyzer asset. Restore the package with analyzer assets enabled.");

    internal static ImmutableArray<DiagnosticDescriptor> All { get; } = ImmutableArray.Create(
        OwnerAlias,
        HandleAlias,
        LifetimeEscape,
        ReturnedHandle,
        ScopedLifecycle,
        RegionMustBeUsing,
        GenerationReturnLiveValue,
        InvalidLifecycle,
        NestedRegion,
        AcrossAsync,
        LocalEscape,
        PooledEscape,
        FieldDisposal,
        UnknownCall,
        DeferredReturnLiveValue,
        AnalyzerMissing);

    private static DiagnosticDescriptor Create(
        string id,
        string title,
        string message,
        DiagnosticSeverity severity = DiagnosticSeverity.Error)
    {
        return new DiagnosticDescriptor(
            id,
            title,
            message,
            "Supprocom.NativeAllocationManagement",
            severity,
            isEnabledByDefault: true,
            description: message,
            helpLinkUri: "https://github.com/Supprocom/NativeAllocationManagement#ownership-diagnostics",
            customTags: WellKnownDiagnosticTags.Telemetry);
    }
}
