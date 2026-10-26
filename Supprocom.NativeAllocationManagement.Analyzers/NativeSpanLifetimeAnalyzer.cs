using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Supprocom.NativeAllocationManagement.Analyzers;

/// <summary>Rejects lease spans used after their lease or owner ended the storage.</summary>
/// <remarks>
/// AsSpan validates once and returns a span without ownership. The compiler keeps the
/// span inside the lease's method; this flow analysis covers the remaining hazard inside
/// that method: a span local reachable after Dispose, Move, an owner Reset, a scoped
/// recycle or the end of the owning region or arena.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NativeSpanLifetimeAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor SpanAfterLeaseEnd = new(
        "NAM1051",
        "Lease span is used after its storage ended",
        "Span '{0}' comes from a NAM lease and is used after '{1}'. A span carries no ownership; take a new span from a current lease.",
        "Supprocom.NativeAllocationManagement",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Span '{0}' comes from a NAM lease and is used after '{1}'. A span carries no ownership; take a new span from a current lease.",
        helpLinkUri: "https://github.com/Supprocom/NativeAllocationManagement#ownership-diagnostics",
        customTags: WellKnownDiagnosticTags.Telemetry);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(SpanAfterLeaseEnd);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(startContext =>
        {
            Symbols symbols = new(startContext.Compilation);
            if (!symbols.IsAvailable)
            {
                return;
            }

            startContext.RegisterOperationBlockAction(blockContext => AnalyzeBlock(blockContext, symbols));
        });
    }

    private static void AnalyzeBlock(OperationBlockAnalysisContext context, Symbols symbols)
    {
        foreach (IOperation block in context.OperationBlocks)
        {
            if (!block.DescendantsAndSelf().Any(operation => symbols.IsLeaseAsSpan(operation)))
            {
                continue;
            }

            ControlFlowGraph graph = context.GetControlFlowGraph(block);
            HashSet<Location> reported = [];
            AnalyzeGraph(graph, symbols, context, reported);
        }
    }

    private static void AnalyzeGraph(
        ControlFlowGraph graph,
        Symbols symbols,
        OperationBlockAnalysisContext context,
        HashSet<Location> reported)
    {
        Dictionary<ISymbol, LeaseOrigin> leases = CollectLeaseOrigins(graph, symbols);
        ImmutableArray<BasicBlock> blocks = graph.Blocks;
        FlowState?[] entry = new FlowState?[blocks.Length];
        entry[0] = new FlowState();
        Queue<int> work = new();
        work.Enqueue(0);
        int budget = blocks.Length * 64;
        while (work.Count != 0 && budget-- > 0)
        {
            int ordinal = work.Dequeue();
            FlowState state = entry[ordinal]!.Clone();
            Walker walker = new(symbols, leases, state, report: null);
            walker.VisitBlock(blocks[ordinal]);
            foreach (ControlFlowBranch? branch in new[] { blocks[ordinal].FallThroughSuccessor, blocks[ordinal].ConditionalSuccessor })
            {
                if (branch?.Destination is not BasicBlock destination)
                {
                    continue;
                }

                // Finally regions on this branch run before control reaches the
                // destination; a using statement's implicit Dispose lives there.
                FlowState outgoing = state;
                if (!branch.FinallyRegions.IsEmpty)
                {
                    outgoing = state.Clone();
                    Walker finallyWalker = new(symbols, leases, outgoing, report: null);
                    foreach (ControlFlowRegion region in branch.FinallyRegions)
                    {
                        foreach (BasicBlock finallyBlock in blocks
                            .Skip(region.FirstBlockOrdinal)
                            .Take(region.LastBlockOrdinal - region.FirstBlockOrdinal + 1))
                        {
                            finallyWalker.VisitBlock(finallyBlock);
                        }
                    }
                }

                FlowState? previous = entry[destination.Ordinal];
                FlowState merged = previous is null ? outgoing.Clone() : previous.Merge(outgoing);
                if (previous is null || !merged.SameAs(previous))
                {
                    entry[destination.Ordinal] = merged;
                    work.Enqueue(destination.Ordinal);
                }
            }
        }

        foreach (BasicBlock block in blocks)
        {
            if (entry[block.Ordinal] is not FlowState state)
            {
                continue;
            }

            Walker walker = new(symbols, leases, state.Clone(), (syntax, span, reason) =>
            {
                Location location = syntax.GetLocation();
                if (reported.Add(location))
                {
                    context.ReportDiagnostic(Diagnostic.Create(SpanAfterLeaseEnd, location, span, reason));
                }
            });
            walker.VisitBlock(block);
        }

        foreach (IMethodSymbol localFunction in graph.LocalFunctions)
        {
            AnalyzeGraph(graph.GetLocalFunctionControlFlowGraph(localFunction), symbols, context, reported);
        }

        foreach (IFlowAnonymousFunctionOperation anonymous in blocks
            .SelectMany(block => block.Operations.Concat(block.BranchValue is null ? [] : [block.BranchValue]))
            .SelectMany(operation => operation.DescendantsAndSelf())
            .OfType<IFlowAnonymousFunctionOperation>())
        {
            AnalyzeGraph(graph.GetAnonymousFunctionControlFlowGraph(anonymous), symbols, context, reported);
        }
    }

    // Flow-insensitive: which owner produced each lease local, and whether it is scoped.
    private static Dictionary<ISymbol, LeaseOrigin> CollectLeaseOrigins(ControlFlowGraph graph, Symbols symbols)
    {
        Dictionary<ISymbol, LeaseOrigin> origins = new(SymbolEqualityComparer.Default);
        foreach (IOperation operation in graph.Blocks
            .SelectMany(block => block.Operations)
            .SelectMany(operation => operation.DescendantsAndSelf()))
        {
            if (operation is ISimpleAssignmentOperation { Target: ILocalReferenceOperation target } assignment
                && symbols.IsLease(target.Local.Type)
                && Unwrap(assignment.Value) is IInvocationOperation acquisition
                && symbols.IsAcquisition(acquisition.TargetMethod))
            {
                origins[target.Local] = new LeaseOrigin(
                    SymbolOf(acquisition.Instance),
                    acquisition.TargetMethod.Name.IndexOf("Scoped", StringComparison.Ordinal) >= 0);
            }
            else if (operation is IInvocationOperation tryAcquisition
                && symbols.IsAcquisition(tryAcquisition.TargetMethod))
            {
                foreach (IArgumentOperation argument in tryAcquisition.Arguments)
                {
                    if (argument.Parameter?.RefKind == RefKind.Out
                        && Unwrap(argument.Value) is ILocalReferenceOperation outLease
                        && symbols.IsLease(outLease.Local.Type))
                    {
                        origins[outLease.Local] = new LeaseOrigin(
                            SymbolOf(tryAcquisition.Instance),
                            tryAcquisition.TargetMethod.Name.IndexOf("Scoped", StringComparison.Ordinal) >= 0);
                    }
                }
            }
        }

        return origins;
    }

    private static IOperation Unwrap(IOperation operation)
    {
        while (operation is IConversionOperation conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
    }

    private static ISymbol? SymbolOf(IOperation? operation) => operation is null ? null : Unwrap(operation) switch
    {
        ILocalReferenceOperation local => local.Local,
        IParameterReferenceOperation parameter => parameter.Parameter,
        IFieldReferenceOperation field => field.Field,
        IPropertyReferenceOperation property => property.Property,
        _ => null
    };

    private sealed class Walker
    {
        private readonly Symbols _symbols;
        private readonly Dictionary<ISymbol, LeaseOrigin> _leases;
        private readonly FlowState _state;
        private readonly Action<SyntaxNode, string, string>? _report;

        internal Walker(
            Symbols symbols,
            Dictionary<ISymbol, LeaseOrigin> leases,
            FlowState state,
            Action<SyntaxNode, string, string>? report)
        {
            _symbols = symbols;
            _leases = leases;
            _state = state;
            _report = report;
        }

        internal void VisitBlock(BasicBlock block)
        {
            foreach (IOperation operation in block.Operations)
            {
                Visit(operation);
            }

            if (block.BranchValue is not null)
            {
                Visit(block.BranchValue);
            }
        }

        private void Visit(IOperation operation)
        {
            switch (operation)
            {
                case ISimpleAssignmentOperation { Target: ILocalReferenceOperation target } assignment
                    when Symbols.IsSpan(target.Local.Type):
                    Visit(assignment.Value);
                    Assign(target.Local, assignment.Value);
                    return;
                case ILocalReferenceOperation reference
                    when _state.Spans.TryGetValue(reference.Local, out SpanFact? fact) && fact.EndedBy is not null:
                    _report?.Invoke(reference.Syntax, reference.Local.Name, fact.EndedBy);
                    return;
                case IInvocationOperation invocation:
                    if (invocation.Instance is not null)
                    {
                        Visit(invocation.Instance);
                    }

                    foreach (IArgumentOperation argument in invocation.Arguments)
                    {
                        Visit(argument);
                    }

                    ApplyEnd(invocation);
                    return;
            }

            foreach (IOperation child in operation.ChildOperations)
            {
                Visit(child);
            }
        }

        private void Assign(ILocalSymbol span, IOperation value)
        {
            SpanFact? fact = Derive(value);
            if (fact is null)
            {
                _state.Spans.Remove(span);
            }
            else
            {
                _state.Spans[span] = fact;
            }
        }

        private SpanFact? Derive(IOperation value)
        {
            value = Unwrap(value);
            if (value is ILocalReferenceOperation other && _state.Spans.TryGetValue(other.Local, out SpanFact? copied))
            {
                return copied;
            }

            if (value is not IInvocationOperation invocation)
            {
                return null;
            }

            if (_symbols.IsLeaseAsSpan(invocation))
            {
                IOperation? receiver = invocation.Instance is null ? null : Unwrap(invocation.Instance);
                if (receiver is IInvocationOperation acquisition && _symbols.IsAcquisition(acquisition.TargetMethod))
                {
                    return new SpanFact(null, SymbolOf(acquisition.Instance), acquisition.TargetMethod.Name.IndexOf("Scoped", StringComparison.Ordinal) >= 0, null);
                }

                ISymbol? lease = SymbolOf(receiver);
                LeaseOrigin? origin = lease is not null && _leases.TryGetValue(lease, out LeaseOrigin? known) ? known : null;
                return new SpanFact(lease, origin?.Owner, origin?.Scoped ?? false, null);
            }

            // Slices and conversions of a tracked span share its storage.
            if (Symbols.IsSpan(invocation.TargetMethod.ContainingType)
                && invocation.Instance is not null
                && Unwrap(invocation.Instance) is ILocalReferenceOperation source
                && _state.Spans.TryGetValue(source.Local, out SpanFact? sliced)
                && Symbols.IsSpan(invocation.Type))
            {
                return sliced;
            }

            if (invocation.TargetMethod.MethodKind == MethodKind.Conversion
                && invocation.Arguments.Length == 1
                && Unwrap(invocation.Arguments[0].Value) is ILocalReferenceOperation converted
                && _state.Spans.TryGetValue(converted.Local, out SpanFact? conversion))
            {
                return conversion;
            }

            return null;
        }

        private void ApplyEnd(IInvocationOperation invocation)
        {
            string name = invocation.TargetMethod.Name;
            ISymbol? receiver = SymbolOf(invocation.Instance);
            if (string.Equals(name, "Move", StringComparison.Ordinal)
                && invocation.Instance is null
                && _symbols.IsLease(invocation.TargetMethod.ContainingType))
            {
                foreach (IArgumentOperation argument in invocation.Arguments)
                {
                    if (argument.Parameter?.RefKind == RefKind.Ref && SymbolOf(argument.Value) is ISymbol moved)
                    {
                        End(fact => SymbolEqualityComparer.Default.Equals(fact.Lease, moved), moved.Name + " was moved");
                    }
                }

                return;
            }

            if (receiver is null)
            {
                return;
            }

            if (string.Equals(name, "Dispose", StringComparison.Ordinal) && _symbols.IsLease(invocation.TargetMethod.ContainingType))
            {
                End(fact => SymbolEqualityComparer.Default.Equals(fact.Lease, receiver), receiver.Name + ".Dispose()");
            }
            else if (_symbols.IsBumpOwner(invocation.TargetMethod.ContainingType))
            {
                if (name is "Reset" or "Dispose")
                {
                    End(fact => SymbolEqualityComparer.Default.Equals(fact.Owner, receiver), receiver.Name + "." + name + "()");
                }
                else if (string.Equals(name, "RecycleScoped", StringComparison.Ordinal))
                {
                    End(fact => fact.Scoped && SymbolEqualityComparer.Default.Equals(fact.Owner, receiver), receiver.Name + ".RecycleScoped()");
                }
            }
        }

        private void End(Func<SpanFact, bool> affected, string reason)
        {
            foreach (ILocalSymbol span in _state.Spans.Keys.ToArray())
            {
                SpanFact fact = _state.Spans[span];
                if (fact.EndedBy is null && affected(fact))
                {
                    _state.Spans[span] = fact.WithEnd(reason);
                }
            }
        }
    }

    private sealed class LeaseOrigin
    {
        internal LeaseOrigin(ISymbol? owner, bool scoped)
        {
            Owner = owner;
            Scoped = scoped;
        }

        internal ISymbol? Owner { get; }

        internal bool Scoped { get; }
    }

    private sealed class SpanFact : IEquatable<SpanFact>
    {
        internal SpanFact(ISymbol? lease, ISymbol? owner, bool scoped, string? endedBy)
        {
            Lease = lease;
            Owner = owner;
            Scoped = scoped;
            EndedBy = endedBy;
        }

        internal ISymbol? Lease { get; }

        internal ISymbol? Owner { get; }

        internal bool Scoped { get; }

        internal string? EndedBy { get; }

        internal SpanFact WithEnd(string reason) => new(Lease, Owner, Scoped, reason);

        public bool Equals(SpanFact? other) =>
            other is not null
            && SymbolEqualityComparer.Default.Equals(Lease, other.Lease)
            && SymbolEqualityComparer.Default.Equals(Owner, other.Owner)
            && Scoped == other.Scoped
            && string.Equals(EndedBy, other.EndedBy, StringComparison.Ordinal);

        public override bool Equals(object? obj) => Equals(obj as SpanFact);

        public override int GetHashCode() =>
            (SymbolEqualityComparer.Default.GetHashCode(Lease) * 397)
            ^ SymbolEqualityComparer.Default.GetHashCode(Owner)
            ^ (Scoped ? 1 : 0);
    }

    private sealed class FlowState
    {
        internal Dictionary<ILocalSymbol, SpanFact> Spans { get; } = new(SymbolEqualityComparer.Default);

        internal FlowState Clone()
        {
            FlowState copy = new();
            foreach (KeyValuePair<ILocalSymbol, SpanFact> pair in Spans)
            {
                copy.Spans[pair.Key] = pair.Value;
            }

            return copy;
        }

        // May-analysis: a span ended on any incoming path stays ended.
        internal FlowState Merge(FlowState other)
        {
            FlowState merged = Clone();
            foreach (KeyValuePair<ILocalSymbol, SpanFact> pair in other.Spans)
            {
                if (!merged.Spans.TryGetValue(pair.Key, out SpanFact? existing) || (existing.EndedBy is null && pair.Value.EndedBy is not null))
                {
                    merged.Spans[pair.Key] = pair.Value;
                }
            }

            return merged;
        }

        internal bool SameAs(FlowState other) =>
            Spans.Count == other.Spans.Count
            && Spans.All(pair => other.Spans.TryGetValue(pair.Key, out SpanFact? fact) && Equals(fact, pair.Value));
    }

    private sealed class Symbols
    {
        private const string Namespace = "Supprocom.NativeAllocationManagement.";
        private readonly INamedTypeSymbol? _pooled;
        private readonly INamedTypeSymbol? _preparedPooled;
        private readonly INamedTypeSymbol? _arenaLease;
        private readonly INamedTypeSymbol? _local;
        private readonly INamedTypeSymbol? _arena;
        private readonly INamedTypeSymbol? _region;

        internal Symbols(Compilation compilation)
        {
            _pooled = compilation.GetTypeByMetadataName(Namespace + "Pooled`1");
            IAssemblySymbol? runtime = _pooled?.ContainingAssembly;
            if (runtime is null)
            {
                return;
            }

            _preparedPooled = runtime.GetTypeByMetadataName(Namespace + "PreparedPooled`1");
            _arenaLease = runtime.GetTypeByMetadataName(Namespace + "ArenaLease`1");
            _local = runtime.GetTypeByMetadataName(Namespace + "Local`1");
            _arena = runtime.GetTypeByMetadataName(Namespace + "NativeArena");
            _region = runtime.GetTypeByMetadataName(Namespace + "NativeRegion");
        }

        internal bool IsAvailable => _pooled is not null;

        internal bool IsLease(ITypeSymbol? type) =>
            Is(type, _pooled) || Is(type, _preparedPooled) || Is(type, _arenaLease) || Is(type, _local);

        internal bool IsBumpOwner(ITypeSymbol? type) => Is(type, _arena) || Is(type, _region);

        // Span may be visible through several reference assemblies; match its definition by name.
        internal static bool IsSpan(ITypeSymbol? type) =>
            type is INamedTypeSymbol named
            && named.OriginalDefinition.ToDisplayString() is "System.Span<T>" or "System.ReadOnlySpan<T>";

        internal bool IsLeaseAsSpan(IOperation operation) =>
            operation is IInvocationOperation invocation
            && string.Equals(invocation.TargetMethod.Name, "AsSpan", StringComparison.Ordinal)
            && IsLease(invocation.TargetMethod.ContainingType);

        internal bool IsAcquisition(IMethodSymbol method) =>
            method.Name is "Rent" or "TryRent" or "Scratch" or "ScratchScoped" or "TryScratch" or "TryScratchScoped" or "Lease"
            && IsLease(method.ReturnType is INamedTypeSymbol { SpecialType: SpecialType.System_Boolean }
                ? method.Parameters.LastOrDefault(parameter => parameter.RefKind == RefKind.Out && IsLease(parameter.Type))?.Type
                : method.ReturnType);

        private static bool Is(ITypeSymbol? candidate, INamedTypeSymbol? expected) =>
            expected is not null
            && candidate is INamedTypeSymbol named
            && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, expected);
    }
}
