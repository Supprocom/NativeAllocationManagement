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
        "Using-owner lifecycle is automatic",
        "'{0}' cannot call '{1}' inside a using-owner scope. The generated cleanup owns this lifecycle boundary.");

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
        "NativeRegion '{0}' overlaps active region '{1}'. NativeRegion ownership is explicit, braced, and non-nesting.");

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

    internal static readonly DiagnosticDescriptor ScopedAcquisitionEscape = Create(
        "NAM1018",
        "Scoped acquisition must initialize a scoped local",
        "Scoped acquisition '{0}' must directly initialize a scoped local. It cannot be returned, passed, discarded, stored, aggregated, or hidden behind a helper.");

    internal static readonly DiagnosticDescriptor OrdinaryAcquisitionScopedWarning = Create(
        "NAM1019",
        "Ordinary native acquisition does not recycle scoped storage",
        "'{0}' is a scoped local initialized by an ordinary native acquisition. Use the matching scoped acquisition when scoped recycling is intended.",
        DiagnosticSeverity.Warning);

    internal static readonly DiagnosticDescriptor MissingScopedCompletion = Create(
        "NAM1020",
        "Scoped native storage is not recycled on every exit",
        "Scoped allocations from '{0}' leave this lexical region without a proven RecycleScoped completion. Place the same operation after the scope or in finally.",
        DiagnosticSeverity.Warning);

    internal static readonly DiagnosticDescriptor TransferAlias = Create(
        "NAM1021",
        "Transfer ownership cannot be copied",
        "Native transfer '{0}' owns transferable storage. Use NativeTransfer<T>.Move(ref source) instead of copying it to '{1}'.");

    internal static readonly DiagnosticDescriptor InactiveTransferUse = Create(
        "NAM1022",
        "Inactive transfer cannot be used",
        "Native transfer '{0}' cannot run '{1}' because its ownership is {2}.");

    internal static readonly DiagnosticDescriptor InvalidTransferMove = Create(
        "NAM1023",
        "Transfer move requires active ownership",
        "NativeTransfer<T>.Move cannot move '{0}' because its ownership is {1}.");

    internal static readonly DiagnosticDescriptor TransferViewEscape = Create(
        "NAM1024",
        "Native transfer view cannot escape",
        "Callback view '{0}' cannot escape through '{1}'. Use it only during the Access or Read callback.");

    internal static readonly DiagnosticDescriptor TransferLifetime = Create(
        "NAM1025",
        "Transfer ownership must end",
        "Native transfer '{0}' remains active at lexical exit. Dispose it or move ownership before exit.");

    internal static readonly DiagnosticDescriptor TransferAcquisitionEscape = Create(
        "NAM1026",
        "Transfer acquisition requires a local source",
        "'{0}' must initialize a local NativeTransfer<T>. Move that local before storage, return, or channel transfer.");

    internal static readonly DiagnosticDescriptor UnsupportedTransferParameter = Create(
        "NAM1027",
        "Transfer parameter must own its value",
        "Native transfer parameter '{0}' uses '{1}'. Use a by-value owner, and borrow only through Access or Read callback views.");

    internal static readonly DiagnosticDescriptor BuilderAlias = Create(
        "NAM1028",
        "Native builder ownership cannot be copied",
        "Native builder '{0}' owns unpublished storage. Keep one local binding instead of copying it to '{1}'.");

    internal static readonly DiagnosticDescriptor InactiveBuilderUse = Create(
        "NAM1029",
        "Inactive native builder cannot be used",
        "Native builder '{0}' cannot run '{1}' because its ownership is {2}.");

    internal static readonly DiagnosticDescriptor InvalidBuilderCompletion = Create(
        "NAM1030",
        "Native builder completion requires active ownership",
        "NativeBuilder<T>.Complete cannot complete '{0}' because its ownership is {1}.");

    internal static readonly DiagnosticDescriptor BuilderLifetime = Create(
        "NAM1031",
        "Native builder ownership must end",
        "Native builder '{0}' remains active at lexical exit. Complete or dispose it before exit.");

    internal static readonly DiagnosticDescriptor BuilderAcquisitionEscape = Create(
        "NAM1032",
        "Native builder acquisition requires one local owner",
        "'{0}' must directly initialize a local NativeBuilder<T>. It cannot be returned, passed, discarded, stored, or aggregated.");

    internal static readonly DiagnosticDescriptor UnsupportedBuilderParameter = Create(
        "NAM1033",
        "Native builder parameter is not permitted",
        "Native builder parameter '{0}' creates an ownership alias. Keep the builder in its declaring method and publish one NativeTransfer<T>.");

    internal static readonly DiagnosticDescriptor BuilderCompletionEscape = Create(
        "NAM1034",
        "Native builder completion requires a typed transfer destination",
        "NativeBuilder<T>.Complete for '{0}' must publish directly to an exact NativeTransfer<T> destination.");

    internal static readonly DiagnosticDescriptor WorkspaceAlias = Create(
        "NAM1036",
        "Native workspace ownership cannot be copied",
        "Native workspace '{0}' owns one persistent range. Keep one local binding instead of copying it to '{1}'.");

    internal static readonly DiagnosticDescriptor InactiveWorkspaceUse = Create(
        "NAM1037",
        "Native workspace operation requires active authority",
        "Native workspace '{0}' cannot run '{1}' because its authority is {2}.");

    internal static readonly DiagnosticDescriptor WorkspaceLifetime = Create(
        "NAM1038",
        "Native workspace ownership must end",
        "Native workspace '{0}' remains active at lexical exit. Dispose it before exit.");

    internal static readonly DiagnosticDescriptor WorkspaceAcquisitionEscape = Create(
        "NAM1039",
        "Native workspace acquisition requires one local owner",
        "'{0}' must directly initialize a local NativeWorkspace<T>. It cannot be returned, passed, discarded, stored, or aggregated.");

    internal static readonly DiagnosticDescriptor UnsupportedWorkspaceParameter = Create(
        "NAM1040",
        "Native workspace parameter must be a scoped read-only borrow",
        "Native workspace parameter '{0}' uses '{1}'. Use scoped in for a bounded borrow, or keep ownership in the declaring method.");

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
        ScopedAcquisitionEscape,
        OrdinaryAcquisitionScopedWarning,
        MissingScopedCompletion,
        TransferAlias,
        InactiveTransferUse,
        InvalidTransferMove,
        TransferViewEscape,
        TransferLifetime,
        TransferAcquisitionEscape,
        UnsupportedTransferParameter,
        BuilderAlias,
        InactiveBuilderUse,
        InvalidBuilderCompletion,
        BuilderLifetime,
        BuilderAcquisitionEscape,
        UnsupportedBuilderParameter,
        BuilderCompletionEscape,
        WorkspaceAlias,
        InactiveWorkspaceUse,
        WorkspaceLifetime,
        WorkspaceAcquisitionEscape,
        UnsupportedWorkspaceParameter,
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
