using System.Collections.Immutable;
using System.Collections.Generic;
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
            startContext.RegisterOperationBlockAction(blockContext =>
            {
                blockContext.CancellationToken.ThrowIfCancellationRequested();
                MethodFlowAnalyzer analyzer = new(blockContext);
                foreach (IOperation operationBlock in blockContext.OperationBlocks)
                {
                    analyzer.AnalyzeOperationBlock(operationBlock);
                }

                analyzer.Complete();
            });
        });
    }

    private sealed class MethodFlowAnalyzer : OperationWalker
    {
        private readonly OperationBlockAnalysisContext _context;
        private readonly Dictionary<ISymbol, OwnerState> _owners = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<ISymbol, HandleState> _handles = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<string, LifecycleEffect> _lifecycleSummaries = new(StringComparer.Ordinal);
        private readonly HashSet<string> _lifecycleSummaryVisiting = new(StringComparer.Ordinal);
        private readonly List<RegionScope> _regions = [];
        private readonly HashSet<OwnerState> _borrowedOwners = [];
        private readonly HashSet<ISymbol> _usingResourceSymbols = new(SymbolEqualityComparer.Default);
        private readonly List<FlowSnapshot> _exitSnapshots = [];
        private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
        private int _closureDepth;
        private int _finallyProtectionDepth;
        private int _finallyDepth;
        private bool _cfgMode;
        private bool _suppressDiagnostics;

        internal MethodFlowAnalyzer(OperationBlockAnalysisContext context)
        {
            _context = context;
        }

        internal void AnalyzeOperationBlock(IOperation operationBlock)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();

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

        internal void Complete()
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            IEnumerable<FlowSnapshot> exits = _exitSnapshots.Count == 0
                ? [CaptureSnapshot()]
                : _exitSnapshots;

            foreach (FlowSnapshot exit in exits)
            {
                _context.CancellationToken.ThrowIfCancellationRequested();
                foreach (HandleState handle in exit.Handles.Values)
                {
                    if (!IsVisibleAtMethodExit(handle.Symbol))
                    {
                        continue;
                    }

                    if (handle.Returned && !handle.Ambiguous && handle.GenerationRelation != GenerationRelationKind.Unknown || handle.IsUsing || handle.Owner.IsRegion)
                    {
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
                    if (owner.IsField || owner.IsUsing || owner.IsRegion || (owner.Returned && !owner.Ambiguous && owner.GenerationRelation != GenerationRelationKind.Unknown) || (owner.Disposed && !owner.Ambiguous && owner.GenerationRelation != GenerationRelationKind.Unknown))
                    {
                        continue;
                    }

                    if (owner.RequiresDeterministicReturn)
                    {
                        Report(
                            NativeAllocationDiagnosticDescriptors.LifetimeEscape,
                            owner.Syntax,
                            owner.DisplayName);
                    }
                }
            }
        }

        private bool IsVisibleAtMethodExit(ISymbol? symbol)
        {
            if (symbol is not ILocalSymbol local || local.DeclaringSyntaxReferences.Length == 0)
            {
                return true;
            }

            SyntaxNode declaration = local.DeclaringSyntaxReferences[0].GetSyntax(_context.CancellationToken);
            if (declaration.AncestorsAndSelf().OfType<SwitchSectionSyntax>().Any())
            {
                return false;
            }

            BlockSyntax? containingBlock = declaration.AncestorsAndSelf().OfType<BlockSyntax>().FirstOrDefault();
            BlockSyntax? methodBlock = declaration.AncestorsAndSelf()
                .Select(GetCallableBody)
                .FirstOrDefault(body => body is not null);
            return containingBlock is null || ReferenceEquals(containingBlock, methodBlock);
        }

        private static BlockSyntax? GetCallableBody(SyntaxNode syntax)
        {
            return syntax switch
            {
                MethodDeclarationSyntax method => method.Body,
                ConstructorDeclarationSyntax constructor => constructor.Body,
                DestructorDeclarationSyntax destructor => destructor.Body,
                OperatorDeclarationSyntax operatorDeclaration => operatorDeclaration.Body,
                ConversionOperatorDeclarationSyntax conversion => conversion.Body,
                AccessorDeclarationSyntax accessor => accessor.Body,
                LocalFunctionStatementSyntax localFunction => localFunction.Body,
                _ => null
            };
        }

        private void AnalyzeControlFlowGraph(ControlFlowGraph graph)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            BasicBlock entry = graph.Blocks.First(block => block.Kind == BasicBlockKind.Entry);
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
                    incoming = EmptySnapshot();
                }
                else
                {
                    FlowSnapshot[] predecessorStates = block.Predecessors
                        .Where(branch => branch.Source is not null && exitStates.ContainsKey(branch.Source))
                        .Select(branch => ApplyFinallyOnEdge(graph, branch.Source!, block, exitStates[branch.Source!]))
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

                entryStates[block] = CloneSnapshot(incoming);
                RestoreSnapshot(incoming);
                VisitBlock(block);
                FlowSnapshot outgoing = CaptureSnapshot();
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

                RestoreSnapshot(incoming);
                VisitBlock(block);
            }

            _cfgMode = false;
        }

        private void VisitBlock(BasicBlock block)
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

        private FlowSnapshot ApplyFinallyOnEdge(
            ControlFlowGraph graph,
            BasicBlock source,
            BasicBlock destination,
            FlowSnapshot state)
        {
            foreach (ControlFlowRegion tryAndFinally in graph.Root.NestedRegions
                .SelectMany(FlattenRegions)
                .Where(region => region.Kind == ControlFlowRegionKind.TryAndFinally))
            {
                ControlFlowRegion? tryRegion = tryAndFinally.NestedRegions
                    .FirstOrDefault(region => region.Kind == ControlFlowRegionKind.Try);
                ControlFlowRegion? finallyRegion = tryAndFinally.NestedRegions
                    .FirstOrDefault(region => region.Kind == ControlFlowRegionKind.Finally);
                if (tryRegion is null || finallyRegion is null
                    || !ContainsBlock(tryRegion, source.Ordinal)
                    || ContainsBlock(tryAndFinally, destination.Ordinal))
                {
                    continue;
                }

                RestoreSnapshot(state);
                foreach (BasicBlock cleanupBlock in graph.Blocks
                    .Where(block => ContainsBlock(finallyRegion, block.Ordinal))
                    .OrderBy(block => block.Ordinal))
                {
                    VisitBlock(cleanupBlock);
                }

                state = CaptureSnapshot();
            }

            return state;
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

        private static IEnumerable<BasicBlock> GraphSuccessors(ControlFlowGraph graph, BasicBlock block)
        {
            HashSet<BasicBlock> successors = [];
            if (block.ConditionalSuccessor?.Destination is BasicBlock conditional)
            {
                successors.Add(conditional);
            }

            if (block.FallThroughSuccessor?.Destination is BasicBlock fallThrough)
            {
                successors.Add(fallThrough);
            }

            foreach (BasicBlock candidate in graph.Blocks)
            {
                if (candidate.Predecessors.Any(branch => ReferenceEquals(branch.Source, block)))
                {
                    successors.Add(candidate);
                }
            }

            foreach (BasicBlock successor in successors)
            {
                yield return successor;
            }
        }

        private static FlowSnapshot EmptySnapshot()
        {
            return new FlowSnapshot(
                new Dictionary<ISymbol, OwnerState>(SymbolEqualityComparer.Default),
                new Dictionary<ISymbol, HandleState>(SymbolEqualityComparer.Default),
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

            HashSet<OwnerState> borrowed = [];
            foreach (OwnerState owner in _borrowedOwners)
            {
                borrowed.Add(ownerCopies.TryGetValue(owner, out OwnerState? copy) ? copy : owner.Clone());
            }

            return new FlowSnapshot(owners, handles, [.. _regions], borrowed);
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
                    merged.Ambiguous |= owner.Ambiguous;
                }

                if (!presentOnEveryPath)
                {
                    merged.Ambiguous = true;
                }

                if (paths.Any(path => path.Owners.TryGetValue(symbol, out OwnerState? owner) && owner.Returned != merged.Returned)
                    || paths.Any(path => path.Owners.TryGetValue(symbol, out OwnerState? owner) && owner.Disposed != merged.Disposed))
                {
                    merged.Ambiguous = true;
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
                    mergedHandle.Ambiguous = true;
                }

                if (paths.Any(path => path.Handles.TryGetValue(symbol, out HandleState? handle) && handle.Returned != mergedHandle.Returned))
                {
                    mergedHandle.Ambiguous = true;
                }

                mergedHandle.GenerationRelation = MergeHandleGenerationRelation(paths, symbol, first, mergedHandle);
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

            return new FlowSnapshot(owners, handles, regions, borrowed);
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

            HashSet<OwnerState> borrowed = [];
            foreach (OwnerState owner in snapshot.BorrowedOwners)
            {
                borrowed.Add(ownerCopies.TryGetValue(owner, out OwnerState? copy) ? copy : owner.Clone());
            }

            return new FlowSnapshot(owners, handles, [.. snapshot.Regions], borrowed);
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
            if (states.Any(state => state.Returned != first.Returned || state.Disposed != first.Disposed))
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

        private static bool SnapshotEquivalent(FlowSnapshot left, FlowSnapshot right)
        {
            if (left.Owners.Count != right.Owners.Count || left.Handles.Count != right.Handles.Count)
            {
                return false;
            }

            foreach (KeyValuePair<ISymbol, OwnerState> pair in left.Owners)
            {
                if (!right.Owners.TryGetValue(pair.Key, out OwnerState? other)
                    || pair.Value.Returned != other.Returned
                    || pair.Value.Disposed != other.Disposed
                    || pair.Value.Ambiguous != other.Ambiguous
                    || pair.Value.Generation != other.Generation
                    || pair.Value.GenerationRelation != other.GenerationRelation)
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

            if (borrowedOwner is not null)
            {
                _borrowedOwners.Add(borrowedOwner);
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
                tryPath = CaptureSnapshot();
                FlowSnapshot catchEntry = MergeSnapshotsForResult(before, tryPath);
                paths = [tryPath, catchEntry];

                foreach (ICatchClauseOperation catchClause in operation.Catches)
                {
                    RestoreSnapshot(catchEntry);
                    Visit(catchClause);
                    paths.Add(CaptureSnapshot());
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
                        paths[index] = CaptureSnapshot();
                    }
                }
                finally
                {
                    _finallyDepth = previousFinallyDepth;
                }
            }

            MergeSnapshots(paths.ToArray());
        }

        public override void VisitPropertyReference(IPropertyReferenceOperation operation)
        {
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
            if (value is not null && value is not IObjectCreationOperation && !IsHandleCreatingInvocation(value))
            {
                Target target = GetTarget(operation.Target);
                if (IsHandleType(value.Type) && GetHandle(value) is HandleState handle)
                {
                    ReportHandleTransfer(handle, target);
                }
                else if (IsOwnerType(value.Type) && GetOwner(value) is OwnerState owner)
                {
                    ReportOwnerTransfer(owner, target);
                }
            }

            base.VisitSimpleAssignment(operation);
        }

        public override void VisitVariableDeclarator(IVariableDeclaratorOperation operation)
        {
            IOperation? value = Unwrap(operation.Initializer?.Value);
            if (value is not null && value is not IObjectCreationOperation && !IsHandleCreatingInvocation(value))
            {
                Target target = new(operation.Symbol, operation.Syntax);
                if (IsHandleType(value.Type) && GetHandle(value) is HandleState handle)
                {
                    ReportHandleTransfer(handle, target);
                }
                else if (IsOwnerType(value.Type) && GetOwner(value) is OwnerState owner)
                {
                    ReportOwnerTransfer(owner, target);
                }
            }

            base.VisitVariableDeclarator(operation);
        }

        public override void VisitArgument(IArgumentOperation operation)
        {
            IOperation? value = Unwrap(operation.Value);
            if (value is not null && value is not IObjectCreationOperation)
            {
                if (IsHandleType(value.Type) && GetHandle(value) is HandleState handle)
                {
                    string callName = operation.Parent is IInvocationOperation invocation
                        ? invocation.TargetMethod.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
                        : "an unknown call";
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
                        && GetLifecycleEffect(invocation.TargetMethod, operation.Parameter) is not LifecycleEffect.None)
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
                base.VisitAnonymousFunction(operation);
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
                base.VisitLocalFunction(operation);
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
                if (_handles.TryGetValue(operation.Local, out HandleState? handle) && !handle.Returned)
                {
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
                    Report(
                        NativeAllocationDiagnosticDescriptors.OwnerAlias,
                        operation.Syntax,
                        owner.DisplayName,
                        "a closure");
                }
            }

            base.VisitLocalReference(operation);
        }

        public override void VisitConversion(IConversionOperation operation)
        {
            IOperation? operand = Unwrap(operation.Operand);
            if (operand is not null && IsHandleType(operand.Type) && !IsHandleType(operation.Type))
            {
                if (GetHandle(operand) is HandleState handle)
                {
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

        public override void VisitTuple(ITupleOperation operation)
        {
            foreach (IOperation element in operation.Elements)
            {
                if (IsHandleType(element.Type) && GetHandle(Unwrap(element)) is HandleState handle)
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
                if (IsHandleType(element.Type) && GetHandle(Unwrap(element)) is HandleState handle)
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

        private void RegisterOwner(IObjectCreationOperation operation)
        {
            Target target = FindTarget(operation);
            bool isRegion = IsNativeRegion(operation.Type);
            bool isUsing = IsUsingSyntax(operation.Syntax, target.Symbol);
            bool requiresDeterministicReturn = RequiresDeterministicReturn(operation);
            TextSpan? regionScope = isRegion ? GetUsingScope(operation.Syntax, target.Symbol) : null;

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
                isUsing,
                target.Symbol is IFieldSymbol,
                requiresDeterministicReturn,
                operation.Syntax,
                regionScope);
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
            if (target.Symbol is not ILocalSymbol)
            {
                Report(
                    owner.IsRegion
                        ? NativeAllocationDiagnosticDescriptors.LocalEscape
                        : NativeAllocationDiagnosticDescriptors.PooledEscape,
                    operation.Syntax,
                    "the new allocation",
                    target.Symbol?.Name ?? "an escaping destination");
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
            _handles[target.Symbol] = handle;
        }

        private void ProcessOwnerLifecycle(OwnerState owner, string name, SyntaxNode syntax)
        {
            if (name is "Rent" or "Allocate")
            {
                CheckOwnerActive(owner, syntax, name);
                return;
            }

            if (name is "ReturnToNativeMemory" or "ReturnToGarbageCollector")
            {
                if (_borrowedOwners.Contains(owner))
                {
                    string borrow = FindBorrowDisplayName(owner);
                    Report(
                        NativeAllocationDiagnosticDescriptors.BorrowBlocksReturn,
                        syntax,
                        owner.DisplayName,
                        name,
                        borrow);
                    return;
                }

                if (owner.IsUsing && !owner.IsRegion)
                {
                    Report(NativeAllocationDiagnosticDescriptors.ScopedLifecycle, syntax, owner.DisplayName, name);
                    return;
                }

                if (!CheckOwnerActive(owner, syntax, name))
                {
                    return;
                }

                owner.Returned = true;
                owner.Ambiguous = false;
                owner.GenerationRelation = owner.GenerationRelation == GenerationRelationKind.Unknown
                    ? GenerationRelationKind.Current
                    : owner.GenerationRelation;
                owner.Generation++;
                foreach (HandleState handle in _handles.Values)
                {
                    if (ReferenceEquals(handle.Owner, owner) && !handle.Returned)
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
                if (owner.IsUsing && !owner.IsRegion)
                {
                    Report(NativeAllocationDiagnosticDescriptors.ScopedLifecycle, syntax, owner.DisplayName, name);
                    return;
                }

                if (owner.IsRegion || owner.Ambiguous || owner.GenerationRelation == GenerationRelationKind.Unknown || !owner.Returned || owner.Disposed)
                {
                    Report(NativeAllocationDiagnosticDescriptors.InvalidLifecycle, syntax, owner.DisplayName, name);
                    return;
                }

                owner.Returned = false;
                owner.Ambiguous = false;
                return;
            }

            if (name == "Dispose")
            {
                if (_borrowedOwners.Contains(owner))
                {
                    Report(
                        NativeAllocationDiagnosticDescriptors.BorrowBlocksReturn,
                        syntax,
                        owner.DisplayName,
                        name,
                        FindBorrowDisplayName(owner));
                    return;
                }

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
                owner.Returned = true;
                foreach (HandleState handle in _handles.Values)
                {
                    if (ReferenceEquals(handle.Owner, owner))
                    {
                        handle.Returned = true;
                    }
                }
            }
        }

        private void ReportBorrowedCallbackLifecycle(IInvocationOperation operation, OwnerState owner)
        {
            if (owner.Symbol is null)
            {
                return;
            }

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
                    if (ToLifecycleEffect(lifecycleName) is LifecycleEffect.None)
                    {
                        continue;
                    }

                    ISymbol? receiver = model.GetSymbolInfo(member.Expression, _context.CancellationToken).Symbol;
                    if (SymbolEqualityComparer.Default.Equals(receiver, owner.Symbol))
                    {
                        Report(
                            NativeAllocationDiagnosticDescriptors.BorrowBlocksReturn,
                            invocation,
                            owner.DisplayName,
                            lifecycleName,
                            FindBorrowDisplayName(owner));
                    }
                }
            }
        }

        private bool CheckOwnerActive(OwnerState owner, SyntaxNode syntax, string operation)
        {
            if (owner.Ambiguous || owner.GenerationRelation == GenerationRelationKind.Unknown || owner.Disposed || owner.Returned)
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
                target.Symbol?.Name ?? "an escaping destination");
        }

        private void ReportOwnerTransfer(OwnerState owner, Target target)
        {
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
                    Report(NativeAllocationDiagnosticDescriptors.AcrossAsync, syntax, handle.DisplayName);
                }
            }
        }

        private void ReportActiveExit(SyntaxNode syntax)
        {
            foreach (HandleState handle in _handles.Values)
            {
                if ((!handle.Returned || handle.Ambiguous || handle.GenerationRelation == GenerationRelationKind.Unknown) && !handle.IsUsing && !handle.Owner.IsRegion)
                {
                    Report(
                        NativeAllocationDiagnosticDescriptors.LifetimeEscape,
                        syntax,
                        handle.DisplayName);
                }
            }

            foreach (OwnerState owner in _owners.Values)
            {
                if (!owner.IsField
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

        private string FindBorrowDisplayName(OwnerState owner)
        {
            foreach (HandleState handle in _handles.Values)
            {
                if (ReferenceEquals(handle.Owner, owner) && !handle.Returned)
                {
                    return handle.DisplayName + " -> scoped callback";
                }
            }

            return owner.DisplayName + " -> scoped callback";
        }

        private OwnerState? GetOwner(IOperation? operation)
        {
            if (operation is null || !IsOwnerType(operation.Type))
            {
                return null;
            }

            ISymbol? symbol = GetSymbol(operation);
            if (symbol is not null && _owners.TryGetValue(symbol, out OwnerState? existing))
            {
                return existing;
            }

            OwnerState owner = new(
                symbol,
                operation.Type!,
                IsNativeRegion(operation.Type),
                isUsing: false,
                symbol is IFieldSymbol,
                requiresDeterministicReturn: false,
                operation.Syntax);
            if (symbol is not null)
            {
                _owners[symbol] = owner;
            }

            return owner;
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
                "ReturnToNativeMemory" => LifecycleEffect.ReturnToNativeMemory,
                "ReturnToGarbageCollector" => LifecycleEffect.ReturnToGarbageCollector,
                "LeaseFromMemory" => LifecycleEffect.LeaseFromMemory,
                "Dispose" => LifecycleEffect.Dispose,
                _ => LifecycleEffect.None
            };
        }

        private static string ToMethodName(LifecycleEffect effect)
        {
            return effect switch
            {
                LifecycleEffect.ReturnToNativeMemory => "ReturnToNativeMemory",
                LifecycleEffect.ReturnToGarbageCollector => "ReturnToGarbageCollector",
                LifecycleEffect.LeaseFromMemory => "LeaseFromMemory",
                LifecycleEffect.Dispose => "Dispose",
                _ => string.Empty
            };
        }

        private enum LifecycleEffect
        {
            None,
            ReturnToNativeMemory,
            ReturnToGarbageCollector,
            LeaseFromMemory,
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
                if (argument.Parameter?.Name == "returnOnDispose" && argument.Value.ConstantValue.HasValue)
                {
                    return argument.Value.ConstantValue.Value is 1;
                }

                if (argument.Parameter?.Name == "returnOnDispose")
                {
                    sawPolicy = true;
                }
            }

            return sawPolicy;
        }

        private static bool IsHandleCreatingInvocation(IOperation operation)
        {
            return operation is IInvocationOperation invocation
                && ((invocation.TargetMethod.Name == "Rent" && IsNativePool(invocation.Instance?.Type))
                    || (invocation.TargetMethod.Name == "Allocate" && IsNativeRegion(invocation.Instance?.Type)));
        }

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

        private static bool IsOwnerType(ITypeSymbol? type)
        {
            return IsNativePool(type) || IsNativeRegion(type);
        }

        private static bool IsHandleType(ITypeSymbol? type)
        {
            return IsNativePooled(type) || IsNativeLocal(type);
        }

        private static bool IsNativePool(ITypeSymbol? type)
        {
            return IsNamedType(type, "NativePool");
        }

        private static bool IsNativeRegion(ITypeSymbol? type)
        {
            return IsNamedType(type, "NativeRegion");
        }

        private static bool IsNativePooled(ITypeSymbol? type)
        {
            return IsNamedType(type, "Pooled");
        }

        private static bool IsNativeLocal(ITypeSymbol? type)
        {
            return IsNamedType(type, "Local");
        }

        private static bool IsNamedType(ITypeSymbol? type, string name)
        {
            return type is INamedTypeSymbol named
                && named.Name == name
                && named.ContainingNamespace.ToDisplayString() == "Supprocom.NativeAllocationManagement";
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

        private TextSpan? GetUsingScope(SyntaxNode syntax, ISymbol? symbol)
        {
            UsingStatementSyntax? usingStatement = syntax.AncestorsAndSelf()
                .OfType<UsingStatementSyntax>()
                .FirstOrDefault(statement => IsDirectUsingInitializer(statement, syntax, symbol));
            if (usingStatement is not null)
            {
                return usingStatement.Statement?.Span ?? usingStatement.Span;
            }

            LocalDeclarationStatementSyntax? usingDeclaration = syntax.AncestorsAndSelf()
                .OfType<LocalDeclarationStatementSyntax>()
                .FirstOrDefault(statement => statement.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)
                    && IsDirectUsingDeclarationInitializer(statement, syntax, symbol));
            return usingDeclaration?.Parent?.Span ?? usingDeclaration?.Span;
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

        private void Report(DiagnosticDescriptor descriptor, SyntaxNode syntax, params object[] arguments)
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
            string provenance = string.Join(" -> ", arguments.Select(argument => argument?.ToString() ?? string.Empty));
            string sourceFile = string.IsNullOrEmpty(line.Path)
                ? syntax.SyntaxTree.FilePath is { Length: > 0 } filePath ? filePath : "<in-memory>"
                : line.Path;
            ImmutableDictionary<string, string?> properties = ImmutableDictionary<string, string?>.Empty
                .Add("NAM.DiagnosticId", descriptor.Id)
                .Add("NAM.Provenance", provenance)
                .Add("NAM.ProvenancePath", provenance)
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

        private sealed class OwnerState
        {
            internal OwnerState(
                ISymbol? symbol,
                ITypeSymbol type,
                bool isRegion,
                bool isUsing,
                bool isField,
                bool requiresDeterministicReturn,
                SyntaxNode syntax,
                TextSpan? regionScope = null)
            {
                Symbol = symbol;
                Type = type;
                IsRegion = isRegion;
                IsUsing = isUsing;
                IsField = isField;
                RequiresDeterministicReturn = requiresDeterministicReturn;
                Syntax = syntax;
                RegionScope = regionScope;
            }

            internal ISymbol? Symbol { get; }
            internal ITypeSymbol Type { get; }
            internal bool IsRegion { get; }
            internal bool IsUsing { get; }
            internal bool IsField { get; }
            internal bool RequiresDeterministicReturn { get; }
            internal bool Returned { get; set; }
            internal bool Disposed { get; set; }
            internal bool Ambiguous { get; set; }
            internal int Generation { get; set; }
            internal GenerationRelationKind GenerationRelation { get; set; } = GenerationRelationKind.Exact;
            internal SyntaxNode Syntax { get; }
            internal TextSpan? RegionScope { get; }
            internal string DisplayName => Symbol?.Name ?? Type.Name;

            internal OwnerState Clone()
            {
                OwnerState copy = new(
                    Symbol,
                    Type,
                    IsRegion,
                    IsUsing,
                    IsField,
                    RequiresDeterministicReturn,
                    Syntax,
                    RegionScope);
                copy.Returned = Returned;
                copy.Disposed = Disposed;
                copy.Ambiguous = Ambiguous;
                copy.Generation = Generation;
                copy.GenerationRelation = GenerationRelation;
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
            internal bool Returned { get; set; }
            internal bool Ambiguous { get; set; }
            internal GenerationRelationKind GenerationRelation { get; set; } = GenerationRelationKind.Exact;
            internal SyntaxNode Syntax { get; }
            internal string DisplayName => Symbol?.Name ?? Owner.Type.Name;

            internal HandleState Clone(OwnerState owner)
            {
                HandleState copy = new(Symbol, owner, Generation, IsUsing, Syntax)
                {
                    Returned = Returned
                };
                copy.Ambiguous = Ambiguous;
                copy.GenerationRelation = GenerationRelation;
                return copy;
            }
        }

        private sealed class FlowSnapshot
        {
            internal FlowSnapshot(
                Dictionary<ISymbol, OwnerState> owners,
                Dictionary<ISymbol, HandleState> handles,
                List<RegionScope> regions,
                HashSet<OwnerState> borrowedOwners)
            {
                Owners = owners;
                Handles = handles;
                Regions = regions;
                BorrowedOwners = borrowedOwners;
            }

            internal Dictionary<ISymbol, OwnerState> Owners { get; }
            internal Dictionary<ISymbol, HandleState> Handles { get; }
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
