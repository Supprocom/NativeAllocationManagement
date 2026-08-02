using System.Collections.Immutable;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Supprocom.NativeAllocationManagement.Analyzers;

/// <summary>
/// Enforces the source-visible ownership and lifecycle rules for native pool and region values.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NativeAllocationAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => NativeAllocationDiagnosticDescriptors.All;

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(startContext =>
        {
            NativeSymbols symbols = new(startContext.Compilation);
            if (!symbols.IsAvailable)
            {
                return;
            }

            startContext.RegisterOperationBlockAction(blockContext =>
            {
                blockContext.CancellationToken.ThrowIfCancellationRequested();
                MethodFlowAnalyzer analyzer = new(blockContext, symbols);
                foreach (IOperation operationBlock in blockContext.OperationBlocks)
                {
                    analyzer.AnalyzeOperationBlock(operationBlock);
                }

                analyzer.Complete();
            });
            startContext.RegisterOperationAction(operationContext =>
            {
                if (operationContext.Operation is not IAnonymousFunctionOperation anonymous)
                {
                    return;
                }

                MethodFlowAnalyzer analyzer = new(operationContext, symbols);
                analyzer.AnalyzeOperationBlock(anonymous.Body);
                analyzer.Complete();
            }, OperationKind.AnonymousFunction);
        });
    }

    private sealed class NativeSymbols
    {
        private const string Namespace =
            "Supprocom.NativeAllocationManagement.";

        internal NativeSymbols(Compilation compilation)
        {
            Pool = compilation.GetTypeByMetadataName(
                Namespace + "NativePool`1");
            IAssemblySymbol? runtimeAssembly = Pool?.ContainingAssembly;
            if (runtimeAssembly is null)
            {
                return;
            }

            Region = runtimeAssembly.GetTypeByMetadataName(
                Namespace + "NativeRegion");
            Arena = runtimeAssembly.GetTypeByMetadataName(
                Namespace + "NativeArena");
            Pooled = runtimeAssembly.GetTypeByMetadataName(
                Namespace + "Pooled`1");
            Local = runtimeAssembly.GetTypeByMetadataName(
                Namespace + "Local`1");
            ArenaLease = runtimeAssembly.GetTypeByMetadataName(
                Namespace + "ArenaLease`1");
            Transfer = runtimeAssembly.GetTypeByMetadataName(
                Namespace + "NativeTransfer`1");
            LeaseView = runtimeAssembly.GetTypeByMetadataName(
                Namespace + "NativeLeaseView`1");
        }

        internal INamedTypeSymbol? Pool { get; }

        internal INamedTypeSymbol? Region { get; }

        internal INamedTypeSymbol? Arena { get; }

        internal INamedTypeSymbol? Pooled { get; }

        internal INamedTypeSymbol? Local { get; }

        internal INamedTypeSymbol? ArenaLease { get; }

        internal INamedTypeSymbol? Transfer { get; }

        internal INamedTypeSymbol? LeaseView { get; }

        internal bool IsAvailable =>
            Pool is not null
            && Region is not null
            && Arena is not null
            && Pooled is not null
            && Local is not null
            && ArenaLease is not null
            && Transfer is not null
            && LeaseView is not null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool Is(
            ITypeSymbol? candidate,
            INamedTypeSymbol? expected)
        {
            return candidate is INamedTypeSymbol named
                && SymbolEqualityComparer.Default.Equals(
                    named.OriginalDefinition,
                    expected);
        }
    }

    private interface IFlowAnalysisContext
    {
        Compilation Compilation { get; }

        CancellationToken CancellationToken { get; }

        ISymbol? OwningSymbol { get; }

        void ReportDiagnostic(Diagnostic diagnostic);
    }

    private sealed class OperationBlockContextAdapter : IFlowAnalysisContext
    {
        private readonly OperationBlockAnalysisContext _context;

        internal OperationBlockContextAdapter(OperationBlockAnalysisContext context)
        {
            _context = context;
        }

        public Compilation Compilation => _context.Compilation;

        public CancellationToken CancellationToken => _context.CancellationToken;

        public ISymbol? OwningSymbol => _context.OwningSymbol;

        public void ReportDiagnostic(Diagnostic diagnostic) => _context.ReportDiagnostic(diagnostic);
    }

    private sealed class OperationContextAdapter : IFlowAnalysisContext
    {
        private readonly OperationAnalysisContext _context;

        internal OperationContextAdapter(OperationAnalysisContext context)
        {
            _context = context;
        }

        public Compilation Compilation => _context.Compilation;

        public CancellationToken CancellationToken => _context.CancellationToken;

        public ISymbol? OwningSymbol => _context.ContainingSymbol;

        public void ReportDiagnostic(Diagnostic diagnostic) => _context.ReportDiagnostic(diagnostic);
    }

    private sealed class MethodFlowAnalyzer : OperationWalker
    {
        private readonly IFlowAnalysisContext _context;
        private readonly NativeSymbols _symbols;
        private readonly Dictionary<ISymbol, OwnerState> _owners = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<ISymbol, HandleState> _handles = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<ISymbol, TransferState> _transfers = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<string, LifecycleEffect> _lifecycleSummaries = new(StringComparer.Ordinal);
        private readonly HashSet<string> _lifecycleSummaryVisiting = new(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> _nonRetainingOwnerParameters =
            new(StringComparer.Ordinal);
        private readonly HashSet<string> _nonRetainingOwnerParameterVisiting =
            new(StringComparer.Ordinal);
        private readonly List<RegionScope> _regions = [];
        private readonly HashSet<OwnerState> _borrowedOwners = [];
        private readonly List<(ISymbol? OwnerSymbol, string HandleName)> _borrowScopes = [];
        private readonly Dictionary<ISymbol, ControlFlowRegion> _localLifetimeRegions = new(SymbolEqualityComparer.Default);
        private readonly HashSet<ISymbol> _usingResourceSymbols = new(SymbolEqualityComparer.Default);
        private readonly List<FlowSnapshot> _exitSnapshots = [];
        private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
        private readonly HashSet<ISymbol> _reportedScopedCompletions = new(SymbolEqualityComparer.Default);
        private readonly HashSet<ISymbol> _reportedRegionParameters = new(SymbolEqualityComparer.Default);
        private readonly HashSet<IInvocationOperation> _preprocessedTransferInvocations = [];
        private readonly Dictionary<ControlFlowRegion, List<FinallyAnalysisCacheEntry>>
            _finallyAnalysisCache = new();
        private readonly HashSet<int> _ownershipRelevantBlocks = [];
        private int _closureDepth;
        private int _finallyProtectionDepth;
        private int _finallyDepth;
        private bool _cfgMode;
        private bool _suppressDiagnostics;
        private SyntaxTree? _analysisRootTree;
        private TextSpan _analysisRootSpan;
        private bool _hasAnalysisRoot;
        private bool _isStandaloneClosureAnalysis;

        internal MethodFlowAnalyzer(
            OperationBlockAnalysisContext context,
            NativeSymbols symbols)
        {
            _context = new OperationBlockContextAdapter(context);
            _symbols = symbols;
        }

        internal MethodFlowAnalyzer(
            OperationAnalysisContext context,
            NativeSymbols symbols)
        {
            _context = new OperationContextAdapter(context);
            _symbols = symbols;
        }

        internal void AnalyzeOperationBlock(IOperation operationBlock)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            _analysisRootTree = operationBlock.Syntax.SyntaxTree;
            _analysisRootSpan = operationBlock.Syntax.Span;
            _hasAnalysisRoot = true;
            _isStandaloneClosureAnalysis = operationBlock.Parent is IAnonymousFunctionOperation;
            ReportRegionParameters();
            RegisterOwnerParameters();
            RegisterTransferParameters();
            if (!ContainsNativeOwnership(operationBlock))
            {
                return;
            }

            if (operationBlock is IMethodBodyOperation methodBody)
            {
                AnalyzeControlFlowGraph(ControlFlowGraph.Create(methodBody, _context.CancellationToken));
                return;
            }

            if (operationBlock.Parent is IMethodBodyOperation parentMethodBody)
            {
                AnalyzeControlFlowGraph(ControlFlowGraph.Create(parentMethodBody, _context.CancellationToken));
                return;
            }

            if (operationBlock is IBlockOperation block && block.Parent is null)
            {
                AnalyzeControlFlowGraph(ControlFlowGraph.Create(block, _context.CancellationToken));
                return;
            }

            _cfgMode = false;
            Visit(operationBlock);
            _exitSnapshots.Add(CaptureSnapshot());
        }

        private void RegisterOwnerParameters()
        {
            if (_context.OwningSymbol is not IMethodSymbol method)
            {
                return;
            }

            foreach (IParameterSymbol parameter in method.Parameters
                .Where(parameter => IsOwnerType(parameter.Type)))
            {
                if (_owners.ContainsKey(parameter))
                {
                    continue;
                }

                SyntaxNode syntax = parameter.DeclaringSyntaxReferences
                    .FirstOrDefault()
                    ?.GetSyntax(_context.CancellationToken)
                    ?? method.DeclaringSyntaxReferences
                        .First()
                        .GetSyntax(_context.CancellationToken);
                OwnerState owner = new(
                    parameter,
                    parameter.Type,
                    IsNativeRegion(parameter.Type),
                    IsNativeArena(parameter.Type),
                    isUsing: false,
                    isField: false,
                    requiresDeterministicReturn: false,
                    syntax)
                {
                    IsExternalReceiver = true,
                    ScopedOwnerEligible = false
                };
                _owners.Add(parameter, owner);
            }
        }

        private bool ContainsNativeOwnership(IOperation operationBlock)
        {
            return operationBlock.DescendantsAndSelf()
                .Any(operation => IsOwnerType(operation.Type)
                    || IsHandleType(operation.Type)
                    || IsNativeTransfer(operation.Type));
        }

        private void RegisterTransferParameters()
        {
            if (_context.OwningSymbol is not IMethodSymbol method)
            {
                return;
            }

            foreach (IParameterSymbol parameter in method.Parameters
                .Where(parameter => IsNativeTransfer(parameter.Type)))
            {
                if (_transfers.ContainsKey(parameter))
                {
                    continue;
                }

                SyntaxNode syntax = parameter.DeclaringSyntaxReferences
                    .FirstOrDefault()
                    ?.GetSyntax(_context.CancellationToken)
                    ?? method.DeclaringSyntaxReferences
                        .First()
                        .GetSyntax(_context.CancellationToken);
                _transfers.Add(
                    parameter,
                    TransferState.CreateExternal(
                        parameter,
                        syntax));
            }
        }

        private void ReportRegionParameters()
        {
            if (_context.OwningSymbol is not IMethodSymbol method)
            {
                return;
            }

            foreach (IParameterSymbol parameter in method.Parameters.Where(parameter => IsNativeRegion(parameter.Type)))
            {
                if (!_reportedRegionParameters.Add(parameter))
                {
                    continue;
                }

                SyntaxNode syntax = parameter.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(_context.CancellationToken)
                    ?? method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(_context.CancellationToken)
                    ?? throw new InvalidOperationException("The NativeRegion parameter has no source declaration.");
                Report(NativeAllocationDiagnosticDescriptors.RegionMustBeUsing, syntax, parameter.Name);
            }
        }

        internal void Complete()
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            IEnumerable<FlowSnapshot> exits = _exitSnapshots.Count == 0
                ? [CaptureSnapshot()]
                : _exitSnapshots;

            foreach (FlowSnapshot exit in exits)
            {
                _context.CancellationToken.ThrowIfCancellationRequested();
                foreach (TransferState transfer in exit.Transfers.Values)
                {
                    if (!transfer.MustEnd
                        || transfer.IsUsing
                        || transfer.Status is TransferStatus.Moved or TransferStatus.Disposed or TransferStatus.Unowned)
                    {
                        continue;
                    }

                    Report(
                        NativeAllocationDiagnosticDescriptors.TransferLifetime,
                        transfer.Syntax,
                        transfer.DisplayName);
                }

                foreach (HandleState handle in exit.Handles.Values)
                {
                    if (handle.Returned && !handle.Ambiguous && handle.GenerationRelation != GenerationRelationKind.Unknown
                        || handle.IsUsing
                        || handle.Owner.IsRegion
                        || handle.Owner.IsArena && handle.Owner.IsUsing)
                    {
                        continue;
                    }

                    if (handle.IsScoped)
                    {
                        ReportMissingScopedCompletion(handle.Owner, handle.Syntax);
                        continue;
                    }

                    Report(
                        NativeAllocationDiagnosticDescriptors.LifetimeEscape,
                        handle.Syntax,
                        handle.DisplayName);
                }

                foreach (OwnerState owner in exit.Owners.Values)
                {
                    _context.CancellationToken.ThrowIfCancellationRequested();
                    if ((owner.ScopedPending || owner.ScopedPendingAmbiguous)
                        && !exit.Handles.Values.Any(handle => ReferenceEquals(handle.Owner, owner) && handle.IsScoped && !IsHandleEnded(handle)))
                    {
                        ReportMissingScopedCompletion(owner, owner.Syntax);
                    }

                    if (owner.IsExternalReceiver
                        || owner.IsField
                        || owner.IsUsing
                        || owner.IsRegion
                        || (owner.Returned && !owner.Ambiguous && owner.GenerationRelation != GenerationRelationKind.Unknown)
                        || (owner.Disposed && !owner.Ambiguous && owner.GenerationRelation != GenerationRelationKind.Unknown))
                    {
                        continue;
                    }

                    if (owner.RequiresDeterministicReturn || owner.IsArena)
                    {
                        Report(
                            NativeAllocationDiagnosticDescriptors.LifetimeEscape,
                            owner.Syntax,
                            owner.DisplayName);
                    }
                }
            }
        }

        private void AnalyzeControlFlowGraph(ControlFlowGraph graph)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            _finallyAnalysisCache.Clear();
            BuildOwnershipRelevantBlocks(graph);
            BuildLocalLifetimeRegions(graph);
            BasicBlock entry = graph.Blocks.First(block => block.Kind == BasicBlockKind.Entry);
            FlowSnapshot initial = CaptureSnapshot();
            Dictionary<BasicBlock, FlowSnapshot> entryStates = new();
            Dictionary<BasicBlock, FlowSnapshot> exitStates = new();
            Queue<BasicBlock> work = new([entry]);

            _cfgMode = true;
            _suppressDiagnostics = true;
            while (work.Count != 0)
            {
                _context.CancellationToken.ThrowIfCancellationRequested();
                BasicBlock block = work.Dequeue();
                FlowSnapshot incoming;
                if (block == entry)
                {
                    incoming = CloneSnapshot(initial);
                }
                else
                {
                    FlowSnapshot[] predecessorStates = GraphPredecessors(graph, block)
                        .Where(branch => branch.Source is not null
                            && !IsFinallyBlock(graph, branch.Source)
                            && exitStates.ContainsKey(branch.Source))
                        .Select(branch => ApplyBranchTransfer(
                            graph,
                            branch,
                            exitStates[branch.Source!],
                            emitDiagnostics: false))
                        .ToArray();
                    if (predecessorStates.Length == 0)
                    {
                        continue;
                    }

                    incoming = MergeSnapshotsForResult(predecessorStates);
                }

                if (entryStates.TryGetValue(block, out FlowSnapshot? oldEntry)
                    && SnapshotEquivalent(oldEntry, incoming))
                {
                    continue;
                }

                FlowSnapshot stableIncoming = CloneSnapshot(incoming);
                entryStates[block] = stableIncoming;
                FlowSnapshot outgoing;
                if (_ownershipRelevantBlocks.Contains(block.Ordinal))
                {
                    RestoreSnapshot(stableIncoming);
                    VisitBlock(block);
                    outgoing = CaptureSnapshot();
                }
                else
                {
                    outgoing = stableIncoming;
                }
                bool changed = !exitStates.TryGetValue(block, out FlowSnapshot? oldExit)
                    || !SnapshotEquivalent(oldExit, outgoing);
                exitStates[block] = outgoing;
                if (changed)
                {
                    foreach (BasicBlock successor in GraphSuccessors(graph, block))
                    {
                        work.Enqueue(successor);
                    }
                }
            }

            _suppressDiagnostics = false;
            ReportFinalLexicalScopeExits(graph, exitStates);
            _exitSnapshots.AddRange(exitStates
                .Where(pair => pair.Key.Kind == BasicBlockKind.Exit)
                .Select(pair => pair.Value));

            foreach (BasicBlock block in graph.Blocks)
            {
                _context.CancellationToken.ThrowIfCancellationRequested();
                if (!entryStates.TryGetValue(block, out FlowSnapshot? incoming))
                {
                    continue;
                }

                if (!_ownershipRelevantBlocks.Contains(block.Ordinal))
                {
                    continue;
                }

                RestoreSnapshot(incoming);
                VisitBlock(block);
            }

            _cfgMode = false;
        }

        private void BuildLocalLifetimeRegions(ControlFlowGraph graph)
        {
            _localLifetimeRegions.Clear();
            foreach (ControlFlowRegion region in FlattenRegions(graph.Root))
            {
                if (region.Kind != ControlFlowRegionKind.LocalLifetime)
                {
                    continue;
                }

                foreach (ILocalSymbol local in region.Locals)
                {
                    if (IsOwnerType(local.Type)
                        || IsHandleType(local.Type)
                        || IsNativeTransfer(local.Type))
                    {
                        _localLifetimeRegions[local] = region;
                    }
                }
            }
        }

        private void ReportFinalLexicalScopeExits(
            ControlFlowGraph graph,
            IReadOnlyDictionary<BasicBlock, FlowSnapshot> exitStates)
        {
            foreach (KeyValuePair<BasicBlock, FlowSnapshot> pair in exitStates)
            {
                if (IsFinallyBlock(graph, pair.Key))
                {
                    continue;
                }

                foreach (ControlFlowBranch branch in GraphBranches(graph, pair.Key))
                {
                    _ = ApplyBranchTransfer(graph, branch, pair.Value, emitDiagnostics: true);
                }
            }
        }

        private FlowSnapshot ApplyLexicalScopeExit(
            ControlFlowGraph graph,
            BasicBlock source,
            BasicBlock? destination,
            FlowSnapshot state,
            bool emitDiagnostics,
            bool enteringFinally)
        {
            FlowSnapshot result = CloneSnapshot(state);
            HashSet<ISymbol> exitingSymbols = new(SymbolEqualityComparer.Default);
            exitingSymbols.UnionWith(_localLifetimeRegions
                .Where(pair => ContainsBlock(pair.Value, source.Ordinal)
                    && (destination is null || !ContainsBlock(pair.Value, destination.Ordinal)))
                .Select(pair => pair.Key));
            if (exitingSymbols.Count == 0)
            {
                return result;
            }

            foreach (HandleState handle in result.Handles.Values
                .Where(handle => handle.Symbol is not null && exitingSymbols.Contains(handle.Symbol))
                .ToArray())
            {
                if (enteringFinally && handle.IsScoped && handle.Owner.Symbol is not null
                    && !exitingSymbols.Contains(handle.Owner.Symbol))
                {
                    continue;
                }

                if (!IsHandleEndedAtScopeExit(handle, source, destination) && emitDiagnostics)
                {
                    if (handle.IsScoped
                        && handle.Owner.Symbol is not null
                        && !exitingSymbols.Contains(handle.Owner.Symbol))
                    {
                        if (result.Owners.TryGetValue(handle.Owner.Symbol, out OwnerState? owner))
                        {
                            owner.ScopedPending = true;
                        }
                    }
                    else
                    {
                        if (handle.IsScoped)
                        {
                            ReportMissingScopedCompletion(handle.Owner, handle.Syntax);
                        }
                        else
                        {
                            Report(
                                NativeAllocationDiagnosticDescriptors.LifetimeEscape,
                                handle.Syntax,
                                handle.DisplayName);
                        }
                    }
                }
            }

            foreach (OwnerState owner in result.Owners.Values
                .Where(owner => owner.Symbol is not null && exitingSymbols.Contains(owner.Symbol))
                .ToArray())
            {
                if (owner.RequiresDeterministicReturn
                    && !owner.IsUsing
                    && !owner.IsRegion
                    && !IsOwnerEnded(owner)
                    && emitDiagnostics)
                {
                    Report(
                        NativeAllocationDiagnosticDescriptors.LifetimeEscape,
                        owner.Syntax,
                        owner.DisplayName);
                }

                if (owner.ScopedPending && emitDiagnostics)
                {
                    ReportMissingScopedCompletion(owner, owner.Syntax);
                }
            }

            foreach (TransferState transfer in result.Transfers.Values
                .Where(transfer => transfer.Symbol is not null && exitingSymbols.Contains(transfer.Symbol))
                .ToArray())
            {
                if (transfer.MustEnd
                    && !transfer.IsUsing
                    && transfer.Status is TransferStatus.Active or TransferStatus.Ambiguous
                    && emitDiagnostics)
                {
                    Report(
                        NativeAllocationDiagnosticDescriptors.TransferLifetime,
                        transfer.Syntax,
                        transfer.DisplayName);
                }
            }

            foreach (ISymbol symbol in exitingSymbols)
            {
                if (enteringFinally
                    && result.Handles.TryGetValue(symbol, out HandleState? handle)
                    && handle.IsScoped
                    && handle.Owner.Symbol is not null
                    && !exitingSymbols.Contains(handle.Owner.Symbol))
                {
                    continue;
                }

                result.Handles.Remove(symbol);
                result.Owners.Remove(symbol);
                result.Transfers.Remove(symbol);
            }

            return result;
        }

        private bool IsHandleEndedAtScopeExit(HandleState handle, BasicBlock source, BasicBlock? destination)
        {
            if (handle.IsScoped)
            {
                return IsHandleEnded(handle);
            }

            if (handle.IsUsing || IsHandleEnded(handle))
            {
                return true;
            }

            return handle.Owner.IsArena
                || handle.Owner.IsRegion
                    && handle.Owner.Symbol is not null
                    && IsLifetimeLeaving(handle.Owner.Symbol, source, destination);
        }

        private static bool IsHandleEnded(HandleState handle)
        {
            return handle.Returned
                && !handle.Ambiguous
                && handle.GenerationRelation != GenerationRelationKind.Unknown;
        }

        private static bool IsOwnerEnded(OwnerState owner)
        {
            return (owner.Returned || owner.Disposed)
                && !owner.Ambiguous
                && owner.GenerationRelation != GenerationRelationKind.Unknown;
        }

        private bool IsLifetimeLeaving(ISymbol symbol, BasicBlock source, BasicBlock? destination)
        {
            return _localLifetimeRegions.TryGetValue(symbol, out ControlFlowRegion? region)
                && ContainsBlock(region, source.Ordinal)
                && (destination is null || !ContainsBlock(region, destination.Ordinal));
        }

        private void VisitBlock(BasicBlock block)
        {
            if (!_ownershipRelevantBlocks.Contains(block.Ordinal))
            {
                return;
            }

            foreach (IOperation operation in block.Operations)
            {
                Visit(operation);
            }

            if (block.BranchValue is not null)
            {
                Visit(block.BranchValue);
            }
        }

        private void BuildOwnershipRelevantBlocks(ControlFlowGraph graph)
        {
            _ownershipRelevantBlocks.Clear();
            foreach (BasicBlock block in graph.Blocks)
            {
                bool relevant = block.Operations.Any(OperationRequiresOwnershipVisit)
                    || block.BranchValue is not null
                        && OperationRequiresOwnershipVisit(block.BranchValue);
                if (relevant)
                {
                    _ownershipRelevantBlocks.Add(block.Ordinal);
                }
            }
        }

        private bool OperationRequiresOwnershipVisit(IOperation operation)
        {
            return operation.DescendantsAndSelf().Any(candidate =>
                IsOwnerType(candidate.Type)
                || IsHandleType(candidate.Type)
                || IsNativeTransfer(candidate.Type)
                || candidate is IAwaitOperation
                || candidate is IReturnOperation
                || candidate is IThrowOperation);
        }

        private FlowSnapshot ApplyBranchTransfer(
            ControlFlowGraph graph,
            ControlFlowBranch branch,
            FlowSnapshot state,
            bool emitDiagnostics)
        {
            FlowSnapshot result = CloneSnapshot(state);
            ControlFlowRegion[] finallyRegions = GetFinallyRegionsForBranch(graph, branch);
            for (int index = 0; index < finallyRegions.Length; index++)
            {
                result = AnalyzeFinallyRegion(
                    graph,
                    finallyRegions[index],
                    result,
                    emitDiagnostics);
            }

            return branch.Source is null
                ? result
                : ApplyLexicalScopeExit(
                    graph,
                    branch.Source,
                    branch.Destination,
                    result,
                    emitDiagnostics,
                    enteringFinally: finallyRegions.Length != 0);
        }

        private static ControlFlowRegion[] GetFinallyRegionsForBranch(
            ControlFlowGraph graph,
            ControlFlowBranch branch)
        {
            if (!branch.FinallyRegions.IsDefaultOrEmpty)
            {
                return branch.FinallyRegions.ToArray();
            }

            if (branch.Destination is null
                && branch.Source is not null
                && branch.Semantics is (ControlFlowBranchSemantics.Throw or ControlFlowBranchSemantics.StructuredExceptionHandling))
            {
                return FlattenRegions(graph.Root)
                .Where(region => region.Kind == ControlFlowRegionKind.TryAndFinally)
                .Where(region => region.NestedRegions.Any(nested =>
                    nested.Kind == ControlFlowRegionKind.Try
                    && ContainsBlock(nested, branch.Source.Ordinal)))
                .OrderBy(region => region.LastBlockOrdinal - region.FirstBlockOrdinal)
                .SelectMany(region => region.NestedRegions
                    .Where(nested => nested.Kind == ControlFlowRegionKind.Finally))
                .ToArray();
            }

            return [];
        }

        private FlowSnapshot AnalyzeFinallyRegion(
            ControlFlowGraph graph,
            ControlFlowRegion finallyRegion,
            FlowSnapshot input,
            bool emitDiagnostics)
        {
            if (finallyRegion.Kind == ControlFlowRegionKind.TryAndFinally)
            {
                finallyRegion = finallyRegion.NestedRegions
                    .FirstOrDefault(region => region.Kind == ControlFlowRegionKind.Finally)
                    ?? finallyRegion;
            }

            if (_finallyAnalysisCache.TryGetValue(
                finallyRegion,
                out List<FinallyAnalysisCacheEntry>? cachedEntries))
            {
                FinallyAnalysisCacheEntry? cached = cachedEntries.FirstOrDefault(
                    entry => SnapshotEquivalent(entry.Input, input));
                if (cached is not null
                    && (!emitDiagnostics || cached.DiagnosticsEmitted))
                {
                    return CloneSnapshot(cached.Output);
                }
            }
            else
            {
                cachedEntries = [];
                _finallyAnalysisCache.Add(finallyRegion, cachedEntries);
            }

            BasicBlock[] blocks = graph.Blocks
                .Where(block => ContainsBlock(finallyRegion, block.Ordinal))
                .ToArray();
            if (blocks.Length == 0)
            {
                return input;
            }

            HashSet<int> memberOrdinals = [.. blocks.Select(block => block.Ordinal)];
            BasicBlock[] entries = blocks
                .Where(block => GraphPredecessors(graph, block, includeFinallySources: true).Any(branch =>
                    branch.Source is null || !memberOrdinals.Contains(branch.Source.Ordinal)))
                .ToArray();
            if (entries.Length == 0)
            {
                entries = [blocks.OrderBy(block => block.Ordinal).First()];
            }

            Dictionary<BasicBlock, FlowSnapshot> entryStates = new();
            Dictionary<BasicBlock, FlowSnapshot> exitStates = new();
            Queue<BasicBlock> work = new();
            foreach (BasicBlock entry in entries)
            {
                entryStates[entry] = CloneSnapshot(input);
                work.Enqueue(entry);
            }

            bool previousConvergenceSuppression = _suppressDiagnostics;
            try
            {
                _suppressDiagnostics = true;
                while (work.Count != 0)
                {
                    _context.CancellationToken.ThrowIfCancellationRequested();
                    BasicBlock block = work.Dequeue();
                    if (!entryStates.TryGetValue(block, out FlowSnapshot? incoming))
                    {
                        continue;
                    }

                    FlowSnapshot outgoing;
                    if (_ownershipRelevantBlocks.Contains(block.Ordinal))
                    {
                        RestoreSnapshot(incoming);
                        VisitBlock(block);
                        outgoing = CaptureSnapshot();
                    }
                    else
                    {
                        outgoing = incoming;
                    }
                    bool changed = !exitStates.TryGetValue(block, out FlowSnapshot? oldExit)
                        || !SnapshotEquivalent(oldExit, outgoing);
                    exitStates[block] = outgoing;
                    if (!changed)
                    {
                        continue;
                    }

                    foreach (ControlFlowBranch branch in GraphBranches(graph, block)
                        .Where(branch => branch.Destination is not null && memberOrdinals.Contains(branch.Destination.Ordinal)))
                    {
                        BasicBlock successor = blocks.First(candidate => candidate.Ordinal == branch.Destination!.Ordinal);
                        FlowSnapshot candidate = ApplyBranchTransfer(
                            graph,
                            branch,
                            outgoing,
                            emitDiagnostics: false);
                        if (entryStates.TryGetValue(successor, out FlowSnapshot? oldEntry))
                        {
                            candidate = MergeSnapshotsForResult(oldEntry, candidate);
                        }

                        if (!entryStates.TryGetValue(successor, out oldEntry)
                            || !SnapshotEquivalent(oldEntry, candidate))
                        {
                            entryStates[successor] = CloneSnapshot(candidate);
                            work.Enqueue(successor);
                        }
                    }
                }
            }
            finally
            {
                _suppressDiagnostics = previousConvergenceSuppression;
            }

            if (emitDiagnostics)
            {
                bool previousReportingSuppression = _suppressDiagnostics;
                try
                {
                    _suppressDiagnostics = false;
                    foreach (BasicBlock block in blocks.OrderBy(block => block.Ordinal))
                    {
                        if (!entryStates.TryGetValue(block, out FlowSnapshot? stableEntry))
                        {
                            continue;
                        }

                        RestoreSnapshot(stableEntry);
                        VisitBlock(block);
                        RestoreSnapshot(stableEntry);
                        if (!exitStates.TryGetValue(block, out FlowSnapshot? stableOutgoing))
                        {
                            continue;
                        }

                        foreach (ControlFlowBranch branch in GraphBranches(graph, block)
                            .Where(branch => branch.Destination is null || !memberOrdinals.Contains(branch.Destination.Ordinal)))
                        {
                            _ = ApplyFinallyRegionExit(
                                graph,
                                branch,
                                stableOutgoing,
                                emitDiagnostics: true,
                                applyExitLexical: false);
                        }
                    }
                }
                finally
                {
                    _suppressDiagnostics = previousReportingSuppression;
                }
            }

            List<FlowSnapshot> exits = [];
            foreach (BasicBlock block in blocks)
            {
                if (!exitStates.TryGetValue(block, out FlowSnapshot? stableOutgoing))
                {
                    continue;
                }

                ControlFlowBranch[] outgoingBranches = GraphBranches(graph, block)
                    .Where(branch => branch.Destination is null || !memberOrdinals.Contains(branch.Destination.Ordinal))
                    .ToArray();
                foreach (ControlFlowBranch branch in outgoingBranches)
                {
                    exits.Add(ApplyFinallyRegionExit(
                        graph,
                        branch,
                        stableOutgoing,
                        emitDiagnostics: false,
                        applyExitLexical: false));
                }

                if (outgoingBranches.Length == 0
                    && !GraphBranches(graph, block).Any(branch => branch.Destination is not null && memberOrdinals.Contains(branch.Destination.Ordinal)))
                {
                    exits.Add(stableOutgoing);
                }
            }

            FlowSnapshot output = exits.Count == 0
                ? input
                : MergeSnapshotsForResult(exits.ToArray());
            FinallyAnalysisCacheEntry? existing = cachedEntries.FirstOrDefault(
                entry => SnapshotEquivalent(entry.Input, input));
            if (existing is null)
            {
                cachedEntries.Add(new FinallyAnalysisCacheEntry(
                    CloneSnapshot(input),
                    CloneSnapshot(output),
                    emitDiagnostics));
            }
            else
            {
                existing.Output = CloneSnapshot(output);
                existing.DiagnosticsEmitted |= emitDiagnostics;
            }

            return output;
        }

        private sealed class FinallyAnalysisCacheEntry
        {
            internal FinallyAnalysisCacheEntry(
                FlowSnapshot input,
                FlowSnapshot output,
                bool diagnosticsEmitted)
            {
                Input = input;
                Output = output;
                DiagnosticsEmitted = diagnosticsEmitted;
            }

            internal FlowSnapshot Input { get; }

            internal FlowSnapshot Output { get; set; }

            internal bool DiagnosticsEmitted { get; set; }
        }

        private FlowSnapshot ApplyFinallyRegionExit(
            ControlFlowGraph graph,
            ControlFlowBranch branch,
            FlowSnapshot state,
            bool emitDiagnostics,
            bool applyExitLexical)
        {
            FlowSnapshot result = ApplyFinallyRegions(graph, branch, state, emitDiagnostics);
            if (!applyExitLexical || branch.Source is null)
            {
                return result;
            }

            return ApplyLexicalScopeExit(
                graph,
                branch.Source,
                branch.Destination,
                result,
                emitDiagnostics,
                enteringFinally: GetFinallyRegionsForBranch(graph, branch).Length != 0);
        }

        private FlowSnapshot ApplyFinallyRegions(
            ControlFlowGraph graph,
            ControlFlowBranch branch,
            FlowSnapshot state,
            bool emitDiagnostics)
        {
            FlowSnapshot result = CloneSnapshot(state);
            ControlFlowRegion[] finallyRegions = GetFinallyRegionsForBranch(graph, branch);
            for (int index = 0; index < finallyRegions.Length; index++)
            {
                result = AnalyzeFinallyRegion(
                    graph,
                    finallyRegions[index],
                    result,
                    emitDiagnostics);
            }

            return result;
        }

        private static IEnumerable<ControlFlowRegion> FlattenRegions(ControlFlowRegion region)
        {
            yield return region;
            foreach (ControlFlowRegion child in region.NestedRegions)
            {
                foreach (ControlFlowRegion nested in FlattenRegions(child))
                {
                    yield return nested;
                }
            }
        }

        private static bool ContainsBlock(ControlFlowRegion region, int ordinal)
        {
            return ordinal >= region.FirstBlockOrdinal && ordinal <= region.LastBlockOrdinal;
        }

        private static IEnumerable<ControlFlowBranch> GraphBranches(ControlFlowGraph graph, BasicBlock block)
        {
            return ControlFlowTopology.For(graph).Branches(block);
        }

        private static IEnumerable<BasicBlock> GraphSuccessors(ControlFlowGraph graph, BasicBlock block)
        {
            return ControlFlowTopology.For(graph).Successors(block);
        }

        private static IEnumerable<ControlFlowBranch> GraphPredecessors(
            ControlFlowGraph graph,
            BasicBlock destination,
            bool includeFinallySources = false)
        {
            return ControlFlowTopology.For(graph)
                .Predecessors(destination, includeFinallySources);
        }

        private static bool IsFinallyBlock(ControlFlowGraph graph, BasicBlock block)
        {
            return ControlFlowTopology.For(graph).IsFinallyBlock(block);
        }

        private sealed class ControlFlowTopology
        {
            private static readonly ConditionalWeakTable<
                ControlFlowGraph,
                ControlFlowTopology> Cache = new();

            private readonly Dictionary<int, ControlFlowBranch[]> _branchesBySource;
            private readonly Dictionary<int, ControlFlowBranch[]> _predecessorsByDestination;
            private readonly Dictionary<int, BasicBlock[]> _successorsBySource;
            private readonly HashSet<int> _finallyBlocks;

            private ControlFlowTopology(ControlFlowGraph graph)
            {
                Dictionary<int, HashSet<ControlFlowBranch>> branchesBySource = [];
                Dictionary<int, HashSet<ControlFlowBranch>> predecessorsByDestination = [];
                Dictionary<int, BasicBlock> blocksByOrdinal = graph.Blocks.ToDictionary(
                    static block => block.Ordinal);
                foreach (BasicBlock block in graph.Blocks)
                {
                    Add(block.ConditionalSuccessor);
                    Add(block.FallThroughSuccessor);
                    foreach (ControlFlowBranch branch in block.Predecessors)
                    {
                        Add(branch);
                    }
                }

                _branchesBySource = branchesBySource.ToDictionary(
                    static pair => pair.Key,
                    static pair => pair.Value.ToArray());
                _predecessorsByDestination = predecessorsByDestination.ToDictionary(
                    static pair => pair.Key,
                    static pair => pair.Value.ToArray());
                _finallyBlocks = [];
                foreach (ControlFlowRegion region in FlattenRegions(graph.Root)
                    .Where(static region => region.Kind == ControlFlowRegionKind.Finally))
                {
                    for (int ordinal = region.FirstBlockOrdinal;
                        ordinal <= region.LastBlockOrdinal;
                        ordinal++)
                    {
                        _finallyBlocks.Add(ordinal);
                    }
                }

                _successorsBySource = [];
                foreach (KeyValuePair<int, ControlFlowBranch[]> pair in _branchesBySource)
                {
                    _successorsBySource[pair.Key] = pair.Value
                        .Where(static branch => branch.Destination is not null)
                        .Select(static branch => branch.Destination!)
                        .Where(destination => !_finallyBlocks.Contains(destination.Ordinal))
                        .Distinct()
                        .ToArray();
                }

                void Add(ControlFlowBranch? branch)
                {
                    if (branch is null)
                    {
                        return;
                    }

                    if (branch.Source is BasicBlock source)
                    {
                        if (!branchesBySource.TryGetValue(
                            source.Ordinal,
                            out HashSet<ControlFlowBranch>? sourceBranches))
                        {
                            sourceBranches = [];
                            branchesBySource.Add(source.Ordinal, sourceBranches);
                        }

                        sourceBranches.Add(branch);
                    }

                    if (branch.Destination is BasicBlock destination
                        && blocksByOrdinal.ContainsKey(destination.Ordinal))
                    {
                        if (!predecessorsByDestination.TryGetValue(
                            destination.Ordinal,
                            out HashSet<ControlFlowBranch>? destinationBranches))
                        {
                            destinationBranches = [];
                            predecessorsByDestination.Add(
                                destination.Ordinal,
                                destinationBranches);
                        }

                        destinationBranches.Add(branch);
                    }
                }
            }

            internal static ControlFlowTopology For(ControlFlowGraph graph)
            {
                return Cache.GetValue(
                    graph,
                    static candidate => new ControlFlowTopology(candidate));
            }

            internal IEnumerable<ControlFlowBranch> Branches(BasicBlock source)
            {
                return _branchesBySource.TryGetValue(
                    source.Ordinal,
                    out ControlFlowBranch[]? branches)
                    ? branches
                    : [];
            }

            internal IEnumerable<BasicBlock> Successors(BasicBlock source)
            {
                return _successorsBySource.TryGetValue(
                    source.Ordinal,
                    out BasicBlock[]? successors)
                    ? successors
                    : [];
            }

            internal IEnumerable<ControlFlowBranch> Predecessors(
                BasicBlock destination,
                bool includeFinallySources)
            {
                if (!_predecessorsByDestination.TryGetValue(
                    destination.Ordinal,
                    out ControlFlowBranch[]? predecessors))
                {
                    return [];
                }

                return includeFinallySources
                    ? predecessors
                    : predecessors.Where(
                        branch => branch.Source is null
                            || !_finallyBlocks.Contains(branch.Source.Ordinal));
            }

            internal bool IsFinallyBlock(BasicBlock block)
            {
                return _finallyBlocks.Contains(block.Ordinal);
            }
        }

        private static FlowSnapshot EmptySnapshot()
        {
            return new FlowSnapshot(
                new Dictionary<ISymbol, OwnerState>(SymbolEqualityComparer.Default),
                new Dictionary<ISymbol, HandleState>(SymbolEqualityComparer.Default),
                new Dictionary<ISymbol, TransferState>(SymbolEqualityComparer.Default),
                [],
                []);
        }

        private FlowSnapshot CaptureSnapshot()
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            Dictionary<ISymbol, OwnerState> owners = new(SymbolEqualityComparer.Default);
            Dictionary<OwnerState, OwnerState> ownerCopies = new();
            foreach (KeyValuePair<ISymbol, OwnerState> pair in _owners)
            {
                OwnerState copy = pair.Value.Clone();
                owners.Add(pair.Key, copy);
                ownerCopies.Add(pair.Value, copy);
            }

            Dictionary<ISymbol, HandleState> handles = new(SymbolEqualityComparer.Default);
            foreach (KeyValuePair<ISymbol, HandleState> pair in _handles)
            {
                OwnerState owner = ownerCopies.TryGetValue(pair.Value.Owner, out OwnerState? copy)
                    ? copy
                    : pair.Value.Owner.Clone();
                handles.Add(pair.Key, pair.Value.Clone(owner));
            }

            Dictionary<ISymbol, TransferState> transfers = new(SymbolEqualityComparer.Default);
            foreach (KeyValuePair<ISymbol, TransferState> pair in _transfers)
            {
                transfers.Add(pair.Key, pair.Value.Clone());
            }

            HashSet<OwnerState> borrowed = [];
            foreach (OwnerState owner in _borrowedOwners)
            {
                borrowed.Add(ownerCopies.TryGetValue(owner, out OwnerState? copy) ? copy : owner.Clone());
            }

            return new FlowSnapshot(owners, handles, transfers, [.. _regions], borrowed);
        }

        private void RestoreSnapshot(FlowSnapshot snapshot)
        {
            FlowSnapshot copy = CloneSnapshot(snapshot);
            _owners.Clear();
            foreach (KeyValuePair<ISymbol, OwnerState> pair in copy.Owners)
            {
                _owners.Add(pair.Key, pair.Value);
            }

            _handles.Clear();
            foreach (KeyValuePair<ISymbol, HandleState> pair in copy.Handles)
            {
                _handles.Add(pair.Key, pair.Value);
            }

            _transfers.Clear();
            foreach (KeyValuePair<ISymbol, TransferState> pair in copy.Transfers)
            {
                _transfers.Add(pair.Key, pair.Value);
            }

            _regions.Clear();
            _regions.AddRange(copy.Regions);
            _borrowedOwners.Clear();
            foreach (OwnerState owner in copy.BorrowedOwners)
            {
                _borrowedOwners.Add(owner);
            }
        }

        private void MergeSnapshots(params FlowSnapshot[] paths)
        {
            RestoreSnapshot(MergeSnapshotsForResult(paths));
        }

        private static FlowSnapshot MergeSnapshotsForResult(params FlowSnapshot[] paths)
        {
            if (paths.Length == 0)
            {
                return new FlowSnapshot(
                    new Dictionary<ISymbol, OwnerState>(SymbolEqualityComparer.Default),
                    new Dictionary<ISymbol, HandleState>(SymbolEqualityComparer.Default),
                    new Dictionary<ISymbol, TransferState>(SymbolEqualityComparer.Default),
                    [],
                    []);
            }

            Dictionary<ISymbol, OwnerState> owners = new(SymbolEqualityComparer.Default);
            HashSet<ISymbol> ownerSymbols = new(SymbolEqualityComparer.Default);
            foreach (FlowSnapshot path in paths)
            {
                ownerSymbols.UnionWith(path.Owners.Keys);
            }

            foreach (ISymbol symbol in ownerSymbols)
            {
                OwnerState? first = paths
                    .Select(path => path.Owners.TryGetValue(symbol, out OwnerState? owner) ? owner : null)
                    .FirstOrDefault(owner => owner is not null);
                if (first is null)
                {
                    continue;
                }

                OwnerState merged = first.Clone();
                bool presentOnEveryPath = true;
                foreach (FlowSnapshot path in paths)
                {
                    if (!path.Owners.TryGetValue(symbol, out OwnerState? owner))
                    {
                        presentOnEveryPath = false;
                        continue;
                    }

                    merged.Returned &= owner.Returned;
                    merged.Disposed &= owner.Disposed;
                    merged.Unleased &= owner.Unleased;
                    merged.ScopedOwnerEligible &= owner.ScopedOwnerEligible;
                    merged.Ambiguous |= owner.Ambiguous;
                    merged.ScopedPending |= owner.ScopedPending;
                    merged.ScopedPendingAmbiguous |= owner.ScopedPendingAmbiguous;
                    foreach (GenerationLivenessFact fact in owner.LivenessFacts)
                    {
                        if (!merged.LivenessFacts.Any(existing => existing.SameAs(fact)))
                        {
                            merged.LivenessFacts.Add(fact);
                        }
                    }
                }

                if (!presentOnEveryPath)
                {
                    merged.Ambiguous = true;
                    merged.ScopedOwnerEligible = false;
                }

                if (paths.Any(path => path.Owners.TryGetValue(symbol, out OwnerState? owner) && owner.Returned != merged.Returned)
                    || paths.Any(path => path.Owners.TryGetValue(symbol, out OwnerState? owner) && owner.Disposed != merged.Disposed)
                    || paths.Any(path => path.Owners.TryGetValue(symbol, out OwnerState? owner) && owner.Unleased != merged.Unleased))
                {
                    merged.Ambiguous = true;
                }

                if (paths.Any(path => path.Owners.TryGetValue(symbol, out OwnerState? owner) && owner.ScopedPending != merged.ScopedPending))
                {
                    merged.ScopedPendingAmbiguous = true;
                }

                merged.GenerationRelation = MergeOwnerGenerationRelation(paths, first);
                if (merged.GenerationRelation == GenerationRelationKind.Unknown)
                {
                    merged.Generation = paths
                        .Where(path => path.Owners.ContainsKey(symbol))
                        .Select(path => path.Owners[symbol].Generation)
                        .DefaultIfEmpty(first.Generation)
                        .Max();
                }

                owners.Add(symbol, merged);
            }

            Dictionary<ISymbol, HandleState> handles = new(SymbolEqualityComparer.Default);
            HashSet<ISymbol> handleSymbols = new(SymbolEqualityComparer.Default);
            foreach (FlowSnapshot path in paths)
            {
                handleSymbols.UnionWith(path.Handles.Keys);
            }

            foreach (ISymbol symbol in handleSymbols)
            {
                HandleState? first = paths
                    .Select(path => path.Handles.TryGetValue(symbol, out HandleState? handle) ? handle : null)
                    .FirstOrDefault(handle => handle is not null);
                if (first is null)
                {
                    continue;
                }

                OwnerState owner = first.Owner.Symbol is not null
                    && owners.TryGetValue(first.Owner.Symbol, out OwnerState? mergedOwner)
                    ? mergedOwner
                    : first.Owner.Clone();
                HandleState mergedHandle = first.Clone(owner);
                bool presentOnEveryPath = true;
                foreach (FlowSnapshot path in paths)
                {
                    if (!path.Handles.TryGetValue(symbol, out HandleState? handle))
                    {
                        presentOnEveryPath = false;
                        continue;
                    }

                    mergedHandle.Returned &= handle.Returned;
                    mergedHandle.Ambiguous |= handle.Ambiguous;
                }

                if (!presentOnEveryPath)
                {
                    bool allPresentPathsEnded = paths
                        .Where(path => path.Handles.ContainsKey(symbol))
                        .Select(path => path.Handles[symbol])
                        .All(handle => handle.IsScoped && IsHandleEnded(handle));
                    if (allPresentPathsEnded)
                    {
                        mergedHandle.Returned = true;
                        mergedHandle.Ambiguous = false;
                        mergedHandle.GenerationRelation = GenerationRelationKind.Current;
                    }
                    else
                    {
                        mergedHandle.Ambiguous = true;
                    }
                }

                if (paths.Any(path => path.Handles.TryGetValue(symbol, out HandleState? handle) && handle.Returned != mergedHandle.Returned))
                {
                    mergedHandle.Ambiguous = true;
                }

                mergedHandle.GenerationRelation = MergeHandleGenerationRelation(paths, symbol, first, mergedHandle);
                if (!presentOnEveryPath
                    && !mergedHandle.Ambiguous
                    && mergedHandle.Returned)
                {
                    mergedHandle.GenerationRelation = GenerationRelationKind.Current;
                }
                if (mergedHandle.GenerationRelation == GenerationRelationKind.Unknown)
                {
                    mergedHandle.Generation = paths
                        .Where(path => path.Handles.ContainsKey(symbol))
                        .Select(path => path.Handles[symbol].Generation)
                        .DefaultIfEmpty(first.Generation)
                        .Max();
                }

                handles.Add(symbol, mergedHandle);
            }

            Dictionary<ISymbol, TransferState> transfers = new(SymbolEqualityComparer.Default);
            HashSet<ISymbol> transferSymbols = new(SymbolEqualityComparer.Default);
            foreach (FlowSnapshot path in paths)
            {
                transferSymbols.UnionWith(path.Transfers.Keys);
            }

            foreach (ISymbol symbol in transferSymbols)
            {
                TransferState? first = paths
                    .Select(path => path.Transfers.TryGetValue(symbol, out TransferState? transfer) ? transfer : null)
                    .FirstOrDefault(transfer => transfer is not null);
                if (first is null)
                {
                    continue;
                }

                TransferState merged = first.Clone();
                TransferState[] present = paths
                    .Select(path => path.Transfers.TryGetValue(symbol, out TransferState? transfer) ? transfer : null)
                    .Where(transfer => transfer is not null)
                    .Cast<TransferState>()
                    .ToArray();
                if (present.Length != paths.Length
                    || present.Any(transfer => transfer.Status != first.Status)
                    || present.Any(transfer => transfer.OwnershipIdentity != first.OwnershipIdentity))
                {
                    merged.Status = TransferStatus.Ambiguous;
                }

                merged.MustEnd = present.Any(transfer => transfer.MustEnd);
                transfers.Add(symbol, merged);
            }

            List<RegionScope> regions = [];
            foreach (FlowSnapshot path in paths)
            {
                foreach (RegionScope region in path.Regions)
                {
                    if (!regions.Any(existing => existing.Name == region.Name && existing.Start == region.Start))
                    {
                        regions.Add(region);
                    }
                }
            }

            HashSet<OwnerState> borrowed = [];
            foreach (FlowSnapshot path in paths)
            {
                foreach (OwnerState owner in path.BorrowedOwners)
                {
                    if (owner.Symbol is not null && owners.TryGetValue(owner.Symbol, out OwnerState? mergedOwner))
                    {
                        borrowed.Add(mergedOwner);
                    }
                }
            }

            return new FlowSnapshot(owners, handles, transfers, regions, borrowed);
        }

        private static FlowSnapshot CloneSnapshot(FlowSnapshot snapshot)
        {
            Dictionary<ISymbol, OwnerState> owners = new(SymbolEqualityComparer.Default);
            Dictionary<OwnerState, OwnerState> ownerCopies = new();
            foreach (KeyValuePair<ISymbol, OwnerState> pair in snapshot.Owners)
            {
                OwnerState copy = pair.Value.Clone();
                owners.Add(pair.Key, copy);
                ownerCopies.Add(pair.Value, copy);
            }

            Dictionary<ISymbol, HandleState> handles = new(SymbolEqualityComparer.Default);
            foreach (KeyValuePair<ISymbol, HandleState> pair in snapshot.Handles)
            {
                OwnerState owner = ownerCopies.TryGetValue(pair.Value.Owner, out OwnerState? copy)
                    ? copy
                    : pair.Value.Owner.Clone();
                handles.Add(pair.Key, pair.Value.Clone(owner));
            }

            Dictionary<ISymbol, TransferState> transfers = new(SymbolEqualityComparer.Default);
            foreach (KeyValuePair<ISymbol, TransferState> pair in snapshot.Transfers)
            {
                transfers.Add(pair.Key, pair.Value.Clone());
            }

            HashSet<OwnerState> borrowed = [];
            foreach (OwnerState owner in snapshot.BorrowedOwners)
            {
                borrowed.Add(ownerCopies.TryGetValue(owner, out OwnerState? copy) ? copy : owner.Clone());
            }

            return new FlowSnapshot(owners, handles, transfers, [.. snapshot.Regions], borrowed);
        }

        private static GenerationRelationKind MergeOwnerGenerationRelation(
            IReadOnlyList<FlowSnapshot> paths,
            OwnerState first)
        {
            OwnerState[] states = paths
                .Select(path => path.Owners.TryGetValue(first.Symbol!, out OwnerState? owner) ? owner : null)
                .Where(owner => owner is not null)
                .Cast<OwnerState>()
                .ToArray();

            if (states.Length != paths.Count || states.Any(state => state.Ambiguous))
            {
                return GenerationRelationKind.Unknown;
            }

            GenerationRelationKind relation = JoinGenerationRelations(states.Select(state => state.GenerationRelation));
            if (states.Any(state => state.Returned != first.Returned
                || state.Disposed != first.Disposed
                || state.Unleased != first.Unleased))
            {
                return GenerationRelationKind.Unknown;
            }

            return states.Any(state => state.Generation != first.Generation)
                ? relation == GenerationRelationKind.Unknown ? GenerationRelationKind.Unknown : GenerationRelationKind.Current
                : relation;
        }

        private static GenerationRelationKind MergeHandleGenerationRelation(
            IReadOnlyList<FlowSnapshot> paths,
            ISymbol symbol,
            HandleState first,
            HandleState merged)
        {
            HandleState[] states = paths
                .Select(path => path.Handles.TryGetValue(symbol, out HandleState? handle) ? handle : null)
                .Where(handle => handle is not null)
                .Cast<HandleState>()
                .ToArray();

            if (states.Length != paths.Count || states.Any(state => state.Ambiguous))
            {
                return GenerationRelationKind.Unknown;
            }

            GenerationRelationKind relation = JoinGenerationRelations(states.Select(state => state.GenerationRelation));
            if (states.Any(state => state.Returned != first.Returned))
            {
                return GenerationRelationKind.Unknown;
            }

            if (!states.Any(state => state.Generation != first.Generation))
            {
                return relation;
            }

            // A returned handle is stale regardless of the numeric generation used by
            // the path that returned it. An active handle with two generation numbers
            // is not proven to refer to one current allocation and remains unknown.
            return merged.Returned
                ? relation == GenerationRelationKind.Unknown ? GenerationRelationKind.Unknown : GenerationRelationKind.Current
                : GenerationRelationKind.Unknown;
        }

        private static GenerationRelationKind JoinGenerationRelations(IEnumerable<GenerationRelationKind> relations)
        {
            GenerationRelationKind result = GenerationRelationKind.Exact;
            foreach (GenerationRelationKind relation in relations)
            {
                if (relation == GenerationRelationKind.Unknown)
                {
                    return GenerationRelationKind.Unknown;
                }

                if (relation == GenerationRelationKind.Current)
                {
                    result = GenerationRelationKind.Current;
                }
            }

            return result;
        }

        private static bool LivenessFactsEquivalent(
            IReadOnlyList<GenerationLivenessFact> left,
            IReadOnlyList<GenerationLivenessFact> right)
        {
            return left.Count == right.Count
                && left.All(fact => right.Any(other => other.SameAs(fact)));
        }

        private static bool SnapshotEquivalent(FlowSnapshot left, FlowSnapshot right)
        {
            if (left.Owners.Count != right.Owners.Count
                || left.Handles.Count != right.Handles.Count
                || left.Transfers.Count != right.Transfers.Count)
            {
                return false;
            }

            foreach (KeyValuePair<ISymbol, OwnerState> pair in left.Owners)
            {
                if (!right.Owners.TryGetValue(pair.Key, out OwnerState? other)
                    || pair.Value.Returned != other.Returned
                    || pair.Value.Disposed != other.Disposed
                    || pair.Value.Unleased != other.Unleased
                    || pair.Value.ScopedOwnerEligible != other.ScopedOwnerEligible
                    || pair.Value.Ambiguous != other.Ambiguous
                    || pair.Value.ScopedPending != other.ScopedPending
                    || pair.Value.ScopedPendingAmbiguous != other.ScopedPendingAmbiguous
                    || pair.Value.Generation != other.Generation
                    || pair.Value.GenerationRelation != other.GenerationRelation
                    || !LivenessFactsEquivalent(pair.Value.LivenessFacts, other.LivenessFacts))
                {
                    return false;
                }
            }

            foreach (KeyValuePair<ISymbol, HandleState> pair in left.Handles)
            {
                if (!right.Handles.TryGetValue(pair.Key, out HandleState? other)
                    || pair.Value.Returned != other.Returned
                    || pair.Value.Ambiguous != other.Ambiguous
                    || pair.Value.Generation != other.Generation
                    || pair.Value.GenerationRelation != other.GenerationRelation)
                {
                    return false;
                }
            }

            foreach (KeyValuePair<ISymbol, TransferState> pair in left.Transfers)
            {
                if (!right.Transfers.TryGetValue(pair.Key, out TransferState? other)
                    || pair.Value.Status != other.Status
                    || pair.Value.OwnershipIdentity != other.OwnershipIdentity
                    || pair.Value.MustEnd != other.MustEnd
                    || pair.Value.IsUsing != other.IsUsing)
                {
                    return false;
                }
            }

            if (left.Regions.Count != right.Regions.Count || left.BorrowedOwners.Count != right.BorrowedOwners.Count)
            {
                return false;
            }

            for (int index = 0; index < left.Regions.Count; index++)
            {
                RegionScope a = left.Regions[index];
                RegionScope b = right.Regions[index];
                if (a.Name != b.Name || a.Scope != b.Scope || a.Start != b.Start)
                {
                    return false;
                }
            }

            return left.BorrowedOwners.All(owner => owner.Symbol is not null
                && right.BorrowedOwners.Any(other => SymbolEqualityComparer.Default.Equals(other.Symbol, owner.Symbol)));
        }

        public override void VisitObjectCreation(IObjectCreationOperation operation)
        {
            if (IsOwnerType(operation.Type))
            {
                RegisterOwner(operation);
            }

            base.VisitObjectCreation(operation);
        }

        public override void VisitUsing(IUsingOperation operation)
        {
            foreach (IVariableDeclaratorOperation declarator in operation.Resources.DescendantsAndSelf().OfType<IVariableDeclaratorOperation>())
            {
                _usingResourceSymbols.Add(declarator.Symbol);
            }

            base.VisitUsing(operation);
        }

        public override void VisitInvocation(IInvocationOperation operation)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            bool transferWasPreprocessed = _preprocessedTransferInvocations.Remove(operation);
            if (!transferWasPreprocessed && IsTransferMoveInvocation(operation))
            {
                ProcessTransferMove(operation);
            }
            else if (!transferWasPreprocessed && IsTransferFactoryInvocation(operation))
            {
                RegisterTransferFactory(operation);
            }
            else if (!transferWasPreprocessed
                && IsNativeTransfer(operation.TargetMethod.ContainingType))
            {
                ProcessTransferInvocation(operation);
            }

            if (operation.IsImplicit)
            {
                base.VisitInvocation(operation);
                return;
            }

            IOperation? instance = Unwrap(operation.Instance);
            HandleState? handle = GetHandle(instance);
            OwnerState? owner = GetOwner(instance);
            OwnerState? borrowedOwner = null;

            if (handle is not null)
            {
                if (!CheckHandleUse(handle, operation.Syntax, operation.TargetMethod.Name))
                {
                    base.VisitInvocation(operation);
                    return;
                }

                if (operation.TargetMethod.Name == "Dispose")
                {
                    handle.Returned = true;
                }

                if (operation.TargetMethod.Name is "Access" or "Read")
                {
                    borrowedOwner = handle.Owner;
                }
            }

            if (owner is not null)
            {
                ProcessOwnerLifecycle(owner, operation.TargetMethod.Name, operation.Syntax);
            }

            foreach (IArgumentOperation argument in operation.Arguments)
            {
                IOperation? argumentValue = Unwrap(argument.Value);
                if (argumentValue is null
                    || !IsOwnerType(argumentValue.Type)
                    || GetOwner(argumentValue) is not OwnerState argumentOwner)
                {
                    continue;
                }

                LifecycleEffect effect = GetLifecycleEffect(operation.TargetMethod, argument.Parameter);
                if (effect is not LifecycleEffect.None)
                {
                    ProcessOwnerLifecycle(argumentOwner, ToMethodName(effect), operation.Syntax);
                }
            }

            if (IsHandleCreatingInvocation(operation))
            {
                RegisterHandle(operation);
            }
            else if (IsScopedGroupInitialization(operation))
            {
                RegisterScopedGroupHandles(operation);
            }

            if (borrowedOwner is not null)
            {
                _borrowedOwners.Add(borrowedOwner);
                _borrowScopes.Add((borrowedOwner.Symbol, handle!.DisplayName));
                ReportBorrowedCallbackLifecycle(operation, borrowedOwner);
            }

            try
            {
                base.VisitInvocation(operation);
            }
            finally
            {
                if (borrowedOwner is not null)
                {
                    _borrowScopes.RemoveAt(_borrowScopes.Count - 1);
                    _borrowedOwners.Remove(borrowedOwner);
                }
            }
        }

        public override void VisitConditional(IConditionalOperation operation)
        {
            Visit(operation.Condition);
            FlowSnapshot before = CaptureSnapshot();

            Visit(operation.WhenTrue);
            FlowSnapshot whenTrue = CaptureSnapshot();

            RestoreSnapshot(before);
            Visit(operation.WhenFalse);
            FlowSnapshot whenFalse = CaptureSnapshot();

            MergeSnapshots(whenTrue, whenFalse);
        }

        public override void VisitForEachLoop(IForEachLoopOperation operation)
        {
            VisitLoopWithSnapshot(operation, () => base.VisitForEachLoop(operation));
        }

        public override void VisitForLoop(IForLoopOperation operation)
        {
            VisitLoopWithSnapshot(operation, () => base.VisitForLoop(operation));
        }

        public override void VisitWhileLoop(IWhileLoopOperation operation)
        {
            VisitLoopWithSnapshot(operation, () => base.VisitWhileLoop(operation));
        }

        private void VisitLoopWithSnapshot(ILoopOperation operation, Action visit)
        {
            FlowSnapshot before = CaptureSnapshot();
            FlowSnapshot header = before;
            bool previousSuppression = _suppressDiagnostics;
            _suppressDiagnostics = true;
            for (int iteration = 0; iteration < 32; iteration++)
            {
                RestoreSnapshot(header);
                visit();
                FlowSnapshot bodyExit = CaptureSnapshot();
                FlowSnapshot next = MergeSnapshotsForResult(before, bodyExit);
                if (SnapshotEquivalent(header, next))
                {
                    header = next;
                    break;
                }

                header = next;
            }

            _suppressDiagnostics = previousSuppression;
            RestoreSnapshot(header);
        }

        public override void VisitSwitch(ISwitchOperation operation)
        {
            Visit(operation.Value);
            FlowSnapshot before = CaptureSnapshot();
            List<FlowSnapshot> paths = [before];

            foreach (ISwitchCaseOperation switchCase in operation.Cases)
            {
                RestoreSnapshot(before);
                Visit(switchCase);
                paths.Add(CaptureSnapshot());
            }

            MergeSnapshots(paths.ToArray());
        }

        public override void VisitTry(ITryOperation operation)
        {
            FlowSnapshot before = CaptureSnapshot();
            int previousProtectionDepth = _finallyProtectionDepth;
            if (operation.Finally is not null)
            {
                _finallyProtectionDepth++;
            }

            FlowSnapshot tryPath;
            List<FlowSnapshot> paths;
            try
            {
                Visit(operation.Body);
                tryPath = EndNestedTransferScope(
                    operation.Body,
                    CaptureSnapshot());
                FlowSnapshot catchEntry = MergeSnapshotsForResult(before, tryPath);
                paths = [tryPath, catchEntry];

                foreach (ICatchClauseOperation catchClause in operation.Catches)
                {
                    RestoreSnapshot(catchEntry);
                    Visit(catchClause);
                    paths.Add(EndNestedTransferScope(
                        catchClause,
                        CaptureSnapshot()));
                }
            }
            finally
            {
                _finallyProtectionDepth = previousProtectionDepth;
            }

            if (operation.Finally is not null)
            {
                int previousFinallyDepth = _finallyDepth;
                _finallyDepth++;
                try
                {
                    for (int index = 0; index < paths.Count; index++)
                    {
                        RestoreSnapshot(paths[index]);
                        Visit(operation.Finally);
                        paths[index] = EndNestedTransferScope(
                            operation.Finally,
                            CaptureSnapshot());
                    }
                }
                finally
                {
                    _finallyDepth = previousFinallyDepth;
                }
            }

            MergeSnapshots(paths.ToArray());
        }

        private FlowSnapshot EndNestedTransferScope(
            IOperation scope,
            FlowSnapshot snapshot)
        {
            FlowSnapshot result = CloneSnapshot(snapshot);
            foreach (KeyValuePair<ISymbol, TransferState> pair in result.Transfers
                .Where(pair => pair.Key is ILocalSymbol
                    && IsDeclaredWithin(pair.Key, scope.Syntax))
                .ToArray())
            {
                TransferState transfer = pair.Value;
                if (transfer.MustEnd
                    && !transfer.IsUsing
                    && transfer.Status is TransferStatus.Active or TransferStatus.Ambiguous)
                {
                    Report(
                        NativeAllocationDiagnosticDescriptors.TransferLifetime,
                        transfer.Syntax,
                        transfer.DisplayName);
                }

                result.Transfers.Remove(pair.Key);
            }

            return result;
        }

        private bool IsDeclaredWithin(
            ISymbol symbol,
            SyntaxNode scope)
        {
            foreach (SyntaxReference reference in symbol.DeclaringSyntaxReferences)
            {
                SyntaxNode declaration = reference.GetSyntax(
                    _context.CancellationToken);
                if (ReferenceEquals(
                        declaration.SyntaxTree,
                        scope.SyntaxTree)
                    && scope.Span.Contains(declaration.SpanStart))
                {
                    return true;
                }
            }

            return false;
        }

        public override void VisitPropertyReference(IPropertyReferenceOperation operation)
        {
            if (IsNativeTransfer(operation.Instance?.Type)
                && GetTransfer(Unwrap(operation.Instance)) is TransferState transfer)
            {
                CheckTransferActive(
                    transfer,
                    operation.Syntax,
                    operation.Property.Name);
            }

            HandleState? handle = GetHandle(Unwrap(operation.Instance));
            if (handle is not null)
            {
                CheckHandleUse(handle, operation.Syntax, operation.Property.Name);
            }

            base.VisitPropertyReference(operation);
        }

        public override void VisitArrayElementReference(IArrayElementReferenceOperation operation)
        {
            HandleState? handle = GetHandle(Unwrap(operation.ArrayReference));
            if (handle is not null)
            {
                CheckHandleUse(handle, operation.Syntax, "indexer");
            }

            base.VisitArrayElementReference(operation);
        }

        public override void VisitSimpleAssignment(ISimpleAssignmentOperation operation)
        {
            IOperation? value = Unwrap(operation.Value);
            if (IsNativeTransfer(operation.Target.Type))
            {
                ProcessTransferAssignment(
                    GetTarget(operation.Target),
                    value);
            }

            if (value is not null && value is not IObjectCreationOperation && !IsHandleCreatingInvocation(value))
            {
                Target target = GetTarget(operation.Target);
                if (IsHandleType(value.Type) && GetHandle(value) is HandleState handle)
                {
                    ReportHandleTransfer(handle, target);
                }
                else if (IsOwnerType(value.Type)
                    && GetOwner(value) is OwnerState owner
                    && !SymbolEqualityComparer.Default.Equals(owner.Symbol, target.Symbol))
                {
                    ReportOwnerTransfer(owner, target);
                }
            }

            base.VisitSimpleAssignment(operation);
        }

        public override void VisitVariableDeclarator(IVariableDeclaratorOperation operation)
        {
            IOperation? value = Unwrap(operation.Initializer?.Value);
            if (IsNativeTransfer(operation.Symbol.Type))
            {
                ProcessTransferAssignment(
                    new Target(operation.Symbol, operation.Syntax),
                    value);
            }

            if (value is not null && value is not IObjectCreationOperation && !IsHandleCreatingInvocation(value))
            {
                Target target = new(operation.Symbol, operation.Syntax);
                if (IsHandleType(value.Type) && GetHandle(value) is HandleState handle)
                {
                    ReportHandleTransfer(handle, target);
                }
                else if (IsOwnerType(value.Type)
                    && GetOwner(value) is OwnerState owner
                    && !SymbolEqualityComparer.Default.Equals(owner.Symbol, target.Symbol))
                {
                    ReportOwnerTransfer(owner, target);
                }
            }

            base.VisitVariableDeclarator(operation);
        }

        public override void VisitArgument(IArgumentOperation operation)
        {
            IOperation? value = Unwrap(operation.Value);
            if (value is not null && IsNativeTransfer(value.Type))
            {
                if (operation.Parent is not IInvocationOperation transferCall
                    || !IsTransferMoveInvocation(transferCall)
                    || operation.Parameter?.RefKind != RefKind.Ref)
                {
                    if (!IsTransferMoveInvocation(value)
                        && !IsTransferFactoryInvocation(value)
                        && GetTransfer(value) is TransferState transfer)
                    {
                        string destination = operation.Parent is IInvocationOperation invocation
                            ? invocation.TargetMethod.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
                            : "the call argument";
                        ReportTransferCopy(
                            transfer,
                            operation.Syntax,
                            destination);
                    }
                }
            }

            if (value is not null && value is not IObjectCreationOperation)
            {
                if (operation.Parent is IInvocationOperation composite
                    && IsNonRetainingCompositeLeaseOperation(composite))
                {
                    base.VisitArgument(operation);
                    return;
                }

                if (IsHandleType(value.Type) && GetHandle(value) is HandleState handle)
                {
                    string callName = operation.Parent is IInvocationOperation invocation
                        ? invocation.TargetMethod.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
                        : "an unknown call";
                    RecordGenerationLiveness(
                        handle,
                        GenerationLivenessKind.UnknownRetention,
                        handle.DisplayName + " -> " + callName);
                    Report(
                        handle.Owner.IsRegion
                            ? NativeAllocationDiagnosticDescriptors.LocalEscape
                            : NativeAllocationDiagnosticDescriptors.UnknownCall,
                        operation.Syntax,
                        handle.DisplayName,
                        callName);
                }
                else if (IsOwnerType(value.Type) && GetOwner(value) is OwnerState owner)
                {
                    if (operation.Parent is IInvocationOperation invocation
                        && (IsTransferFactoryInvocation(invocation)
                            || GetLifecycleEffect(invocation.TargetMethod, operation.Parameter)
                                is not LifecycleEffect.None
                            || IsNonRetainingOwnerParameter(
                                invocation.TargetMethod,
                                operation.Parameter)))
                    {
                        base.VisitArgument(operation);
                        return;
                    }

                    Report(
                        NativeAllocationDiagnosticDescriptors.OwnerAlias,
                        operation.Syntax,
                        owner.DisplayName,
                        "the call argument");
                }
            }

            base.VisitArgument(operation);
        }

        public override void VisitReturn(IReturnOperation operation)
        {
            IOperation? value = Unwrap(operation.ReturnedValue);
            if (value is IInvocationOperation transferInvocation
                && IsTransferMoveInvocation(transferInvocation))
            {
                ProcessTransferMove(transferInvocation);
                _preprocessedTransferInvocations.Add(transferInvocation);
            }

            if (value is not null
                && IsNativeTransfer(value.Type)
                && !IsTransferMoveInvocation(value)
                && !IsTransferFactoryInvocation(value)
                && GetTransfer(value) is TransferState transfer)
            {
                ReportTransferCopy(
                    transfer,
                    operation.Syntax,
                    "the return value");
            }

            if (value is not null && value is not IObjectCreationOperation)
            {
                if (IsHandleType(value.Type) && GetHandle(value) is HandleState handle)
                {
                    ReportHandleTransfer(handle, new Target(null, operation.Syntax));
                }
                else if (IsOwnerType(value.Type) && GetOwner(value) is OwnerState owner)
                {
                    ReportOwnerTransfer(owner, new Target(null, operation.Syntax));
                }
            }

            if (operation.Syntax is YieldStatementSyntax)
            {
                ReportActiveHandlesAcrossBoundary(operation.Syntax);
            }
            else if (!_cfgMode && _finallyProtectionDepth == 0 && _finallyDepth == 0)
            {
                ReportActiveExit(operation.Syntax);
            }

            base.VisitReturn(operation);
        }

        public override void VisitThrow(IThrowOperation operation)
        {
            if (!_cfgMode && _finallyProtectionDepth == 0 && _finallyDepth == 0)
            {
                ReportActiveExit(operation.Syntax);
            }

            base.VisitThrow(operation);
        }

        public override void VisitAwait(IAwaitOperation operation)
        {
            ReportActiveHandlesAcrossBoundary(operation.Syntax);
            base.VisitAwait(operation);
        }

        public override void VisitAnonymousFunction(IAnonymousFunctionOperation operation)
        {
            _closureDepth++;
            try
            {
                Visit(operation.Body);
            }
            finally
            {
                _closureDepth--;
            }
        }

        public override void VisitLocalFunction(ILocalFunctionOperation operation)
        {
            _closureDepth++;
            try
            {
                Visit(operation.Body);
            }
            finally
            {
                _closureDepth--;
            }
        }

        public override void VisitLocalReference(ILocalReferenceOperation operation)
        {
            if (_closureDepth > 0)
            {
                if (_transfers.TryGetValue(operation.Local, out TransferState? transfer)
                    && IsCapturedLocal(operation.Local, operation.Syntax))
                {
                    ReportTransferCopy(
                        transfer,
                        operation.Syntax,
                        "a closure");
                }

                if (_handles.TryGetValue(operation.Local, out HandleState? handle) && !handle.Returned)
                {
                    RecordGenerationLiveness(
                        handle,
                        GenerationLivenessKind.UnknownRetention,
                        handle.DisplayName + " -> a closure");
                    Report(
                        handle.Owner.IsRegion
                            ? NativeAllocationDiagnosticDescriptors.LocalEscape
                            : NativeAllocationDiagnosticDescriptors.PooledEscape,
                        operation.Syntax,
                        handle.DisplayName,
                        "a closure");
                }
                else if (_owners.TryGetValue(operation.Local, out OwnerState? owner) && !owner.IsField)
                {
                    owner.ScopedOwnerEligible = false;
                    Report(
                        NativeAllocationDiagnosticDescriptors.OwnerAlias,
                        operation.Syntax,
                        owner.DisplayName,
                        "a closure");
                }
            }

            base.VisitLocalReference(operation);
        }

        private bool IsCapturedLocal(
            ILocalSymbol local,
            SyntaxNode referenceSyntax)
        {
            AnonymousFunctionExpressionSyntax? containingCallback = referenceSyntax
                .Ancestors()
                .OfType<AnonymousFunctionExpressionSyntax>()
                .FirstOrDefault();
            if (containingCallback is null)
            {
                return false;
            }

            SyntaxNode? declaration = local.DeclaringSyntaxReferences
                .FirstOrDefault()
                ?.GetSyntax(_context.CancellationToken);
            return declaration is null
                || !containingCallback.Span.Contains(declaration.SpanStart);
        }

        public override void VisitConversion(IConversionOperation operation)
        {
            IOperation? operand = Unwrap(operation.Operand);
            if (operand is not null
                && IsNativeTransfer(operand.Type)
                && !IsNativeTransfer(operation.Type)
                && GetTransfer(operand) is TransferState transfer)
            {
                ReportTransferCopy(
                    transfer,
                    operation.Syntax,
                    operation.Type?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
                        ?? "a converted value");
            }

            if (operand is not null
                && IsOwnerType(operand.Type)
                && operation.IsImplicit
                && IsDirectUsingResourceConversion(operation.Syntax))
            {
                base.VisitConversion(operation);
                return;
            }

            if (operand is not null && IsHandleType(operand.Type) && !IsHandleType(operation.Type))
            {
                if (GetHandle(operand) is HandleState handle)
                {
                    RecordGenerationLiveness(
                        handle,
                        GenerationLivenessKind.AliasOrEscape,
                        handle.DisplayName + " -> " + (operation.Type?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? "a converted value"));
                    Report(
                        handle.Owner.IsRegion
                            ? NativeAllocationDiagnosticDescriptors.LocalEscape
                            : NativeAllocationDiagnosticDescriptors.PooledEscape,
                        operation.Syntax,
                        handle.DisplayName,
                        operation.Type?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? "a converted value");
                }
            }
            else if (operand is not null && IsOwnerType(operand.Type) && !IsOwnerType(operation.Type))
            {
                if (GetOwner(operand) is OwnerState owner)
                {
                    ReportOwnerTransfer(owner, new Target(null, operation.Syntax));
                }
            }

            base.VisitConversion(operation);
        }

        private static bool IsDirectUsingResourceConversion(SyntaxNode syntax)
        {
            return syntax.AncestorsAndSelf()
                .OfType<UsingStatementSyntax>()
                .Any(statement =>
                    statement.Declaration?.Span.Contains(syntax.Span) == true
                    || statement.Expression?.Span.Contains(syntax.Span) == true)
                || syntax.AncestorsAndSelf()
                    .OfType<LocalDeclarationStatementSyntax>()
                    .Any(statement => statement.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)
                        && statement.Declaration.Span.Contains(syntax.Span));
        }

        public override void VisitTuple(ITupleOperation operation)
        {
            foreach (IOperation element in operation.Elements)
            {
                if (IsNativeTransfer(element.Type)
                    && GetTransfer(Unwrap(element)) is TransferState transfer)
                {
                    ReportTransferCopy(
                        transfer,
                        operation.Syntax,
                        "a tuple");
                }
                else if (IsHandleType(element.Type) && GetHandle(Unwrap(element)) is HandleState handle)
                {
                    ReportHandleTransfer(
                        handle,
                        new Target(null, operation.Syntax));
                }
                else if (IsOwnerType(element.Type) && GetOwner(Unwrap(element)) is OwnerState owner)
                {
                    ReportOwnerTransfer(owner, new Target(null, operation.Syntax));
                }
            }

            base.VisitTuple(operation);
        }

        public override void VisitArrayInitializer(IArrayInitializerOperation operation)
        {
            foreach (IOperation element in operation.ElementValues)
            {
                if (IsNativeTransfer(element.Type)
                    && GetTransfer(Unwrap(element)) is TransferState transfer)
                {
                    ReportTransferCopy(
                        transfer,
                        operation.Syntax,
                        "an array");
                }
                else if (IsHandleType(element.Type) && GetHandle(Unwrap(element)) is HandleState handle)
                {
                    ReportHandleTransfer(
                        handle,
                        new Target(null, operation.Syntax));
                }
                else if (IsOwnerType(element.Type) && GetOwner(Unwrap(element)) is OwnerState owner)
                {
                    ReportOwnerTransfer(owner, new Target(null, operation.Syntax));
                }
            }

            base.VisitArrayInitializer(operation);
        }

        public override void VisitDeconstructionAssignment(IDeconstructionAssignmentOperation operation)
        {
            foreach (IOperation value in operation.Value is ITupleOperation tuple
                ? tuple.Elements
                : [operation.Value])
            {
                if (IsHandleType(value.Type) && GetHandle(Unwrap(value)) is HandleState handle)
                {
                    ReportHandleTransfer(
                        handle,
                        new Target(null, operation.Syntax));
                }
            }

            base.VisitDeconstructionAssignment(operation);
        }

        private void ProcessTransferInvocation(
            IInvocationOperation operation)
        {
            IOperation? instance = Unwrap(operation.Instance);
            TransferState? transfer = GetTransfer(instance);
            if (transfer is null
                || !CheckTransferActive(
                    transfer,
                    operation.Syntax,
                    operation.TargetMethod.Name))
            {
                return;
            }

            if (operation.TargetMethod.Name is "Access" or "Read")
            {
                ReportTransferViewEscapes(operation);
                return;
            }

            if (operation.TargetMethod.Name == "Dispose")
            {
                MarkTransferIdentity(
                    transfer.OwnershipIdentity,
                    TransferStatus.Disposed);
            }
        }

        private void ProcessTransferMove(
            IInvocationOperation operation)
        {
            IArgumentOperation? sourceArgument = operation.Arguments
                .FirstOrDefault(argument => argument.Parameter?.RefKind == RefKind.Ref);
            IOperation? sourceOperation = Unwrap(sourceArgument?.Value);
            TransferState? source = GetTransfer(sourceOperation);
            if (source is null)
            {
                Report(
                    NativeAllocationDiagnosticDescriptors.InvalidTransferMove,
                    operation.Syntax,
                    sourceOperation?.Syntax.ToString() ?? "the source",
                    "unknown");
                return;
            }

            if (source.IsUsing)
            {
                Report(
                    NativeAllocationDiagnosticDescriptors.InvalidTransferMove,
                    operation.Syntax,
                    source.DisplayName,
                    "bound to automatic disposal");
                return;
            }

            if (source.Status != TransferStatus.Active)
            {
                Report(
                    NativeAllocationDiagnosticDescriptors.InvalidTransferMove,
                    operation.Syntax,
                    source.DisplayName,
                    DescribeTransferStatus(source.Status));
                return;
            }

            MarkTransferIdentity(
                source.OwnershipIdentity,
                TransferStatus.Moved);
            Target target = FindTarget(operation);
            if (target.Symbol is not null)
            {
                RegisterTransferDestination(
                    target,
                    operation.Syntax,
                    mustEnd: target.Symbol is ILocalSymbol,
                    isUsing: IsUsingSyntax(operation.Syntax, target.Symbol));
                return;
            }

            if (!IsSafeTransferEscape(operation))
            {
                Report(
                    NativeAllocationDiagnosticDescriptors.TransferLifetime,
                    operation.Syntax,
                    "the moved destination");
            }
        }

        private void RegisterTransferFactory(
            IInvocationOperation operation)
        {
            Target target = FindTarget(operation);
            if (target.Symbol is not ILocalSymbol)
            {
                Report(
                    NativeAllocationDiagnosticDescriptors.TransferAcquisitionEscape,
                    operation.Syntax,
                    operation.TargetMethod.Name);
                return;
            }

            RegisterTransferDestination(
                target,
                operation.Syntax,
                mustEnd: true,
                isUsing: IsUsingSyntax(operation.Syntax, target.Symbol));
        }

        private void ProcessTransferAssignment(
            Target target,
            IOperation? value)
        {
            if (target.Symbol is null)
            {
                if (value is not null
                    && IsNativeTransfer(value.Type)
                    && !IsTransferMoveInvocation(value)
                    && !IsTransferFactoryInvocation(value)
                    && GetTransfer(value) is TransferState discarded)
                {
                    ReportTransferCopy(
                        discarded,
                        target.Syntax,
                        "a discarded value");
                }

                return;
            }

            if (value is null || IsNullValue(value))
            {
                ReportActiveTransferOverwrite(target);
                _transfers[target.Symbol] = TransferState.Create(
                    target.Symbol,
                    target.Syntax,
                    TransferStatus.Unowned,
                    mustEnd: false,
                    isUsing: false);
                return;
            }

            if (IsTransferMoveInvocation(value)
                || IsTransferFactoryInvocation(value))
            {
                return;
            }

            if (IsNativeTransfer(value.Type)
                && GetTransfer(value) is TransferState source)
            {
                if (SymbolEqualityComparer.Default.Equals(
                    source.Symbol,
                    target.Symbol))
                {
                    return;
                }

                ReportActiveTransferOverwrite(target);
                if (CheckTransferActive(
                    source,
                    target.Syntax,
                    "copy"))
                {
                    Report(
                        NativeAllocationDiagnosticDescriptors.TransferAlias,
                        target.Syntax,
                        source.DisplayName,
                        target.Symbol.Name);
                }

                _transfers[target.Symbol] = source.CloneFor(
                    target.Symbol,
                    mustEnd: target.Symbol is ILocalSymbol,
                    isUsing: IsUsingSyntax(target.Syntax, target.Symbol),
                    target.Syntax);
                return;
            }

            if (IsNativeTransfer(GetSymbolType(target.Symbol)))
            {
                RegisterTransferDestination(
                    target,
                    value.Syntax,
                    mustEnd: target.Symbol is ILocalSymbol,
                    isUsing: IsUsingSyntax(target.Syntax, target.Symbol));
            }
        }

        private void RegisterTransferDestination(
            Target target,
            SyntaxNode origin,
            bool mustEnd,
            bool isUsing)
        {
            if (target.Symbol is null)
            {
                return;
            }

            ReportActiveTransferOverwrite(target);
            _transfers[target.Symbol] = TransferState.Create(
                target.Symbol,
                origin,
                TransferStatus.Active,
                mustEnd,
                isUsing);
        }

        private void ReportActiveTransferOverwrite(Target target)
        {
            if (target.Symbol is null
                || !_transfers.TryGetValue(
                    target.Symbol,
                    out TransferState? previous)
                || previous.Status != TransferStatus.Active)
            {
                return;
            }

            Report(
                NativeAllocationDiagnosticDescriptors.TransferLifetime,
                target.Syntax,
                previous.DisplayName);
        }

        private TransferState? GetTransfer(IOperation? operation)
        {
            if (operation is null || !IsNativeTransfer(operation.Type))
            {
                return null;
            }

            ISymbol? symbol = GetSymbol(operation);
            if (symbol is null)
            {
                return null;
            }

            if (_transfers.TryGetValue(
                symbol,
                out TransferState? existing))
            {
                return existing;
            }

            TransferState external = TransferState.CreateExternal(
                symbol,
                operation.Syntax);
            _transfers.Add(symbol, external);
            return external;
        }

        private bool CheckTransferActive(
            TransferState transfer,
            SyntaxNode syntax,
            string operation)
        {
            if (transfer.Status == TransferStatus.Active)
            {
                return true;
            }

            Report(
                NativeAllocationDiagnosticDescriptors.InactiveTransferUse,
                syntax,
                transfer.DisplayName,
                operation,
                DescribeTransferStatus(transfer.Status));
            return false;
        }

        private void ReportTransferCopy(
            TransferState transfer,
            SyntaxNode syntax,
            string destination)
        {
            if (!CheckTransferActive(
                transfer,
                syntax,
                "copy"))
            {
                return;
            }

            Report(
                NativeAllocationDiagnosticDescriptors.TransferAlias,
                syntax,
                transfer.DisplayName,
                destination);
        }

        private void MarkTransferIdentity(
            string ownershipIdentity,
            TransferStatus status)
        {
            foreach (TransferState transfer in _transfers.Values)
            {
                if (transfer.OwnershipIdentity == ownershipIdentity)
                {
                    transfer.Status = status;
                }
            }
        }

        private static string DescribeTransferStatus(
            TransferStatus status) =>
            status switch
            {
                TransferStatus.Unowned => "absent",
                TransferStatus.Moved => "moved",
                TransferStatus.Disposed => "disposed",
                TransferStatus.Ambiguous => "not proven active",
                _ => "active"
            };

        private static bool IsNullValue(IOperation operation) =>
            operation.ConstantValue.HasValue
            && operation.ConstantValue.Value is null;

        private static bool IsSafeTransferEscape(
            IInvocationOperation move)
        {
            IOperation current = move;
            while (current.Parent is IConversionOperation conversion
                && conversion.Operand == current)
            {
                current = conversion;
            }

            return current.Parent is IArgumentOperation
                or IReturnOperation
                || move.Syntax.Ancestors()
                    .Any(ancestor => ancestor is ReturnStatementSyntax
                        or ArrowExpressionClauseSyntax
                        or ArgumentSyntax);
        }

        private bool IsTransferMoveInvocation(IOperation operation) =>
            operation is IInvocationOperation invocation
            && invocation.TargetMethod.Name == "Move"
            && IsNativeTransfer(invocation.TargetMethod.ContainingType)
            && IsNativeTransfer(invocation.Type);

        private bool IsTransferFactoryInvocation(IOperation operation) =>
            operation is IInvocationOperation invocation
            && IsNativeTransfer(invocation.Type)
            && (invocation.TargetMethod.Name == "RentTransferable"
                    && invocation.TargetMethod.ContainingType.ToDisplayString()
                        == "Supprocom.NativeAllocationManagement.NativeTransferPoolExtensions"
                || invocation.TargetMethod.Name == "ScratchTransferable"
                    && IsNativeArena(invocation.TargetMethod.ContainingType));

        private void ReportTransferViewEscapes(
            IInvocationOperation operation)
        {
            SemanticModel model = _context.Compilation.GetSemanticModel(
                operation.Syntax.SyntaxTree);
            foreach (IArgumentOperation argument in operation.Arguments)
            {
                AnonymousFunctionExpressionSyntax[] callbackSyntaxes = argument.Value.Syntax
                    .DescendantNodesAndSelf()
                    .OfType<AnonymousFunctionExpressionSyntax>()
                    .ToArray();
                if (callbackSyntaxes.Length != 0)
                {
                    ReportTransferViewSyntaxEscapes(callbackSyntaxes[0]);
                }

                foreach (AnonymousFunctionExpressionSyntax callbackSyntax in callbackSyntaxes)
                {
                    if (model.GetOperation(
                        callbackSyntax,
                        _context.CancellationToken) is not IAnonymousFunctionOperation callback)
                    {
                        continue;
                    }

                    IParameterSymbol[] viewParameters = callback.Symbol.Parameters
                        .Where(parameter => IsNativeLeaseView(parameter.Type))
                        .ToArray();
                    if (viewParameters.Length == 0)
                    {
                        continue;
                    }

                    TransferViewEscapeWalker walker = new(
                        this,
                        viewParameters);
                    walker.Visit(callback.Body);
                }
            }
        }

        private void ReportTransferViewSyntaxEscapes(
            AnonymousFunctionExpressionSyntax callback)
        {
            string[] parameterNames = callback switch
            {
                SimpleLambdaExpressionSyntax simple =>
                    [simple.Parameter.Identifier.ValueText],
                ParenthesizedLambdaExpressionSyntax parenthesized =>
                    parenthesized.ParameterList.Parameters
                        .Select(parameter => parameter.Identifier.ValueText)
                        .ToArray(),
                AnonymousMethodExpressionSyntax anonymous =>
                    anonymous.ParameterList?.Parameters
                        .Select(parameter => parameter.Identifier.ValueText)
                        .ToArray()
                    ?? [],
                _ => []
            };
            if (parameterNames.Length == 0)
            {
                return;
            }

            foreach (AnonymousFunctionExpressionSyntax nested in callback
                .DescendantNodes()
                .OfType<AnonymousFunctionExpressionSyntax>())
            {
                IdentifierNameSyntax? captured = nested
                    .DescendantNodes()
                    .OfType<IdentifierNameSyntax>()
                    .FirstOrDefault(identifier => parameterNames.Contains(
                        identifier.Identifier.ValueText,
                        StringComparer.Ordinal));
                if (captured is null)
                {
                    continue;
                }

                Report(
                    NativeAllocationDiagnosticDescriptors.TransferViewEscape,
                    nested,
                    captured.Identifier.ValueText,
                    "a nested callback");
            }
        }

        private bool IsViewDerived(
            IOperation? operation,
            IReadOnlyCollection<IParameterSymbol> viewParameters)
        {
            if (operation is null
                || !IsViewLikeType(operation.Type))
            {
                return false;
            }

            return operation.DescendantsAndSelf()
                .OfType<IParameterReferenceOperation>()
                .Any(reference => viewParameters.Any(parameter =>
                    SymbolEqualityComparer.Default.Equals(
                        parameter,
                        reference.Parameter)));
        }

        private bool IsViewLikeType(ITypeSymbol? type)
        {
            if (IsNativeLeaseView(type))
            {
                return true;
            }

            if (type is not INamedTypeSymbol named)
            {
                return type?.TypeKind == TypeKind.Pointer;
            }

            string metadataName = named.OriginalDefinition.ToDisplayString();
            return metadataName is "System.Span<T>"
                or "System.ReadOnlySpan<T>";
        }

        private sealed class TransferViewEscapeWalker : OperationWalker
        {
            private readonly MethodFlowAnalyzer _analyzer;
            private readonly IParameterSymbol[] _viewParameters;

            internal TransferViewEscapeWalker(
                MethodFlowAnalyzer analyzer,
                IParameterSymbol[] viewParameters)
            {
                _analyzer = analyzer;
                _viewParameters = viewParameters;
            }

            public override void VisitReturn(IReturnOperation operation)
            {
                if (_analyzer.IsViewDerived(
                    operation.ReturnedValue,
                    _viewParameters))
                {
                    _analyzer.Report(
                        NativeAllocationDiagnosticDescriptors.TransferViewEscape,
                        operation.Syntax,
                        ViewName(operation.ReturnedValue),
                        "the callback return");
                }

                base.VisitReturn(operation);
            }

            public override void VisitSimpleAssignment(
                ISimpleAssignmentOperation operation)
            {
                if (_analyzer.IsViewDerived(
                    operation.Value,
                    _viewParameters)
                    && operation.Target is IFieldReferenceOperation
                        or IPropertyReferenceOperation
                        or IArrayElementReferenceOperation)
                {
                    _analyzer.Report(
                        NativeAllocationDiagnosticDescriptors.TransferViewEscape,
                        operation.Syntax,
                        ViewName(operation.Value),
                        "a nonlocal assignment");
                }

                base.VisitSimpleAssignment(operation);
            }

            public override void VisitArgument(IArgumentOperation operation)
            {
                if (_analyzer.IsViewDerived(
                    operation.Value,
                    _viewParameters)
                    && operation.Parameter?.ScopedKind == ScopedKind.None)
                {
                    string destination = operation.Parent is IInvocationOperation invocation
                        ? invocation.TargetMethod.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
                        : "an unscoped call";
                    _analyzer.Report(
                        NativeAllocationDiagnosticDescriptors.TransferViewEscape,
                        operation.Syntax,
                        ViewName(operation.Value),
                        destination);
                }

                base.VisitArgument(operation);
            }

            public override void VisitAnonymousFunction(
                IAnonymousFunctionOperation operation)
            {
                IParameterReferenceOperation? captured = operation.Body
                    .DescendantsAndSelf()
                    .OfType<IParameterReferenceOperation>()
                    .FirstOrDefault(reference => _viewParameters.Any(parameter =>
                        SymbolEqualityComparer.Default.Equals(
                            parameter,
                            reference.Parameter)));
                if (captured is not null)
                {
                    _analyzer.Report(
                        NativeAllocationDiagnosticDescriptors.TransferViewEscape,
                        operation.Syntax,
                        captured.Parameter.Name,
                        "a nested callback");
                    return;
                }

                base.VisitAnonymousFunction(operation);
            }

            private string ViewName(IOperation? operation)
            {
                IParameterReferenceOperation? reference = operation?
                    .DescendantsAndSelf()
                    .OfType<IParameterReferenceOperation>()
                    .FirstOrDefault(item => _viewParameters.Any(parameter =>
                        SymbolEqualityComparer.Default.Equals(
                            parameter,
                            item.Parameter)));
                return reference?.Parameter.Name ?? "view";
            }
        }

        private void RegisterOwner(IObjectCreationOperation operation)
        {
            Target target = FindTarget(operation);
            bool isRegion = IsNativeRegion(operation.Type);
            bool isUsing = isRegion
                ? IsDirectBracedRegionUsingStatement(operation.Syntax, target.Symbol)
                : IsUsingSyntax(operation.Syntax, target.Symbol);
            bool requiresDeterministicReturn = IsNativeArena(operation.Type) || RequiresDeterministicReturn(operation);
            TextSpan? regionScope = isRegion && isUsing ? GetUsingScope(operation.Syntax, target.Symbol) : null;

            if (target.Symbol is null)
            {
                Report(
                    isRegion
                        ? NativeAllocationDiagnosticDescriptors.RegionMustBeUsing
                        : NativeAllocationDiagnosticDescriptors.OwnerAlias,
                    operation.Syntax,
                    "the temporary owner");
                return;
            }

            OwnerState owner = new(
                target.Symbol,
                operation.Type!,
                isRegion,
                IsNativeArena(operation.Type),
                isUsing,
                target.Symbol is IFieldSymbol,
                requiresDeterministicReturn,
                operation.Syntax,
                regionScope);
            owner.ScopedOwnerEligible = target.Symbol is ILocalSymbol
                && (!isRegion || isUsing);
            switch (GetDeclarationActivation(operation))
            {
                case DeclarationActivation.Unleased:
                    owner.Unleased = true;
                    break;
                case DeclarationActivation.Unknown:
                    owner.Ambiguous = true;
                    break;
            }
            _owners[target.Symbol] = owner;

            if (isRegion && !isUsing)
            {
                Report(NativeAllocationDiagnosticDescriptors.RegionMustBeUsing, operation.Syntax, target.Symbol.Name);
            }

            if (isRegion && target.Symbol is IFieldSymbol)
            {
                Report(NativeAllocationDiagnosticDescriptors.RegionMustBeUsing, operation.Syntax, target.Symbol.Name);
            }

            if (requiresDeterministicReturn && target.Symbol is IFieldSymbol field && !HasFieldDisposalPath(field))
            {
                Report(NativeAllocationDiagnosticDescriptors.FieldDisposal, operation.Syntax, field.Name);
            }

            if (isRegion && isUsing && GetUsingScope(operation.Syntax, target.Symbol) is TextSpan scope)
            {
                foreach (RegionScope previous in _regions)
                {
                    if (previous.Scope.Contains(operation.Syntax.Span.Start) && previous.Start < operation.Syntax.Span.Start)
                    {
                        Report(
                            NativeAllocationDiagnosticDescriptors.NestedRegion,
                            operation.Syntax,
                            target.Symbol.Name,
                            previous.Name);
                    }
                }

                _regions.Add(new RegionScope(target.Symbol.Name, scope, operation.Syntax.Span.Start));
            }
        }

        private void RegisterHandle(IInvocationOperation operation)
        {
            IOperation? instance = Unwrap(operation.Instance);
            OwnerState? owner = GetOwner(instance);
            if (owner is null || !CheckOwnerActive(owner, operation.Syntax, operation.TargetMethod.Name))
            {
                return;
            }

            Target target = FindTarget(operation);
            bool scopedAcquisition = operation.TargetMethod.Name is "LeaseScoped" or "ScratchScoped";
            if (target.Symbol is not ILocalSymbol)
            {
                Report(
                    scopedAcquisition
                        ? NativeAllocationDiagnosticDescriptors.ScopedAcquisitionEscape
                        : owner.IsRegion
                            ? NativeAllocationDiagnosticDescriptors.LocalEscape
                            : NativeAllocationDiagnosticDescriptors.PooledEscape,
                    operation.Syntax,
                    scopedAcquisition ? operation.TargetMethod.Name : "the new allocation",
                    scopedAcquisition ? "" : target.Symbol?.Name ?? "an escaping destination");
                return;
            }

            if (owner.IsRegion && owner.RegionScope is TextSpan scope && !scope.Contains(target.Syntax.Span.Start))
            {
                Report(
                    NativeAllocationDiagnosticDescriptors.LocalEscape,
                    target.Syntax,
                    "the new allocation",
                    target.Symbol.Name);
                return;
            }

            HandleState handle = new(
                target.Symbol,
                owner,
                owner.Generation,
                IsUsingSyntax(operation.Syntax, target.Symbol),
                operation.Syntax);
            handle.GenerationRelation = owner.GenerationRelation;
            if (scopedAcquisition)
            {
                if (!owner.ScopedOwnerEligible
                    || target.Symbol is not ILocalSymbol
                    || !IsDirectScopedLocalInitialization(operation.Syntax, target.Symbol))
                {
                    Report(
                        NativeAllocationDiagnosticDescriptors.ScopedAcquisitionEscape,
                        operation.Syntax,
                        owner.ScopedOwnerEligible
                            ? operation.TargetMethod.Name
                            : operation.TargetMethod.Name + " (receiver is not an exclusive local owner)");
                }
                else
                {
                    handle.IsScoped = true;
                    owner.ScopedPending = true;
                }
            }
            else if (owner.ScopedOwnerEligible
                && IsDirectScopedLocalInitialization(operation.Syntax, target.Symbol)
                && operation.TargetMethod.Name is "Rent" or "Lease" or "Scratch")
            {
                Report(
                    NativeAllocationDiagnosticDescriptors.OrdinaryAcquisitionScopedWarning,
                    operation.Syntax,
                    operation.TargetMethod.Name);
            }
            _handles[target.Symbol] = handle;
        }

        private void RegisterScopedGroupHandles(
            IInvocationOperation operation)
        {
            IArgumentOperation? ownerArgument = operation.Arguments
                .FirstOrDefault(argument =>
                    argument.Parameter?.Name == "arena");
            OwnerState? owner = GetOwner(
                Unwrap(ownerArgument?.Value));
            if (owner is null
                || !CheckOwnerActive(
                    owner,
                    operation.Syntax,
                    operation.TargetMethod.Name))
            {
                return;
            }

            IArgumentOperation? sourceArgument = operation.Arguments
                .FirstOrDefault(argument =>
                    argument.Parameter?.Name == "source");
            HandleState? source = GetHandle(
                Unwrap(sourceArgument?.Value));
            if (source is not null)
            {
                CheckHandleUse(
                    source,
                    sourceArgument!.Syntax,
                    operation.TargetMethod.Name);
            }

            foreach (IArgumentOperation argument in operation.Arguments)
            {
                if (argument.Parameter?.RefKind != RefKind.Out
                    || !IsHandleType(argument.Value.Type))
                {
                    continue;
                }

                IOperation? value = Unwrap(argument.Value);
                ISymbol? symbol = value is null
                    ? null
                    : GetSymbol(value);
                if (symbol is not ILocalSymbol)
                {
                    Report(
                        NativeAllocationDiagnosticDescriptors
                            .ScopedAcquisitionEscape,
                        argument.Syntax,
                        operation.TargetMethod.Name);
                    continue;
                }

                HandleState handle = new(
                    symbol,
                    owner,
                    owner.Generation,
                    isUsing: false,
                    argument.Syntax)
                {
                    GenerationRelation =
                        owner.GenerationRelation,
                    IsScoped = true
                };
                _handles[symbol] = handle;
                owner.ScopedPending = true;
            }
        }

        private void ProcessOwnerLifecycle(OwnerState owner, string name, SyntaxNode syntax)
        {
            if (name is "Rent" or "Lease" or "Scratch" or "LeaseScoped" or "ScratchScoped")
            {
                CheckOwnerActive(owner, syntax, name);
                return;
            }

            if (name is "ReturnMemoryToNativeMemory" or "ReturnMemoryToGarbageCollector"
                or "ReleaseLeasesToNativeMemory" or "ReleaseLeasesToGarbageCollector")
            {
                bool isMemoryReturn = name.StartsWith("ReturnMemory", StringComparison.Ordinal);
                bool isNative = name.EndsWith("ToNativeMemory", StringComparison.Ordinal);
                if (isMemoryReturn && owner.IsUsing && !owner.IsRegion)
                {
                    Report(NativeAllocationDiagnosticDescriptors.ScopedLifecycle, syntax, owner.DisplayName, name);
                    return;
                }

                if (!CheckOwnerActive(owner, syntax, name))
                {
                    return;
                }

                GenerationReturnLiveness[] findings = FindGenerationReturnLiveness(owner);
                ReportGenerationReturnLiveness(owner, name, syntax, findings);
                if (isNative && findings.Any(finding => finding.Kind == GenerationLivenessKind.ActiveBorrow))
                {
                    return;
                }

                owner.Returned = isMemoryReturn;
                owner.Ambiguous = false;
                owner.GenerationRelation = owner.GenerationRelation == GenerationRelationKind.Unknown
                    ? GenerationRelationKind.Current
                    : owner.GenerationRelation;
                owner.Generation++;
                owner.ScopedPending = false;
                foreach (HandleState handle in _handles.Values)
                {
                    if (ReferenceEquals(handle.Owner, owner) && !handle.Returned)
                    {
                        handle.Returned = true;
                        handle.Ambiguous = false;
                        handle.GenerationRelation = GenerationRelationKind.Current;
                    }
                }

                _ = isNative;
                return;
            }

            if (name == "RecycleScoped")
            {
                if (!CheckOwnerActive(owner, syntax, name))
                {
                    return;
                }

                if (!owner.ScopedOwnerEligible)
                {
                    ReportWithProvenance(
                        NativeAllocationDiagnosticDescriptors.GenerationReturnLiveValue,
                        syntax,
                        owner.DisplayName + " -> non-exclusive receiver",
                        owner.DisplayName,
                        name,
                        "non-exclusive receiver",
                        "Scoped recycling requires an analyzer-proven exclusive local owner; a parameter, field, alias, capture, or unknown receiver cannot discharge another frame's pending set.");
                    return;
                }

                GenerationReturnLiveness[] pending = FindGenerationReturnLiveness(owner, syntax)
                    .Where(finding => finding.IsScoped)
                    .ToArray();
                if (pending.Length != 0)
                {
                    ReportGenerationReturnLiveness(owner, "RecycleScoped", syntax, pending);
                    return;
                }

                if (owner.ScopedPendingAmbiguous)
                {
                    ReportWithProvenance(
                        NativeAllocationDiagnosticDescriptors.GenerationReturnLiveValue,
                        syntax,
                        owner.DisplayName + " -> ambiguous scoped allocation",
                        owner.DisplayName,
                        "RecycleScoped",
                        "ambiguous scoped allocation",
                        "The scoped pending set is ambiguous across control-flow paths; prove the complete set is dead before recycling.");
                    return;
                }

                owner.ScopedPending = false;
                owner.ScopedPendingAmbiguous = false;
                foreach (HandleState handle in _handles.Values)
                {
                    if (ReferenceEquals(handle.Owner, owner)
                        && handle.IsScoped
                        && !handle.Returned)
                    {
                        handle.Returned = true;
                        handle.Ambiguous = false;
                        handle.GenerationRelation = GenerationRelationKind.Current;
                    }
                }
                return;
            }

            if (name == "LeaseFromMemory")
            {
                if (owner.IsUsing && !owner.IsRegion && !owner.Unleased)
                {
                    Report(NativeAllocationDiagnosticDescriptors.ScopedLifecycle, syntax, owner.DisplayName, name);
                    return;
                }

                if (owner.Ambiguous || owner.GenerationRelation == GenerationRelationKind.Unknown || owner.Disposed)
                {
                    Report(NativeAllocationDiagnosticDescriptors.InvalidLifecycle, syntax, owner.DisplayName, name);
                    return;
                }

                if (owner.IsRegion && !owner.Unleased)
                {
                    Report(NativeAllocationDiagnosticDescriptors.InvalidLifecycle, syntax, owner.DisplayName, name);
                    return;
                }

                if (!owner.Unleased && !owner.Returned)
                {
                    Report(NativeAllocationDiagnosticDescriptors.InvalidLifecycle, syntax, owner.DisplayName, name);
                    return;
                }

                owner.Unleased = false;
                owner.Returned = false;
                owner.Ambiguous = false;
                return;
            }

            if (name == "Dispose")
            {
                if (owner.IsUsing && !owner.IsRegion)
                {
                    Report(NativeAllocationDiagnosticDescriptors.ScopedLifecycle, syntax, owner.DisplayName, name);
                    return;
                }

                if (owner.Ambiguous || owner.GenerationRelation == GenerationRelationKind.Unknown || owner.Disposed)
                {
                    Report(NativeAllocationDiagnosticDescriptors.InvalidLifecycle, syntax, owner.DisplayName, name);
                    return;
                }

                owner.Disposed = true;
                owner.Ambiguous = false;
                owner.Unleased = false;
                owner.Returned = true;
                owner.ScopedPending = false;
                foreach (HandleState handle in _handles.Values)
                {
                    if (ReferenceEquals(handle.Owner, owner))
                    {
                        handle.Returned = true;
                    }
                }
            }
        }

        private GenerationReturnLiveness[] FindGenerationReturnLiveness(OwnerState owner, SyntaxNode? atSyntax = null)
        {
            List<GenerationReturnLiveness> findings = [];
            HashSet<string> paths = new(StringComparer.Ordinal);

            foreach (HandleState handle in _handles.Values
                .Where(handle => ReferenceEquals(handle.Owner, owner)
                    && !IsHandleEnded(handle)
                    && (atSyntax is null
                        || !handle.IsScoped
                        || IsScopedHandleLiveAt(handle, atSyntax))
                    && IsCurrentGeneration(handle, owner))
                .OrderBy(handle => handle.Syntax.SpanStart))
            {
                GenerationLivenessKind kind = IsActivelyBorrowed(handle)
                    ? GenerationLivenessKind.ActiveBorrow
                    : GenerationLivenessKind.RootReference;
                string path = kind == GenerationLivenessKind.ActiveBorrow
                    ? handle.DisplayName + " -> scoped callback"
                    : handle.DisplayName;
                if (paths.Add(kind + ":" + path))
                {
                    findings.Add(new GenerationReturnLiveness(kind, path, handle.IsScoped));
                }
            }

            foreach (GenerationLivenessFact fact in owner.LivenessFacts
                .Where(fact => IsCurrentGeneration(fact, owner))
                .OrderBy(fact => fact.Path, StringComparer.Ordinal))
            {
                if (paths.Add(fact.Kind + ":" + fact.Path))
                {
                    findings.Add(new GenerationReturnLiveness(fact.Kind, fact.Path, isScoped: false));
                }
            }

            return findings.ToArray();
        }

        private bool IsScopedHandleLiveAt(HandleState handle, SyntaxNode operationSyntax)
        {
            if (!handle.IsScoped)
            {
                return true;
            }

            SyntaxNode? lexicalScope = handle.Syntax.AncestorsAndSelf()
                .FirstOrDefault(node => node is BlockSyntax or SwitchSectionSyntax);
            return lexicalScope is null || lexicalScope.Span.Contains(operationSyntax.SpanStart);
        }

        private static bool IsCurrentGeneration(HandleState handle, OwnerState owner)
        {
            return handle.Generation == owner.Generation
                || handle.GenerationRelation == GenerationRelationKind.Current
                    && owner.GenerationRelation == GenerationRelationKind.Current;
        }

        private static bool IsCurrentGeneration(GenerationLivenessFact fact, OwnerState owner)
        {
            return fact.Generation == owner.Generation
                || fact.GenerationRelation == GenerationRelationKind.Current
                    && owner.GenerationRelation == GenerationRelationKind.Current;
        }

        private void ReportGenerationReturnLiveness(
            OwnerState owner,
            string operation,
            SyntaxNode syntax,
            IEnumerable<GenerationReturnLiveness> findings)
        {
            DiagnosticDescriptor descriptor = operation is "ReturnMemoryToNativeMemory" or "ReleaseLeasesToNativeMemory" or "RecycleScoped" or "Dispose"
                ? NativeAllocationDiagnosticDescriptors.GenerationReturnLiveValue
                : NativeAllocationDiagnosticDescriptors.DeferredReturnLiveValue;

            foreach (GenerationReturnLiveness finding in findings)
            {
                ReportWithProvenance(
                    descriptor,
                    syntax,
                    owner.DisplayName + " -> " + finding.Path,
                    owner.DisplayName,
                    operation,
                    finding.Path,
                    DescribeGenerationLiveness(finding.Kind, operation));
            }
        }

        private static string DescribeGenerationLiveness(GenerationLivenessKind kind, string operation)
        {
            return kind switch
            {
                GenerationLivenessKind.RootReference when operation is "ReturnMemoryToNativeMemory" or "ReleaseLeasesToNativeMemory" or "RecycleScoped" or "Dispose"
                    => operation == "Dispose"
                        ? "The root/reference would become stale when owner disposal ends the generation; end it before disposing the owner."
                        : "The root/reference would become stale at the generation boundary; end it before deterministic native return.",
                GenerationLivenessKind.RootReference
                    => "The root/reference becomes stale immediately; it does not retain detached native storage.",
                GenerationLivenessKind.ActiveBorrow when operation is "ReturnMemoryToNativeMemory" or "ReleaseLeasesToNativeMemory" or "RecycleScoped"
                    => "An entered bounded operation still holds the generation; end the callback before deterministic native return.",
                GenerationLivenessKind.ActiveBorrow when operation == "Dispose"
                    => "An entered bounded operation still holds the generation; end the callback before disposing the owner.",
                GenerationLivenessKind.ActiveBorrow
                    => "The entered operation token retains detached native storage until the callback exits.",
                GenerationLivenessKind.AliasOrEscape
                    => "The owner-derived value has an alias or escape path that must end before the generation boundary.",
                GenerationLivenessKind.UnknownRetention
                    => "The value may be retained across the generation boundary; prove non-retention before returning the owner.",
                _ => "End the owner-derived value before returning the generation."
            };
        }

        private void ReportBorrowedCallbackLifecycle(IInvocationOperation operation, OwnerState owner)
        {
            SemanticModel model = _context.Compilation.GetSemanticModel(operation.Syntax.SyntaxTree);
            foreach (IArgumentOperation argument in operation.Arguments)
            {
                if (!argument.Value.Syntax.DescendantNodesAndSelf().OfType<AnonymousFunctionExpressionSyntax>().Any())
                {
                    continue;
                }

                foreach (InvocationExpressionSyntax invocation in argument.Value.Syntax
                    .DescendantNodesAndSelf()
                    .OfType<InvocationExpressionSyntax>())
                {
                    if (invocation.Expression is not MemberAccessExpressionSyntax member)
                    {
                        continue;
                    }

                    string lifecycleName = member.Name.Identifier.ValueText;
                    if (lifecycleName is not ("ReturnMemoryToNativeMemory" or "ReturnMemoryToGarbageCollector"
                        or "ReleaseLeasesToNativeMemory" or "ReleaseLeasesToGarbageCollector" or "Dispose"))
                    {
                        continue;
                    }

                    ISymbol? receiver = model.GetSymbolInfo(member.Expression, _context.CancellationToken).Symbol;
                    if (!SymbolEqualityComparer.Default.Equals(receiver, owner.Symbol))
                    {
                        continue;
                    }

                    if (owner.IsUsing && !owner.IsRegion)
                    {
                        Report(
                            NativeAllocationDiagnosticDescriptors.ScopedLifecycle,
                            invocation,
                            owner.DisplayName,
                            lifecycleName);
                    }
                    else if (!owner.IsRegion)
                    {
                        ReportGenerationReturnLiveness(
                            owner,
                            lifecycleName,
                            invocation,
                            FindGenerationReturnLiveness(owner));
                    }
                    else
                    {
                        ReportGenerationReturnLiveness(
                            owner,
                            lifecycleName,
                            invocation,
                            FindGenerationReturnLiveness(owner));
                    }
                }
            }
        }

        private bool IsActivelyBorrowed(HandleState handle)
        {
            return _borrowScopes.Any(scope =>
                SymbolEqualityComparer.Default.Equals(scope.OwnerSymbol, handle.Owner.Symbol)
                && scope.HandleName == handle.DisplayName);
        }

        private bool CheckOwnerActive(OwnerState owner, SyntaxNode syntax, string operation)
        {
            if (owner.Ambiguous || owner.GenerationRelation == GenerationRelationKind.Unknown || owner.Disposed || owner.Returned || owner.Unleased)
            {
                Report(NativeAllocationDiagnosticDescriptors.InvalidLifecycle, syntax, owner.DisplayName, operation);
                return false;
            }

            return true;
        }

        private bool CheckHandleUse(HandleState handle, SyntaxNode syntax, string operation)
        {
            if (handle.Owner.IsRegion && handle.Owner.RegionScope is TextSpan scope && !scope.Contains(syntax.Span.Start))
            {
                Report(
                    NativeAllocationDiagnosticDescriptors.LocalEscape,
                    syntax,
                    handle.DisplayName,
                    "outside its NativeRegion scope");
                return false;
            }

            if (handle.Ambiguous
                || handle.GenerationRelation == GenerationRelationKind.Unknown
                || handle.Owner.Ambiguous
                || handle.Owner.GenerationRelation == GenerationRelationKind.Unknown
                || handle.Returned
                || handle.Owner.Returned
                || handle.Owner.Disposed
                || handle.Generation != handle.Owner.Generation
                    && !(handle.GenerationRelation == GenerationRelationKind.Current
                        && handle.Owner.GenerationRelation == GenerationRelationKind.Current))
            {
                Report(
                    NativeAllocationDiagnosticDescriptors.ReturnedHandle,
                    syntax,
                    handle.DisplayName,
                    operation);
                return false;
            }

            return true;
        }

        private void ReportHandleTransfer(HandleState handle, Target target)
        {
            if (handle.Owner.IsRegion)
            {
                Report(
                    NativeAllocationDiagnosticDescriptors.LocalEscape,
                    target.Syntax,
                    handle.DisplayName,
                    target.Symbol?.Name ?? "an escaping destination");
                return;
            }

            string destination = target.Symbol?.Name ?? "an escaping destination";
            RecordGenerationLiveness(
                handle,
                GenerationLivenessKind.AliasOrEscape,
                handle.DisplayName + " -> " + destination);

            if (target.Symbol is ILocalSymbol && !SymbolEqualityComparer.Default.Equals(handle.Symbol, target.Symbol))
            {
                Report(
                    NativeAllocationDiagnosticDescriptors.HandleAlias,
                    target.Syntax,
                    handle.DisplayName,
                    target.Symbol.Name);
                return;
            }

            Report(
                handle.Owner.IsRegion
                    ? NativeAllocationDiagnosticDescriptors.LocalEscape
                    : NativeAllocationDiagnosticDescriptors.PooledEscape,
                target.Syntax,
                handle.DisplayName,
                destination);
        }

        private static void RecordGenerationLiveness(
            HandleState handle,
            GenerationLivenessKind kind,
            string path)
        {
            if (handle.Owner.IsRegion || handle.Owner.Symbol is null || handle.Returned)
            {
                return;
            }

            handle.Owner.LivenessFacts.Add(new GenerationLivenessFact(
                handle.Generation,
                handle.GenerationRelation,
                kind,
                path));
        }

        private void ReportOwnerTransfer(OwnerState owner, Target target)
        {
            owner.ScopedOwnerEligible = false;
            Report(
                NativeAllocationDiagnosticDescriptors.OwnerAlias,
                target.Syntax,
                owner.DisplayName,
                target.Symbol?.Name ?? "an escaping destination");
        }

        private void ReportActiveHandlesAcrossBoundary(SyntaxNode syntax)
        {
            foreach (HandleState handle in _handles.Values)
            {
                if (!handle.Returned || handle.Ambiguous || handle.GenerationRelation == GenerationRelationKind.Unknown)
                {
                    if (!handle.Owner.IsRegion)
                    {
                        RecordGenerationLiveness(
                            handle,
                            GenerationLivenessKind.UnknownRetention,
                            handle.DisplayName + " -> an asynchronous boundary");
                    }
                    Report(NativeAllocationDiagnosticDescriptors.AcrossAsync, syntax, handle.DisplayName);
                }
            }
        }

        private void ReportActiveExit(SyntaxNode syntax)
        {
            foreach (HandleState handle in _handles.Values)
            {
                if ((!handle.Returned || handle.Ambiguous || handle.GenerationRelation == GenerationRelationKind.Unknown)
                    && !handle.IsUsing
                    && !handle.Owner.IsRegion
                    && !(handle.Owner.IsArena && handle.Owner.IsUsing))
                {
                    Report(
                        NativeAllocationDiagnosticDescriptors.LifetimeEscape,
                        syntax,
                        handle.DisplayName);
                }
            }

            foreach (OwnerState owner in _owners.Values)
            {
                if (!owner.IsExternalReceiver
                    && !owner.IsField
                    && !owner.IsUsing
                    && !owner.IsRegion
                    && owner.RequiresDeterministicReturn
                    && (!owner.Returned || owner.Ambiguous || owner.GenerationRelation == GenerationRelationKind.Unknown)
                    && (!owner.Disposed || owner.Ambiguous || owner.GenerationRelation == GenerationRelationKind.Unknown))
                {
                    Report(
                        NativeAllocationDiagnosticDescriptors.LifetimeEscape,
                        syntax,
                        owner.DisplayName);
                }
            }
        }

        private OwnerState? GetOwner(IOperation? operation)
        {
            if (operation is null)
            {
                return null;
            }

            ISymbol? symbol = GetSymbol(operation) ?? GetSemanticSymbol(operation);
            if (symbol is not null && _owners.TryGetValue(symbol, out OwnerState? existing))
            {
                return existing;
            }

            SemanticModel model = _context.Compilation.GetSemanticModel(operation.Syntax.SyntaxTree);
            ITypeSymbol? type = operation.Type
                ?? (symbol as ILocalSymbol)?.Type
                ?? model.GetTypeInfo(operation.Syntax, _context.CancellationToken).Type;
            if (!IsOwnerType(type))
            {
                return null;
            }

            OwnerState owner = new(
                symbol,
                type!,
                IsNativeRegion(type),
                IsNativeArena(type),
                isUsing: false,
                symbol is IFieldSymbol,
                requiresDeterministicReturn: false,
                operation.Syntax);
            if (symbol is not null)
            {
                owner.IsExternalReceiver = _context is OperationContextAdapter
                    || _isStandaloneClosureAnalysis
                    || operation.Syntax.Ancestors().Any(node => node is AnonymousFunctionExpressionSyntax)
                    || !IsSymbolDeclaredInsideAnalysisRoot(symbol);
                _owners[symbol] = owner;
            }

            return owner;
        }

        private bool IsSymbolDeclaredInsideAnalysisRoot(ISymbol symbol)
        {
            if (!_hasAnalysisRoot || _analysisRootTree is null)
            {
                return false;
            }

            foreach (SyntaxReference reference in symbol.DeclaringSyntaxReferences)
            {
                SyntaxNode declaration = reference.GetSyntax(_context.CancellationToken);
                if (ReferenceEquals(declaration.SyntaxTree, _analysisRootTree)
                    && _analysisRootSpan.Contains(declaration.SpanStart))
                {
                    return true;
                }
            }

            return false;
        }

        private ISymbol? GetSemanticSymbol(IOperation operation)
        {
            SemanticModel model = _context.Compilation.GetSemanticModel(operation.Syntax.SyntaxTree);
            return model.GetSymbolInfo(operation.Syntax, _context.CancellationToken).Symbol;
        }

        private HandleState? GetHandle(IOperation? operation)
        {
            if (operation is null || !IsHandleType(operation.Type))
            {
                return null;
            }

            ISymbol? symbol = GetSymbol(operation);
            if (symbol is not null && _handles.TryGetValue(symbol, out HandleState? existing))
            {
                return existing;
            }

            OwnerState owner = new(
                symbol: null,
                operation.Type!,
                IsNativeLocal(operation.Type),
                isArena: false,
                isUsing: false,
                isField: false,
                requiresDeterministicReturn: false,
                operation.Syntax);
            HandleState handle = new(symbol, owner, 0, isUsing: false, operation.Syntax);
            if (symbol is not null)
            {
                _handles[symbol] = handle;
            }

            return handle;
        }

        private bool HasFieldDisposalPath(IFieldSymbol field)
        {
            INamedTypeSymbol disposable = _context.Compilation.GetTypeByMetadataName("System.IDisposable")!;
            if (!field.ContainingType.AllInterfaces.Contains(disposable, SymbolEqualityComparer.Default))
            {
                return false;
            }

            IMethodSymbol? interfaceDispose = disposable.GetMembers("Dispose").OfType<IMethodSymbol>().FirstOrDefault();
            if (interfaceDispose is null)
            {
                return false;
            }

            IMethodSymbol? implementation = field.ContainingType.FindImplementationForInterfaceMember(interfaceDispose) as IMethodSymbol;
            HashSet<ISymbol> visiting = new(SymbolEqualityComparer.Default);
            bool implementationPath = implementation is not null && MethodDisposesField(implementation, field, visiting);
            if (implementationPath)
            {
                return true;
            }

            foreach (IMethodSymbol candidate in field.ContainingType.GetMembers("Dispose").OfType<IMethodSymbol>())
            {
                bool candidatePath = candidate.Parameters.Length == 1
                    && candidate.Parameters[0].Type.SpecialType == SpecialType.System_Boolean
                    && MethodDisposesField(candidate, field, new HashSet<ISymbol>(SymbolEqualityComparer.Default), knownBoolean: true)
                    && implementation is not null
                    && MethodCallsMethod(implementation, candidate, field);
                if (candidatePath)
                {
                    return true;
                }
            }

            return false;
        }

        private bool MethodCallsMethod(IMethodSymbol caller, IMethodSymbol candidate, IFieldSymbol field)
        {
            if (caller.DeclaringSyntaxReferences.Length == 0)
            {
                return false;
            }

            SyntaxNode declaration = caller.DeclaringSyntaxReferences[0].GetSyntax(_context.CancellationToken);
            SemanticModel model = _context.Compilation.GetSemanticModel(declaration.SyntaxTree);
            if (model.GetOperation(declaration, _context.CancellationToken) is not IMethodBodyOperation body)
            {
                return false;
            }

            FieldInvocationWalker walker = new();
            walker.Visit(body);
            return walker.Invocations.Any(invocation =>
            {
                if (invocation.TargetMethod.Name != candidate.Name
                    || invocation.TargetMethod.Parameters.Length != candidate.Parameters.Length
                    || invocation.TargetMethod.Parameters.Select(parameter => parameter.Type)
                        .SequenceEqual(candidate.Parameters.Select(parameter => parameter.Type), SymbolEqualityComparer.Default))
                {
                    return false;
                }

                IOperation? receiver = Unwrap(invocation.Instance);
                if (receiver is not null && receiver is not IInstanceReferenceOperation)
                {
                    return false;
                }

                return SymbolEqualityComparer.Default.Equals(
                    ResolveMostDerivedMethod(invocation.TargetMethod, field),
                    candidate);
            });
        }

        private bool MethodDisposesField(
            IMethodSymbol method,
            IFieldSymbol field,
            HashSet<ISymbol> visiting,
            bool? knownBoolean = null)
        {
            if (!visiting.Add(method) || method.DeclaringSyntaxReferences.Length == 0)
            {
                return false;
            }

            try
            {
                SyntaxNode declaration = method.DeclaringSyntaxReferences[0].GetSyntax(_context.CancellationToken);
                SemanticModel model = _context.Compilation.GetSemanticModel(declaration.SyntaxTree);
                if (model.GetOperation(declaration, _context.CancellationToken) is not IMethodBodyOperation body)
                {
                    return false;
                }

                ControlFlowGraph graph = ControlFlowGraph.Create(body, _context.CancellationToken);
                BasicBlock entry = graph.Blocks.First(block => block.Kind == BasicBlockKind.Entry);
                Dictionary<BasicBlock, bool> inStates = new();
                Dictionary<BasicBlock, bool> outStates = new();
                Queue<BasicBlock> work = new([entry]);

                while (work.Count != 0)
                {
                    _context.CancellationToken.ThrowIfCancellationRequested();
                    BasicBlock block = work.Dequeue();
                    bool incoming;
                    if (block == entry)
                    {
                        incoming = false;
                    }
                    else
                    {
                        bool[] predecessors = block.Predecessors
                            .Where(branch => branch.Source is not null
                                && outStates.ContainsKey(branch.Source)
                                && FieldSuccessors(graph, branch.Source!, method, knownBoolean).Contains(block))
                            .Select(branch => ApplyFieldFinalizers(graph, branch.Source!, block, outStates[branch.Source!], field, visiting, method, knownBoolean))
                            .ToArray();
                        if (predecessors.Length == 0)
                        {
                            continue;
                        }

                        incoming = predecessors.All(value => value);
                    }

                    if (inStates.TryGetValue(block, out bool oldIncoming) && oldIncoming == incoming)
                    {
                        continue;
                    }

                    inStates[block] = incoming;
                    bool outgoing = incoming || BlockDisposesField(block, field, visiting);
                    bool changed = !outStates.TryGetValue(block, out bool oldOutgoing) || oldOutgoing != outgoing;
                    outStates[block] = outgoing;
                    if (changed)
                    {
                        foreach (BasicBlock successor in FieldSuccessors(graph, block, method, knownBoolean))
                        {
                            work.Enqueue(successor);
                        }
                    }
                }

                return graph.Blocks
                    .Where(block => block.Kind == BasicBlockKind.Exit && outStates.ContainsKey(block))
                    .Select(block => outStates[block])
                    .All(value => value);
            }
            finally
            {
                visiting.Remove(method);
            }
        }

        private bool ApplyFieldFinalizers(
            ControlFlowGraph graph,
            BasicBlock source,
            BasicBlock destination,
            bool state,
            IFieldSymbol field,
            HashSet<ISymbol> visiting,
            IMethodSymbol method,
            bool? knownBoolean)
        {
            foreach (ControlFlowRegion pair in graph.Root.NestedRegions
                .SelectMany(FlattenRegions)
                .Where(region => region.Kind == ControlFlowRegionKind.TryAndFinally))
            {
                ControlFlowRegion? tryRegion = pair.NestedRegions.FirstOrDefault(region => region.Kind == ControlFlowRegionKind.Try);
                ControlFlowRegion? finallyRegion = pair.NestedRegions.FirstOrDefault(region => region.Kind == ControlFlowRegionKind.Finally);
                if (tryRegion is null || finallyRegion is null
                    || !ContainsBlock(tryRegion, source.Ordinal)
                    || ContainsBlock(pair, destination.Ordinal))
                {
                    continue;
                }

                state |= FieldRegionDisposesOnEveryPath(graph, finallyRegion, field, visiting, method, knownBoolean);
            }

            return state;
        }

        private bool BlockDisposesField(BasicBlock block, IFieldSymbol field, HashSet<ISymbol> visiting)
        {
            FieldInvocationWalker walker = new();
            foreach (IOperation operation in block.Operations)
            {
                walker.Visit(operation);
            }

            if (block.BranchValue is not null)
            {
                walker.Visit(block.BranchValue);
            }

            return walker.Invocations.Any(invocation => InvocationDisposesField(invocation, field, visiting));
        }

        private static IEnumerable<BasicBlock> FieldSuccessors(
            ControlFlowGraph graph,
            BasicBlock block,
            IMethodSymbol method,
            bool? knownBoolean)
        {
            IEnumerable<BasicBlock> successors = GraphSuccessors(graph, block);
            if (!knownBoolean.HasValue)
            {
                return successors;
            }

            IParameterSymbol? booleanParameter = method.Parameters
                .FirstOrDefault(parameter => parameter.Type.SpecialType == SpecialType.System_Boolean);
            if (booleanParameter is null
                || !IsKnownBooleanBranch(block, booleanParameter))
            {
                return successors;
            }

            bool branchIsNegated = block.BranchValue?.Syntax.ToString().TrimStart().StartsWith("!", StringComparison.Ordinal) == true;
            bool branchValue = branchIsNegated ? !knownBoolean.Value : knownBoolean.Value;
            BasicBlock? chosen = branchValue
                ? block.FallThroughSuccessor?.Destination
                : block.ConditionalSuccessor?.Destination;
            return chosen is null
                ? successors
                : successors.Where(successor => ReferenceEquals(successor, chosen));
        }

        private static bool IsKnownBooleanBranch(BasicBlock block, IParameterSymbol parameter)
        {
            if (IsParameterReference(block.BranchValue, parameter))
            {
                return true;
            }

            string text = block.BranchValue?.Syntax.ToString().Trim() ?? string.Empty;
            return text == parameter.Name || text == "!" + parameter.Name;
        }

        private bool FieldRegionDisposesOnEveryPath(
            ControlFlowGraph graph,
            ControlFlowRegion region,
            IFieldSymbol field,
            HashSet<ISymbol> visiting,
            IMethodSymbol method,
            bool? knownBoolean)
        {
            BasicBlock[] blocks = graph.Blocks
                .Where(block => ContainsBlock(region, block.Ordinal))
                .OrderBy(block => block.Ordinal)
                .ToArray();
            if (blocks.Length == 0)
            {
                return false;
            }

            HashSet<BasicBlock> members = [.. blocks];
            BasicBlock[] entries = blocks
                .Where(block => block.Predecessors.All(branch => branch.Source is null || !members.Contains(branch.Source)))
                .ToArray();
            if (entries.Length == 0)
            {
                entries = [blocks[0]];
            }

            Dictionary<BasicBlock, bool> incoming = new();
            Dictionary<BasicBlock, bool> outgoing = new();
            Queue<BasicBlock> work = new(entries);
            while (work.Count != 0)
            {
                BasicBlock block = work.Dequeue();
                bool state = entries.Contains(block)
                    ? false
                    : block.Predecessors
                        .Where(branch => branch.Source is not null
                            && members.Contains(branch.Source)
                            && outgoing.ContainsKey(branch.Source)
                            && FieldSuccessors(graph, branch.Source!, method, knownBoolean).Contains(block))
                        .Select(branch => outgoing[branch.Source!])
                        .DefaultIfEmpty(false)
                        .All(value => value);
                bool changed = !incoming.TryGetValue(block, out bool previousIncoming) || previousIncoming != state;
                incoming[block] = state;
                bool next = state || BlockDisposesField(block, field, visiting);
                bool outputChanged = !outgoing.TryGetValue(block, out bool previousOutgoing) || previousOutgoing != next;
                outgoing[block] = next;
                if (changed || outputChanged)
                {
                    foreach (BasicBlock successor in FieldSuccessors(graph, block, method, knownBoolean).Where(members.Contains))
                    {
                        work.Enqueue(successor);
                    }
                }
            }

            BasicBlock[] exits = blocks
                .Where(block => outgoing.ContainsKey(block)
                    && FieldSuccessors(graph, block, method, knownBoolean).All(successor => !members.Contains(successor)))
                .ToArray();
            return exits.Length != 0 && exits.All(block => outgoing.TryGetValue(block, out bool state) && state);
        }

        private bool InvocationDisposesField(
            IInvocationOperation invocation,
            IFieldSymbol field,
            HashSet<ISymbol> visiting)
        {
            IOperation? receiver = Unwrap(invocation.Instance);
            if (receiver is IFieldReferenceOperation fieldReference
                && SymbolEqualityComparer.Default.Equals(fieldReference.Field, field)
                && ToLifecycleEffect(invocation.TargetMethod.Name) is not LifecycleEffect.None)
            {
                return true;
            }

            if (receiver is IInstanceReferenceOperation
                && invocation.TargetMethod.DeclaringSyntaxReferences.Length != 0)
            {
                IMethodSymbol target = ResolveMostDerivedMethod(invocation.TargetMethod, field);
                bool? knownBoolean = invocation.Arguments.Length == 1
                    && invocation.Arguments[0].Value.ConstantValue.HasValue
                    && invocation.Arguments[0].Value.ConstantValue.Value is bool value
                    ? value
                    : null;
                return MethodDisposesField(target, field, visiting, knownBoolean);
            }

            return false;
        }

        private static IMethodSymbol ResolveMostDerivedMethod(IMethodSymbol method, IFieldSymbol field)
        {
            INamedTypeSymbol containingType = field.ContainingType;
            if (!method.IsVirtual && !method.IsOverride)
            {
                return method;
            }

            foreach (IMethodSymbol candidate in containingType.GetMembers(method.Name).OfType<IMethodSymbol>())
            {
                if (candidate.Parameters.Length == method.Parameters.Length
                    && candidate.Parameters.Select(parameter => parameter.Type)
                        .SequenceEqual(method.Parameters.Select(parameter => parameter.Type), SymbolEqualityComparer.Default))
                {
                    return candidate;
                }
            }

            return method;
        }

        private LifecycleEffect GetLifecycleEffect(IMethodSymbol method, IParameterSymbol? parameter)
        {
            if (parameter is null)
            {
                return LifecycleEffect.None;
            }

            if (!method.ReturnsVoid || method.DeclaringSyntaxReferences.Length != 1)
            {
                return LifecycleEffect.None;
            }

            string cacheKey = method.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "|" + parameter.Name;
            if (_lifecycleSummaries.TryGetValue(cacheKey, out LifecycleEffect cached))
            {
                return cached;
            }

            if (!_lifecycleSummaryVisiting.Add(cacheKey))
            {
                return LifecycleEffect.None;
            }

            try
            {
                LifecycleEffect result = AnalyzeLifecycleSummary(method, parameter);
                _lifecycleSummaries[cacheKey] = result;
                return result;
            }
            finally
            {
                _lifecycleSummaryVisiting.Remove(cacheKey);
            }
        }

        private bool IsNonRetainingOwnerParameter(
            IMethodSymbol method,
            IParameterSymbol? parameter)
        {
            if (parameter is null
                || IsNativeRegion(parameter.Type)
                || method.DeclaringSyntaxReferences.Length != 1)
            {
                return false;
            }

            string cacheKey = method.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat)
                + "|borrow|"
                + parameter.Name;
            if (_nonRetainingOwnerParameters.TryGetValue(
                cacheKey,
                out bool cached))
            {
                return cached;
            }

            if (!_nonRetainingOwnerParameterVisiting.Add(cacheKey))
            {
                return false;
            }

            try
            {
                SyntaxNode syntax = method.DeclaringSyntaxReferences[0]
                    .GetSyntax(_context.CancellationToken);
                SemanticModel model = _context.Compilation.GetSemanticModel(
                    syntax.SyntaxTree);
                IOperation? operation = model.GetOperation(
                    syntax,
                    _context.CancellationToken);
                if (operation is null)
                {
                    _nonRetainingOwnerParameters[cacheKey] = false;
                    return false;
                }

                bool result = operation.DescendantsAndSelf()
                    .OfType<IParameterReferenceOperation>()
                    .Where(reference => SymbolEqualityComparer.Default.Equals(
                        reference.Parameter,
                        parameter))
                    .All(reference => IsNonRetainingOwnerReference(
                        reference,
                        syntax));
                _nonRetainingOwnerParameters[cacheKey] = result;
                return result;
            }
            finally
            {
                _nonRetainingOwnerParameterVisiting.Remove(cacheKey);
            }
        }

        private bool IsNonRetainingOwnerReference(
            IParameterReferenceOperation reference,
            SyntaxNode methodSyntax)
        {
            for (IOperation? ancestor = reference.Parent;
                ancestor is not null;
                ancestor = ancestor.Parent)
            {
                if (ancestor is IAnonymousFunctionOperation
                    or ILocalFunctionOperation)
                {
                    return false;
                }

                if (ancestor.Syntax == methodSyntax)
                {
                    break;
                }
            }

            IOperation current = reference;
            while (current.Parent is IConversionOperation
                or IParenthesizedOperation)
            {
                current = current.Parent;
            }

            if (current.Parent is IInvocationOperation invocation
                && ReferenceEquals(Unwrap(invocation.Instance), reference))
            {
                return true;
            }

            if (current.Parent is IPropertyReferenceOperation property
                && ReferenceEquals(Unwrap(property.Instance), reference))
            {
                return true;
            }

            return current.Parent is IArgumentOperation argument
                && argument.Parent is IInvocationOperation call
                && IsNonRetainingOwnerParameter(
                    call.TargetMethod,
                    argument.Parameter);
        }

        private LifecycleEffect AnalyzeLifecycleSummary(IMethodSymbol method, IParameterSymbol parameter)
        {
            if (method.DeclaringSyntaxReferences.Length != 1)
            {
                return LifecycleEffect.None;
            }

            SyntaxNode syntax = method.DeclaringSyntaxReferences[0].GetSyntax(_context.CancellationToken);
            if (syntax.DescendantNodes().OfType<CatchClauseSyntax>().Any())
            {
                return LifecycleEffect.None;
            }

            SemanticModel model = _context.Compilation.GetSemanticModel(syntax.SyntaxTree);
            if (model.GetOperation(syntax, _context.CancellationToken) is not IMethodBodyOperation body)
            {
                return LifecycleEffect.None;
            }

            ControlFlowGraph graph;
            try
            {
                graph = ControlFlowGraph.Create(body, _context.CancellationToken);
            }
            catch (ArgumentException)
            {
                return LifecycleEffect.None;
            }

            BasicBlock entry = graph.Blocks.First(block => block.Kind == BasicBlockKind.Entry);
            Dictionary<BasicBlock, LifecycleSummaryState> entryStates = new();
            Dictionary<BasicBlock, LifecycleSummaryState> exitStates = new();
            Queue<BasicBlock> work = new([entry]);
            while (work.Count != 0)
            {
                _context.CancellationToken.ThrowIfCancellationRequested();
                BasicBlock block = work.Dequeue();
                LifecycleSummaryState incoming;
                if (block == entry)
                {
                    incoming = new();
                }
                else
                {
                    LifecycleSummaryState[] predecessorStates = block.Predecessors
                        .Where(branch => branch.Source is not null && exitStates.ContainsKey(branch.Source))
                        .Select(branch => exitStates[branch.Source!])
                        .ToArray();
                    if (predecessorStates.Length == 0)
                    {
                        continue;
                    }

                    incoming = LifecycleSummaryState.Merge(predecessorStates);
                }

                if (entryStates.TryGetValue(block, out LifecycleSummaryState? oldEntry)
                    && oldEntry.EquivalentTo(incoming))
                {
                    continue;
                }

                entryStates[block] = incoming.Clone();
                LifecycleSummaryState outgoing = incoming.Clone();
                LifecycleSummaryWalker walker = new(parameter, ResolveNestedLifecycleEffect);
                foreach (IOperation operation in block.Operations)
                {
                    walker.Visit(operation);
                }

                if (block.BranchValue is not null)
                {
                    walker.Visit(block.BranchValue);
                }

                if (walker.Unknown)
                {
                    outgoing.Unknown = true;
                }

                foreach (LifecycleEffect effect in walker.Effects)
                {
                    if (outgoing.Unknown || outgoing.Effect is not LifecycleEffect.None)
                    {
                        outgoing.Unknown = true;
                    }
                    else
                    {
                        outgoing.Effect = effect;
                    }
                }

                bool changed = !exitStates.TryGetValue(block, out LifecycleSummaryState? oldExit)
                    || !oldExit.EquivalentTo(outgoing);
                exitStates[block] = outgoing;
                if (changed)
                {
                    foreach (BasicBlock successor in GraphSuccessors(graph, block))
                    {
                        work.Enqueue(successor);
                    }
                }
            }

            LifecycleSummaryState[] exits = graph.Blocks
                .Where(block => block.Kind == BasicBlockKind.Exit && exitStates.ContainsKey(block))
                .Select(block => exitStates[block])
                .ToArray();
            if (exits.Length == 0 || exits.Any(state => state.Unknown || state.Effect is LifecycleEffect.None))
            {
                return LifecycleEffect.None;
            }

            LifecycleEffect effectAtFirstExit = exits[0].Effect;
            return exits.All(state => state.Effect == effectAtFirstExit)
                ? effectAtFirstExit
                : LifecycleEffect.None;

            LifecycleEffect ResolveNestedLifecycleEffect(IMethodSymbol nestedMethod, IParameterSymbol nestedParameter)
            {
                return GetLifecycleEffect(nestedMethod, nestedParameter);
            }
        }

        private static LifecycleEffect ToLifecycleEffect(string methodName)
        {
            return methodName switch
            {
                "ReturnMemoryToNativeMemory" => LifecycleEffect.ReturnMemoryToNativeMemory,
                "ReturnMemoryToGarbageCollector" => LifecycleEffect.ReturnMemoryToGarbageCollector,
                "ReleaseLeasesToNativeMemory" => LifecycleEffect.ReleaseLeasesToNativeMemory,
                "ReleaseLeasesToGarbageCollector" => LifecycleEffect.ReleaseLeasesToGarbageCollector,
                "LeaseFromMemory" => LifecycleEffect.LeaseFromMemory,
                "RecycleScoped" => LifecycleEffect.RecycleScoped,
                "Dispose" => LifecycleEffect.Dispose,
                _ => LifecycleEffect.None
            };
        }

        private static string ToMethodName(LifecycleEffect effect)
        {
            return effect switch
            {
                LifecycleEffect.ReturnMemoryToNativeMemory => "ReturnMemoryToNativeMemory",
                LifecycleEffect.ReturnMemoryToGarbageCollector => "ReturnMemoryToGarbageCollector",
                LifecycleEffect.ReleaseLeasesToNativeMemory => "ReleaseLeasesToNativeMemory",
                LifecycleEffect.ReleaseLeasesToGarbageCollector => "ReleaseLeasesToGarbageCollector",
                LifecycleEffect.LeaseFromMemory => "LeaseFromMemory",
                LifecycleEffect.RecycleScoped => "RecycleScoped",
                LifecycleEffect.Dispose => "Dispose",
                _ => string.Empty
            };
        }

        private enum LifecycleEffect
        {
            None,
            ReturnMemoryToNativeMemory,
            ReturnMemoryToGarbageCollector,
            ReleaseLeasesToNativeMemory,
            ReleaseLeasesToGarbageCollector,
            LeaseFromMemory,
            RecycleScoped,
            Dispose
        }

        private sealed class LifecycleSummaryState
        {
            internal LifecycleEffect Effect { get; set; }
            internal bool Unknown { get; set; }

            internal LifecycleSummaryState Clone()
            {
                return new LifecycleSummaryState
                {
                    Effect = Effect,
                    Unknown = Unknown
                };
            }

            internal bool EquivalentTo(LifecycleSummaryState other)
            {
                return Effect == other.Effect && Unknown == other.Unknown;
            }

            internal static LifecycleSummaryState Merge(IEnumerable<LifecycleSummaryState> states)
            {
                LifecycleSummaryState[] paths = states.ToArray();
                LifecycleSummaryState merged = new();
                foreach (LifecycleSummaryState path in paths)
                {
                    if (path.Unknown)
                    {
                        merged.Unknown = true;
                        continue;
                    }

                    if (merged.Effect is LifecycleEffect.None)
                    {
                        merged.Effect = path.Effect;
                    }
                    else if (path.Effect is LifecycleEffect.None || path.Effect != merged.Effect)
                    {
                        merged.Unknown = true;
                    }
                }

                if (paths.Any(path => path.Effect is LifecycleEffect.None)
                    && paths.Any(path => path.Effect is not LifecycleEffect.None))
                {
                    merged.Unknown = true;
                }

                return merged;
            }
        }

        private sealed class LifecycleSummaryWalker : OperationWalker
        {
            private readonly IParameterSymbol _parameter;
            private readonly Func<IMethodSymbol, IParameterSymbol, LifecycleEffect> _resolveNestedLifecycleEffect;

            internal LifecycleSummaryWalker(
                IParameterSymbol parameter,
                Func<IMethodSymbol, IParameterSymbol, LifecycleEffect> resolveNestedLifecycleEffect)
            {
                _parameter = parameter;
                _resolveNestedLifecycleEffect = resolveNestedLifecycleEffect;
            }

            internal bool Unknown { get; private set; }
            internal List<LifecycleEffect> Effects { get; } = [];

            public override void VisitInvocation(IInvocationOperation operation)
            {
                LifecycleEffect effect = ToLifecycleEffect(operation.TargetMethod.Name);
                IOperation? receiver = Unwrap(operation.Instance);
                bool exactReceiver = receiver is IParameterReferenceOperation parameterReference
                    && SymbolEqualityComparer.Default.Equals(parameterReference.Parameter, _parameter);
                if (effect is not LifecycleEffect.None && exactReceiver)
                {
                    Effects.Add(effect);
                }
                else if (effect is LifecycleEffect.None
                    && operation.TargetMethod.ReturnsVoid
                    && operation.Arguments.Count(argument => IsParameterReference(argument.Value, _parameter)) == 1)
                {
                    IArgumentOperation argument = operation.Arguments.First(argument => IsParameterReference(argument.Value, _parameter));
                    LifecycleEffect nestedEffect = _resolveNestedLifecycleEffect(operation.TargetMethod, argument.Parameter!);
                    if (nestedEffect is not LifecycleEffect.None
                        && !ContainsParameterReference(operation.Instance, _parameter)
                        && !operation.Arguments.Any(other => other != argument && ContainsParameterReference(other.Value, _parameter)))
                    {
                        Effects.Add(nestedEffect);
                    }
                    else
                    {
                        Unknown = true;
                    }
                }
                else if (ContainsParameterReference(operation.Instance, _parameter)
                    || operation.Arguments.Any(argument => ContainsParameterReference(argument.Value, _parameter)))
                {
                    Unknown = true;
                }

                base.VisitInvocation(operation);
            }

            public override void VisitSimpleAssignment(ISimpleAssignmentOperation operation)
            {
                if (IsParameterReference(operation.Target, _parameter)
                    || ContainsParameterReference(operation.Value, _parameter))
                {
                    Unknown = true;
                }

                base.VisitSimpleAssignment(operation);
            }

            public override void VisitVariableDeclarator(IVariableDeclaratorOperation operation)
            {
                if (ContainsParameterReference(operation.Initializer?.Value, _parameter))
                {
                    Unknown = true;
                }

                base.VisitVariableDeclarator(operation);
            }

            public override void VisitArgument(IArgumentOperation operation)
            {
                if (operation.Parent is IInvocationOperation invocation
                    && invocation.TargetMethod.ReturnsVoid
                    && IsParameterReference(operation.Value, _parameter)
                    && invocation.Arguments.Count(argument => IsParameterReference(argument.Value, _parameter)) == 1
                    && _resolveNestedLifecycleEffect(invocation.TargetMethod, operation.Parameter!) is not LifecycleEffect.None)
                {
                    base.VisitArgument(operation);
                    return;
                }

                if (ContainsParameterReference(operation.Value, _parameter))
                {
                    Unknown = true;
                }

                base.VisitArgument(operation);
            }

            public override void VisitReturn(IReturnOperation operation)
            {
                if (ContainsParameterReference(operation.ReturnedValue, _parameter))
                {
                    Unknown = true;
                }

                base.VisitReturn(operation);
            }

            public override void VisitAnonymousFunction(IAnonymousFunctionOperation operation)
            {
                Unknown = true;
            }

            public override void VisitLocalFunction(ILocalFunctionOperation operation)
            {
                Unknown = true;
            }
        }

        private static bool IsParameterReference(IOperation? operation, IParameterSymbol parameter)
        {
            IOperation? unwrapped = Unwrap(operation);
            return unwrapped is IParameterReferenceOperation reference
                && SymbolEqualityComparer.Default.Equals(reference.Parameter, parameter);
        }

        private static bool ContainsParameterReference(IOperation? operation, IParameterSymbol parameter)
        {
            if (operation is null)
            {
                return false;
            }

            ParameterReferenceWalker walker = new(parameter);
            walker.Visit(operation);
            return walker.Found;
        }

        private sealed class ParameterReferenceWalker : OperationWalker
        {
            private readonly IParameterSymbol _parameter;

            internal ParameterReferenceWalker(IParameterSymbol parameter)
            {
                _parameter = parameter;
            }

            internal bool Found { get; private set; }

            public override void VisitParameterReference(IParameterReferenceOperation operation)
            {
                if (SymbolEqualityComparer.Default.Equals(operation.Parameter, _parameter))
                {
                    Found = true;
                }

                base.VisitParameterReference(operation);
            }
        }

        private sealed class FieldInvocationWalker : OperationWalker
        {
            internal List<IInvocationOperation> Invocations { get; } = [];

            public override void VisitInvocation(IInvocationOperation operation)
            {
                Invocations.Add(operation);
                base.VisitInvocation(operation);
            }

            public override void VisitAnonymousFunction(IAnonymousFunctionOperation operation)
            {
            }

            public override void VisitLocalFunction(ILocalFunctionOperation operation)
            {
            }
        }

        private static bool RequiresDeterministicReturn(IObjectCreationOperation operation)
        {
            bool sawPolicy = false;
            foreach (IArgumentOperation argument in operation.Arguments)
            {
                if (argument.Parameter?.Name == "returnMemoryOnDispose" && argument.Value.ConstantValue.HasValue)
                {
                    return argument.Value.ConstantValue.Value is 1;
                }

                if (argument.Parameter?.Name == "returnMemoryOnDispose")
                {
                    sawPolicy = true;
                }
            }

            return sawPolicy;
        }

        private enum DeclarationActivation
        {
            Active,
            Unleased,
            Unknown
        }

        private static DeclarationActivation GetDeclarationActivation(IObjectCreationOperation operation)
        {
            foreach (IArgumentOperation argument in operation.Arguments)
            {
                if (argument.Parameter?.Name != "doNotLeaseOnDeclaration")
                {
                    continue;
                }

                if (!argument.Value.ConstantValue.HasValue)
                {
                    return DeclarationActivation.Unknown;
                }

                return argument.Value.ConstantValue.Value is bool value
                    ? value ? DeclarationActivation.Unleased : DeclarationActivation.Active
                    : DeclarationActivation.Unknown;
            }

            return DeclarationActivation.Active;
        }

        private bool IsHandleCreatingInvocation(IOperation operation)
        {
            return operation is IInvocationOperation invocation
                && (((invocation.TargetMethod.Name is "Rent" or "LeaseScoped") && IsNativePool(invocation.TargetMethod.ContainingType))
                    || ((invocation.TargetMethod.Name is "Lease" or "LeaseScoped") && IsNativeRegion(invocation.TargetMethod.ContainingType))
                    || ((invocation.TargetMethod.Name is "Scratch" or "ScratchScoped") && IsNativeArena(invocation.TargetMethod.ContainingType)));
        }

        private static bool IsNonRetainingCompositeLeaseOperation(IInvocationOperation operation) =>
            (operation.TargetMethod.Name is "Access" or "InitializeScoped")
            && operation.TargetMethod.ContainingType.ToDisplayString() == "Supprocom.NativeAllocationManagement.NativeLeaseOperations";

        private static bool IsScopedGroupInitialization(
            IInvocationOperation operation) =>
            operation.TargetMethod.Name == "InitializeScoped"
            && operation.TargetMethod.ContainingType.ToDisplayString()
                == "Supprocom.NativeAllocationManagement.NativeLeaseOperations";

        private static Target FindTarget(IOperation operation)
        {
            IOperation current = operation;
            while (current.Parent is IConversionOperation conversion && conversion.Operand == current)
            {
                current = conversion;
            }

            if (current.Parent is IVariableInitializerOperation initializer
                && initializer.Value == current
                && initializer.Parent is IVariableDeclaratorOperation declarator)
            {
                return new Target(declarator.Symbol, declarator.Syntax);
            }

            if (current.Parent is IFieldInitializerOperation fieldInitializer
                && fieldInitializer.InitializedFields.Length == 1)
            {
                return new Target(fieldInitializer.InitializedFields[0], fieldInitializer.Syntax);
            }

            if (current.Parent is ISimpleAssignmentOperation assignment && assignment.Value == current)
            {
                return GetTarget(assignment.Target);
            }

            return new Target(null, operation.Syntax);
        }

        private static Target GetTarget(IOperation operation)
        {
            IOperation target = Unwrap(operation) ?? operation;
            return new Target(GetSymbol(target), target.Syntax);
        }

        private static IOperation? Unwrap(IOperation? operation)
        {
            while (operation is IConversionOperation conversion)
            {
                operation = conversion.Operand;
            }

            return operation;
        }

        private static ISymbol? GetSymbol(IOperation? operation)
        {
            return operation switch
            {
                ILocalReferenceOperation local => local.Local,
                IFieldReferenceOperation field => field.Field,
                IParameterReferenceOperation parameter => parameter.Parameter,
                IPropertyReferenceOperation property => property.Property,
                _ => null
            };
        }

        private static ITypeSymbol? GetSymbolType(ISymbol symbol)
        {
            return symbol switch
            {
                ILocalSymbol local => local.Type,
                IFieldSymbol field => field.Type,
                IParameterSymbol parameter => parameter.Type,
                IPropertySymbol property => property.Type,
                _ => null
            };
        }

        private bool IsOwnerType(ITypeSymbol? type)
        {
            return IsNativePool(type) || IsNativeRegion(type) || IsNativeArena(type);
        }

        private bool IsHandleType(ITypeSymbol? type)
        {
            return IsNativePooled(type) || IsNativeLocal(type) || IsNativeArenaLease(type);
        }

        private bool IsNativeTransfer(ITypeSymbol? type)
        {
            return NativeSymbols.Is(type, _symbols.Transfer);
        }

        private bool IsNativeLeaseView(ITypeSymbol? type)
        {
            return NativeSymbols.Is(type, _symbols.LeaseView);
        }

        private bool IsNativePool(ITypeSymbol? type)
        {
            return NativeSymbols.Is(type, _symbols.Pool);
        }

        private bool IsNativeRegion(ITypeSymbol? type)
        {
            return NativeSymbols.Is(type, _symbols.Region);
        }

        private bool IsNativeArena(ITypeSymbol? type)
        {
            return NativeSymbols.Is(type, _symbols.Arena);
        }

        private bool IsNativePooled(ITypeSymbol? type)
        {
            return NativeSymbols.Is(type, _symbols.Pooled);
        }

        private bool IsNativeLocal(ITypeSymbol? type)
        {
            return NativeSymbols.Is(type, _symbols.Local);
        }

        private bool IsNativeArenaLease(ITypeSymbol? type)
        {
            return NativeSymbols.Is(type, _symbols.ArenaLease);
        }

        private bool IsUsingSyntax(SyntaxNode syntax, ISymbol? symbol)
        {
            if (symbol is not null && _usingResourceSymbols.Contains(symbol))
            {
                return true;
            }

            UsingStatementSyntax? usingStatement = syntax.AncestorsAndSelf()
                .OfType<UsingStatementSyntax>()
                .FirstOrDefault(statement => IsDirectUsingInitializer(statement, syntax, symbol));
            if (usingStatement is not null)
            {
                return true;
            }

            return syntax.AncestorsAndSelf()
                .OfType<LocalDeclarationStatementSyntax>()
                .Any(statement => statement.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)
                    && IsDirectUsingDeclarationInitializer(statement, syntax, symbol));
        }

        private static bool IsDirectScopedLocalInitialization(SyntaxNode syntax, ISymbol? symbol)
        {
            if (symbol is not ILocalSymbol)
            {
                return false;
            }

            LocalDeclarationStatementSyntax? declaration = syntax.AncestorsAndSelf()
                .OfType<LocalDeclarationStatementSyntax>()
                .FirstOrDefault();
            bool isScoped = declaration is not null
                && (declaration.Modifiers.Any(modifier =>
                        modifier.IsKind(SyntaxKind.ScopedKeyword)
                        || string.Equals(modifier.ValueText, "scoped", StringComparison.Ordinal))
                    || declaration.ToString().TrimStart().StartsWith("scoped ", StringComparison.Ordinal));
            return declaration is not null
                && isScoped
                && declaration.Declaration.Variables.Any(variable =>
                    variable.Identifier.ValueText == symbol.Name
                    && variable.Initializer?.Value is SyntaxNode initializer
                    && initializer.Span.Contains(syntax.Span));
        }

        private TextSpan? GetUsingScope(SyntaxNode syntax, ISymbol? symbol)
        {
            UsingStatementSyntax? usingStatement = syntax.AncestorsAndSelf()
                .OfType<UsingStatementSyntax>()
                .FirstOrDefault(statement => IsDirectUsingInitializer(statement, syntax, symbol));
            if (usingStatement is not null)
            {
                return usingStatement.Statement is BlockSyntax block
                    ? block.Span
                    : null;
            }

            return null;
        }

        private static bool IsDirectBracedRegionUsingStatement(SyntaxNode syntax, ISymbol? symbol)
        {
            return syntax.AncestorsAndSelf()
                .OfType<UsingStatementSyntax>()
                .Any(statement => statement.Statement is BlockSyntax
                    && IsDirectUsingInitializer(statement, syntax, symbol));
        }

        private static bool IsDirectUsingInitializer(UsingStatementSyntax statement, SyntaxNode syntax, ISymbol? symbol)
        {
            if (statement.Expression is not null)
            {
                return symbol is null && statement.Expression.Span.Contains(syntax.Span);
            }

            if (statement.Declaration is null)
            {
                return false;
            }

            return statement.Declaration.Variables.Any(variable =>
                variable.Initializer?.Value is SyntaxNode initializer
                && initializer.Span.Contains(syntax.Span)
                && (symbol is null || variable.Identifier.ValueText == symbol.Name));
        }

        private static bool IsDirectUsingDeclarationInitializer(
            LocalDeclarationStatementSyntax statement,
            SyntaxNode syntax,
            ISymbol? symbol)
        {
            return statement.Declaration.Variables.Any(variable =>
                variable.Initializer?.Value is SyntaxNode initializer
                && initializer.Span.Contains(syntax.Span)
                && (symbol is null || variable.Identifier.ValueText == symbol.Name));
        }

        private void ReportMissingScopedCompletion(OwnerState owner, SyntaxNode syntax)
        {
            if (owner.Symbol is not null && !_reportedScopedCompletions.Add(owner.Symbol))
            {
                return;
            }

            Report(
                NativeAllocationDiagnosticDescriptors.MissingScopedCompletion,
                syntax,
                owner.DisplayName);
        }

        private void Report(DiagnosticDescriptor descriptor, SyntaxNode syntax, params object[] arguments)
        {
            ReportCore(descriptor, syntax, provenance: null, arguments);
        }

        private void ReportWithProvenance(
            DiagnosticDescriptor descriptor,
            SyntaxNode syntax,
            string provenance,
            params object[] arguments)
        {
            ReportCore(descriptor, syntax, provenance, arguments);
        }

        private void ReportCore(
            DiagnosticDescriptor descriptor,
            SyntaxNode syntax,
            string? provenance,
            params object[] arguments)
        {
            if (_suppressDiagnostics)
            {
                return;
            }

            string key = descriptor.Id + ":" + syntax.SpanStart + ":" + string.Join("|", arguments);
            if (!_reported.Add(key))
            {
                return;
            }

            FileLinePositionSpan line = syntax.GetLocation().GetLineSpan();
            string diagnosticProvenance = provenance
                ?? string.Join(" -> ", arguments.Select(argument => argument?.ToString() ?? string.Empty));
            string sourceFile = string.IsNullOrEmpty(line.Path)
                ? syntax.SyntaxTree.FilePath is { Length: > 0 } filePath ? filePath : "<in-memory>"
                : line.Path;
            ImmutableDictionary<string, string?> properties = ImmutableDictionary<string, string?>.Empty
                .Add("NAM.DiagnosticId", descriptor.Id)
                .Add("NAM.Provenance", diagnosticProvenance)
                .Add("NAM.ProvenancePath", diagnosticProvenance)
                .Add("NAM.Source", $"{sourceFile}:{line.StartLinePosition.Line + 1}:{line.StartLinePosition.Character + 1}")
                .Add("NAM.SourceFile", sourceFile)
                .Add("NAM.SourceLine", (line.StartLinePosition.Line + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Add("NAM.SourceColumn", (line.StartLinePosition.Character + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Add("NAM.Operation", descriptor.Title.ToString())
                .Add("NAM.OperationId", descriptor.Id);
            _context.ReportDiagnostic(Diagnostic.Create(
                descriptor,
                syntax.GetLocation(),
                properties: properties,
                messageArgs: arguments));
        }

        private enum GenerationRelationKind
        {
            Exact,
            Current,
            Unknown
        }

        private enum GenerationLivenessKind
        {
            RootReference,
            ActiveBorrow,
            AliasOrEscape,
            UnknownRetention
        }

        private sealed class GenerationLivenessFact
        {
            internal GenerationLivenessFact(
                int generation,
                GenerationRelationKind generationRelation,
                GenerationLivenessKind kind,
                string path)
            {
                Generation = generation;
                GenerationRelation = generationRelation;
                Kind = kind;
                Path = path;
            }

            internal int Generation { get; }
            internal GenerationRelationKind GenerationRelation { get; }
            internal GenerationLivenessKind Kind { get; }
            internal string Path { get; }

            internal bool SameAs(GenerationLivenessFact other)
            {
                return Generation == other.Generation
                    && GenerationRelation == other.GenerationRelation
                    && Kind == other.Kind
                    && Path == other.Path;
            }
        }

        private readonly struct GenerationReturnLiveness
        {
            internal GenerationReturnLiveness(GenerationLivenessKind kind, string path, bool isScoped)
            {
                Kind = kind;
                Path = path;
                IsScoped = isScoped;
            }

            internal GenerationLivenessKind Kind { get; }
            internal string Path { get; }
            internal bool IsScoped { get; }
        }

        private sealed class OwnerState
        {
            internal OwnerState(
                ISymbol? symbol,
                ITypeSymbol type,
                bool isRegion,
                bool isArena,
                bool isUsing,
                bool isField,
                bool requiresDeterministicReturn,
                SyntaxNode syntax,
                TextSpan? regionScope = null)
            {
                Symbol = symbol;
                Type = type;
                IsRegion = isRegion;
                IsArena = isArena;
                IsUsing = isUsing;
                IsField = isField;
                RequiresDeterministicReturn = requiresDeterministicReturn;
                Syntax = syntax;
                RegionScope = regionScope;
            }

            internal ISymbol? Symbol { get; }
            internal ITypeSymbol Type { get; }
            internal bool IsRegion { get; }
            internal bool IsArena { get; }
            internal bool IsUsing { get; }
            internal bool IsField { get; }
            internal bool RequiresDeterministicReturn { get; }
            internal bool Returned { get; set; }
            internal bool Disposed { get; set; }
            internal bool Unleased { get; set; }
            internal bool ScopedOwnerEligible { get; set; }
            internal bool IsExternalReceiver { get; set; }
            internal bool Ambiguous { get; set; }
            internal bool ScopedPending { get; set; }
            internal bool ScopedPendingAmbiguous { get; set; }
            internal int Generation { get; set; }
            internal GenerationRelationKind GenerationRelation { get; set; } = GenerationRelationKind.Exact;
            internal SyntaxNode Syntax { get; }
            internal TextSpan? RegionScope { get; }
            internal List<GenerationLivenessFact> LivenessFacts { get; } = [];
            internal string DisplayName => Symbol?.Name ?? Type.Name;

            internal OwnerState Clone()
            {
                OwnerState copy = new(
                    Symbol,
                    Type,
                    IsRegion,
                    IsArena,
                    IsUsing,
                    IsField,
                    RequiresDeterministicReturn,
                    Syntax,
                    RegionScope);
                copy.Returned = Returned;
                copy.Disposed = Disposed;
                copy.Unleased = Unleased;
                copy.ScopedOwnerEligible = ScopedOwnerEligible;
                copy.IsExternalReceiver = IsExternalReceiver;
                copy.Ambiguous = Ambiguous;
                copy.ScopedPending = ScopedPending;
                copy.ScopedPendingAmbiguous = ScopedPendingAmbiguous;
                copy.Generation = Generation;
                copy.GenerationRelation = GenerationRelation;
                copy.LivenessFacts.AddRange(LivenessFacts);
                return copy;
            }
        }

        private sealed class HandleState
        {
            internal HandleState(ISymbol? symbol, OwnerState owner, int generation, bool isUsing, SyntaxNode syntax)
            {
                Symbol = symbol;
                Owner = owner;
                Generation = generation;
                IsUsing = isUsing;
                Syntax = syntax;
            }

            internal ISymbol? Symbol { get; }
            internal OwnerState Owner { get; }
            internal int Generation { get; set; }
            internal bool IsUsing { get; set; }
            internal bool IsScoped { get; set; }
            internal bool Returned { get; set; }
            internal bool Ambiguous { get; set; }
            internal GenerationRelationKind GenerationRelation { get; set; } = GenerationRelationKind.Exact;
            internal SyntaxNode Syntax { get; }
            internal string DisplayName => Symbol?.Name ?? Owner.Type.Name;

            internal HandleState Clone(OwnerState owner)
            {
                HandleState copy = new(Symbol, owner, Generation, IsUsing, Syntax)
                {
                    Returned = Returned,
                    IsScoped = IsScoped
                };
                copy.Ambiguous = Ambiguous;
                copy.GenerationRelation = GenerationRelation;
                return copy;
            }
        }

        private enum TransferStatus
        {
            Unowned,
            Active,
            Moved,
            Disposed,
            Ambiguous
        }

        private sealed class TransferState
        {
            private TransferState(
                ISymbol symbol,
                string ownershipIdentity,
                TransferStatus status,
                bool mustEnd,
                bool isUsing,
                SyntaxNode syntax)
            {
                Symbol = symbol;
                OwnershipIdentity = ownershipIdentity;
                Status = status;
                MustEnd = mustEnd;
                IsUsing = isUsing;
                Syntax = syntax;
            }

            internal ISymbol Symbol { get; }
            internal string OwnershipIdentity { get; }
            internal TransferStatus Status { get; set; }
            internal bool MustEnd { get; set; }
            internal bool IsUsing { get; }
            internal SyntaxNode Syntax { get; }
            internal string DisplayName => Symbol.Name;

            internal static TransferState CreateExternal(
                ISymbol symbol,
                SyntaxNode syntax) =>
                new(
                    symbol,
                    CreateSymbolIdentity(symbol),
                    TransferStatus.Active,
                    mustEnd: false,
                    isUsing: false,
                    syntax);

            internal static TransferState Create(
                ISymbol symbol,
                SyntaxNode origin,
                TransferStatus status,
                bool mustEnd,
                bool isUsing) =>
                new(
                    symbol,
                    CreateSyntaxIdentity(origin),
                    status,
                    mustEnd,
                    isUsing,
                    origin);

            internal TransferState Clone() =>
                new(
                    Symbol,
                    OwnershipIdentity,
                    Status,
                    MustEnd,
                    IsUsing,
                    Syntax);

            internal TransferState CloneFor(
                ISymbol symbol,
                bool mustEnd,
                bool isUsing,
                SyntaxNode syntax) =>
                new(
                    symbol,
                    OwnershipIdentity,
                    Status,
                    mustEnd,
                    isUsing,
                    syntax);

            private static string CreateSyntaxIdentity(SyntaxNode syntax) =>
                $"{syntax.SyntaxTree.FilePath}:{syntax.SpanStart}:{syntax.RawKind}";

            private static string CreateSymbolIdentity(ISymbol symbol)
            {
                Location? source = symbol.Locations.FirstOrDefault(location => location.IsInSource);
                return source is null
                    ? "external:" + symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                    : $"{source.SourceTree?.FilePath}:{source.SourceSpan.Start}:{symbol.Name}";
            }
        }

        private sealed class FlowSnapshot
        {
            internal FlowSnapshot(
                Dictionary<ISymbol, OwnerState> owners,
                Dictionary<ISymbol, HandleState> handles,
                Dictionary<ISymbol, TransferState> transfers,
                List<RegionScope> regions,
                HashSet<OwnerState> borrowedOwners)
            {
                Owners = owners;
                Handles = handles;
                Transfers = transfers;
                Regions = regions;
                BorrowedOwners = borrowedOwners;
            }

            internal Dictionary<ISymbol, OwnerState> Owners { get; }
            internal Dictionary<ISymbol, HandleState> Handles { get; }
            internal Dictionary<ISymbol, TransferState> Transfers { get; }
            internal List<RegionScope> Regions { get; }
            internal HashSet<OwnerState> BorrowedOwners { get; }
        }

        private readonly struct Target
        {
            internal Target(ISymbol? symbol, SyntaxNode syntax)
            {
                Symbol = symbol;
                Syntax = syntax;
            }

            internal ISymbol? Symbol { get; }
            internal SyntaxNode Syntax { get; }
        }

        private readonly struct RegionScope
        {
            internal RegionScope(string name, TextSpan scope, int start)
            {
                Name = name;
                Scope = scope;
                Start = start;
            }

            internal string Name { get; }
            internal TextSpan Scope { get; }
            internal int Start { get; }
        }
    }
}
