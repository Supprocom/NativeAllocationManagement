using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Supprocom.NativeAllocationManagement.Analyzers;

/// <summary>[wording: NativeMappedLifetimeAnalyzer summary]</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NativeMappedLifetimeAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor UseAfterDispose = new(
        "NAM1053",
        "[wording: NAM1053 title]",
        "[wording: NAM1053 message] '{0}' after '{1}'",
        "Supprocom.NativeAllocationManagement",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "[wording: NAM1053 description]",
        helpLinkUri: "https://github.com/Supprocom/NativeAllocationManagement/blob/main/docs/diagnostics/NAM1053.md",
        customTags: WellKnownDiagnosticTags.Telemetry);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(UseAfterDispose);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(startContext =>
        {
            Symbols symbols = new(startContext.Compilation);
            if (symbols.IsAvailable)
            {
                startContext.RegisterOperationBlockAction(blockContext => AnalyzeBlock(blockContext, symbols));
            }
        });
    }

    private static void AnalyzeBlock(OperationBlockAnalysisContext context, Symbols symbols)
    {
        foreach (IOperation block in context.OperationBlocks)
        {
            if (!block.DescendantsAndSelf().Any(operation =>
                operation is IInvocationOperation invocation && symbols.IsViewCreation(invocation.TargetMethod)))
            {
                continue;
            }

            AnalyzeGraph(context.GetControlFlowGraph(block), symbols, context, []);
        }
    }

    private static void AnalyzeGraph(
        ControlFlowGraph graph,
        Symbols symbols,
        OperationBlockAnalysisContext context,
        HashSet<Location> reported)
    {
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
            new Walker(symbols, state, report: null).VisitBlock(blocks[ordinal]);
            foreach (ControlFlowBranch? branch in new[] { blocks[ordinal].FallThroughSuccessor, blocks[ordinal].ConditionalSuccessor })
            {
                if (branch?.Destination is not BasicBlock destination)
                {
                    continue;
                }

                // A using statement's implicit Dispose lives in a finally region on this branch.
                FlowState outgoing = state;
                if (!branch.FinallyRegions.IsEmpty)
                {
                    outgoing = state.Clone();
                    Walker finallyWalker = new(symbols, outgoing, report: null);
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

            new Walker(symbols, state.Clone(), (syntax, name, reason) =>
            {
                Location location = syntax.GetLocation();
                if (reported.Add(location))
                {
                    context.ReportDiagnostic(Diagnostic.Create(UseAfterDispose, location, name, reason));
                }
            }).VisitBlock(block);
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
        private readonly FlowState _state;
        private readonly Action<SyntaxNode, string, string>? _report;

        internal Walker(Symbols symbols, FlowState state, Action<SyntaxNode, string, string>? report)
        {
            _symbols = symbols;
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
                    when Symbols.IsSpan(target.Local.Type) || _symbols.IsView(target.Local.Type):
                    Visit(assignment.Value);
                    Assign(target.Local, assignment.Value);
                    return;
                case ILocalReferenceOperation reference
                    when _state.Facts.TryGetValue(reference.Local, out Fact? fact) && fact.EndedBy is not null:
                    _report?.Invoke(reference.Syntax, reference.Local.Name, fact.EndedBy);
                    return;
                case IInvocationOperation invocation:
                    // Dispose is idempotent, so disposing an ended view or file again is not a use.
                    if (invocation.Instance is not null && !_symbols.IsDispose(invocation.TargetMethod))
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

        private void Assign(ILocalSymbol local, IOperation value)
        {
            Fact? fact = Derive(value);
            if (fact is null)
            {
                _state.Facts.Remove(local);
            }
            else
            {
                _state.Facts[local] = fact;
            }
        }

        private Fact? Derive(IOperation value)
        {
            value = Unwrap(value);
            if (value is ILocalReferenceOperation other && _state.Facts.TryGetValue(other.Local, out Fact? copied))
            {
                return copied.EndedBy is null ? copied : null;
            }

            if (value is not IInvocationOperation invocation)
            {
                return null;
            }

            if (_symbols.IsViewCreation(invocation.TargetMethod))
            {
                return new Fact(null, SymbolOf(invocation.Instance), null);
            }

            if (_symbols.IsSpanAccessor(invocation.TargetMethod))
            {
                IOperation? receiver = invocation.Instance is null ? null : Unwrap(invocation.Instance);
                if (receiver is IInvocationOperation creation && _symbols.IsViewCreation(creation.TargetMethod))
                {
                    return new Fact(null, SymbolOf(creation.Instance), null);
                }

                ISymbol? view = SymbolOf(receiver);
                ISymbol? file = view is ILocalSymbol viewLocal && _state.Facts.TryGetValue(viewLocal, out Fact? viewFact)
                    ? viewFact.File
                    : null;
                return new Fact(view, file, null);
            }

            // Slices and conversions of a tracked span share its storage.
            if (Symbols.IsSpan(invocation.TargetMethod.ContainingType)
                && invocation.Instance is not null
                && Unwrap(invocation.Instance) is ILocalReferenceOperation source
                && _state.Facts.TryGetValue(source.Local, out Fact? sliced)
                && Symbols.IsSpan(invocation.Type))
            {
                return sliced;
            }

            if (invocation.TargetMethod.MethodKind == MethodKind.Conversion
                && invocation.Arguments.Length == 1
                && Unwrap(invocation.Arguments[0].Value) is ILocalReferenceOperation converted
                && _state.Facts.TryGetValue(converted.Local, out Fact? conversion))
            {
                return conversion;
            }

            return null;
        }

        private void ApplyEnd(IInvocationOperation invocation)
        {
            if (!_symbols.IsDispose(invocation.TargetMethod) || SymbolOf(invocation.Instance) is not ISymbol receiver)
            {
                return;
            }

            bool isView = _symbols.IsView(invocation.TargetMethod.ContainingType);
            string reason = receiver.Name + ".Dispose()";
            foreach (ILocalSymbol local in _state.Facts.Keys.ToArray())
            {
                Fact fact = _state.Facts[local];
                bool affected = isView
                    ? SymbolEqualityComparer.Default.Equals(local, receiver)
                        || (!_symbols.IsView(local.Type) && SymbolEqualityComparer.Default.Equals(fact.View, receiver))
                    : SymbolEqualityComparer.Default.Equals(fact.File, receiver);
                if (affected && fact.EndedBy is null)
                {
                    _state.Facts[local] = fact.WithEnd(reason);
                }
            }
        }
    }

    // A tracked local is a view (View is null, File is its file) or a span (View is its view, if known).
    private sealed class Fact : IEquatable<Fact>
    {
        internal Fact(ISymbol? view, ISymbol? file, string? endedBy)
        {
            View = view;
            File = file;
            EndedBy = endedBy;
        }

        internal ISymbol? View { get; }

        internal ISymbol? File { get; }

        internal string? EndedBy { get; }

        internal Fact WithEnd(string reason) => new(View, File, reason);

        public bool Equals(Fact? other) =>
            other is not null
            && SymbolEqualityComparer.Default.Equals(View, other.View)
            && SymbolEqualityComparer.Default.Equals(File, other.File)
            && string.Equals(EndedBy, other.EndedBy, StringComparison.Ordinal);

        public override bool Equals(object? obj) => Equals(obj as Fact);

        public override int GetHashCode() =>
            (SymbolEqualityComparer.Default.GetHashCode(View) * 397)
            ^ SymbolEqualityComparer.Default.GetHashCode(File);
    }

    private sealed class FlowState
    {
        internal Dictionary<ILocalSymbol, Fact> Facts { get; } = new(SymbolEqualityComparer.Default);

        internal FlowState Clone()
        {
            FlowState copy = new();
            foreach (KeyValuePair<ILocalSymbol, Fact> pair in Facts)
            {
                copy.Facts[pair.Key] = pair.Value;
            }

            return copy;
        }

        // May-analysis: a local ended on any incoming path stays ended.
        internal FlowState Merge(FlowState other)
        {
            FlowState merged = Clone();
            foreach (KeyValuePair<ILocalSymbol, Fact> pair in other.Facts)
            {
                if (!merged.Facts.TryGetValue(pair.Key, out Fact? existing) || (existing.EndedBy is null && pair.Value.EndedBy is not null))
                {
                    merged.Facts[pair.Key] = pair.Value;
                }
            }

            return merged;
        }

        internal bool SameAs(FlowState other) =>
            Facts.Count == other.Facts.Count
            && Facts.All(pair => other.Facts.TryGetValue(pair.Key, out Fact? fact) && Equals(fact, pair.Value));
    }

    private sealed class Symbols
    {
        private const string Namespace = "Supprocom.NativeAllocationManagement.";
        private readonly INamedTypeSymbol? _file;
        private readonly INamedTypeSymbol? _view;

        internal Symbols(Compilation compilation)
        {
            _file = compilation.GetTypeByMetadataName(Namespace + "NativeMappedFile");
            _view = _file?.ContainingAssembly.GetTypeByMetadataName(Namespace + "MappedView`1");
        }

        internal bool IsAvailable => _file is not null && _view is not null;

        internal bool IsView(ITypeSymbol? type) =>
            type is INamedTypeSymbol named && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, _view);

        internal bool IsFile(ITypeSymbol? type) =>
            type is INamedTypeSymbol named && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, _file);

        // Span may be visible through several reference assemblies; match its definition by name.
        internal static bool IsSpan(ITypeSymbol? type) =>
            type is INamedTypeSymbol named
            && named.OriginalDefinition.ToDisplayString() is "System.Span<T>" or "System.ReadOnlySpan<T>";

        internal bool IsViewCreation(IMethodSymbol method) =>
            string.Equals(method.Name, "View", StringComparison.Ordinal) && IsFile(method.ContainingType);

        internal bool IsSpanAccessor(IMethodSymbol method) =>
            method.Name is "AsSpan" or "AsReadOnlySpan" && IsView(method.ContainingType);

        internal bool IsDispose(IMethodSymbol method) =>
            string.Equals(method.Name, "Dispose", StringComparison.Ordinal)
            && (IsView(method.ContainingType) || IsFile(method.ContainingType));
    }
}
