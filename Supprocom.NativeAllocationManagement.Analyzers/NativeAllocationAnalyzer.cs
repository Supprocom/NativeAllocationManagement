using System.Collections.Immutable;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
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
                MethodFlowAnalyzer analyzer = new(blockContext);
                foreach (IOperation operationBlock in blockContext.OperationBlocks)
                {
                    analyzer.Visit(operationBlock);
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
        private readonly List<RegionScope> _regions = [];
        private readonly HashSet<OwnerState> _borrowedOwners = [];
        private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
        private int _closureDepth;

        internal MethodFlowAnalyzer(OperationBlockAnalysisContext context)
        {
            _context = context;
        }

        internal void Complete()
        {
            foreach (HandleState handle in _handles.Values)
            {
                if (handle.Returned || handle.IsUsing || handle.Owner.IsRegion)
                {
                    continue;
                }

                Report(
                    NativeAllocationDiagnosticDescriptors.LifetimeEscape,
                    handle.Syntax,
                    handle.DisplayName);
            }

            foreach (OwnerState owner in _owners.Values)
            {
                if (owner.IsField || owner.IsUsing || owner.IsRegion || owner.Returned || owner.Disposed)
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

        private FlowSnapshot CaptureSnapshot()
        {
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

                    merged.Returned |= owner.Returned;
                    merged.Disposed |= owner.Disposed;
                    merged.Generation = Math.Max(merged.Generation, owner.Generation);
                }

                if (!presentOnEveryPath)
                {
                    merged.Returned = true;
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

                    mergedHandle.Returned |= handle.Returned;
                }

                if (!presentOnEveryPath)
                {
                    mergedHandle.Returned = true;
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

        public override void VisitObjectCreation(IObjectCreationOperation operation)
        {
            if (IsOwnerType(operation.Type))
            {
                RegisterOwner(operation);
            }

            base.VisitObjectCreation(operation);
        }

        public override void VisitInvocation(IInvocationOperation operation)
        {
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
            visit();
            FlowSnapshot after = CaptureSnapshot();
            MergeSnapshots(before, after);
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
            Visit(operation.Body);
            FlowSnapshot tryPath = CaptureSnapshot();
            FlowSnapshot catchEntry = MergeSnapshotsForResult(before, tryPath);
            List<FlowSnapshot> paths = [tryPath, catchEntry];

            foreach (ICatchClauseOperation catchClause in operation.Catches)
            {
                RestoreSnapshot(catchEntry);
                Visit(catchClause);
                paths.Add(CaptureSnapshot());
            }

            if (operation.Finally is not null)
            {
                for (int index = 0; index < paths.Count; index++)
                {
                    RestoreSnapshot(paths[index]);
                    Visit(operation.Finally);
                    paths[index] = CaptureSnapshot();
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

            base.VisitReturn(operation);
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
            bool isUsing = IsUsingSyntax(operation.Syntax);
            bool requiresDeterministicReturn = RequiresDeterministicReturn(operation);
            TextSpan? regionScope = isRegion ? GetUsingScope(operation.Syntax) : null;

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

            if (isRegion && isUsing && GetUsingScope(operation.Syntax) is TextSpan scope)
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
                IsUsingSyntax(operation.Syntax),
                operation.Syntax);
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

                long oldGeneration = owner.Generation;
                owner.Returned = true;
                owner.Generation++;
                foreach (HandleState handle in _handles.Values)
                {
                    if (ReferenceEquals(handle.Owner, owner) && handle.Generation == oldGeneration)
                    {
                        handle.Returned = true;
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

                if (owner.IsRegion || !owner.Returned || owner.Disposed)
                {
                    Report(NativeAllocationDiagnosticDescriptors.InvalidLifecycle, syntax, owner.DisplayName, name);
                    return;
                }

                owner.Returned = false;
                owner.Generation++;
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

                if (owner.Disposed)
                {
                    Report(NativeAllocationDiagnosticDescriptors.InvalidLifecycle, syntax, owner.DisplayName, name);
                    return;
                }

                owner.Disposed = true;
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

        private bool CheckOwnerActive(OwnerState owner, SyntaxNode syntax, string operation)
        {
            if (owner.Disposed || owner.Returned)
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

            if (handle.Returned || handle.Owner.Returned || handle.Owner.Disposed || handle.Generation != handle.Owner.Generation)
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
                if (!handle.Returned)
                {
                    Report(NativeAllocationDiagnosticDescriptors.AcrossAsync, syntax, handle.DisplayName);
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

            foreach (IMethodSymbol method in field.ContainingType.GetMembers("Dispose").OfType<IMethodSymbol>())
            {
                foreach (SyntaxReference declaration in method.DeclaringSyntaxReferences)
                {
                    if (declaration.GetSyntax().ToString().Contains(field.Name, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private LifecycleEffect GetLifecycleEffect(IMethodSymbol method, IParameterSymbol? parameter)
        {
            if (parameter is null)
            {
                return LifecycleEffect.None;
            }

            string cacheKey = method.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "|" + parameter.Name;
            if (_lifecycleSummaries.TryGetValue(cacheKey, out LifecycleEffect cached))
            {
                return cached;
            }

            LifecycleEffect effect = LifecycleEffect.None;
            int matches = 0;
            foreach (SyntaxReference declaration in method.DeclaringSyntaxReferences)
            {
                SyntaxNode syntax = declaration.GetSyntax(_context.CancellationToken);
                SemanticModel model = _context.Compilation.GetSemanticModel(syntax.SyntaxTree);
                foreach (InvocationExpressionSyntax invocation in syntax.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
                {
                    if (invocation.Expression is not MemberAccessExpressionSyntax member)
                    {
                        continue;
                    }

                    LifecycleEffect candidate = ToLifecycleEffect(member.Name.Identifier.ValueText);
                    if (candidate is LifecycleEffect.None)
                    {
                        continue;
                    }

                    ISymbol? receiver = model.GetSymbolInfo(member.Expression, _context.CancellationToken).Symbol;
                    if (!SymbolEqualityComparer.Default.Equals(receiver, parameter))
                    {
                        continue;
                    }

                    effect = candidate;
                    matches++;
                }
            }

            LifecycleEffect result = matches == 1 ? effect : LifecycleEffect.None;
            _lifecycleSummaries[cacheKey] = result;
            return result;
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

        private static bool IsUsingSyntax(SyntaxNode syntax)
        {
            if (syntax.AncestorsAndSelf().OfType<UsingStatementSyntax>().Any())
            {
                return true;
            }

            return syntax.AncestorsAndSelf()
                .OfType<LocalDeclarationStatementSyntax>()
                .Any(statement => statement.UsingKeyword.IsKind(SyntaxKind.UsingKeyword));
        }

        private static TextSpan? GetUsingScope(SyntaxNode syntax)
        {
            UsingStatementSyntax? usingStatement = syntax.AncestorsAndSelf().OfType<UsingStatementSyntax>().FirstOrDefault();
            if (usingStatement is not null)
            {
                return usingStatement.Statement?.Span ?? usingStatement.Span;
            }

            LocalDeclarationStatementSyntax? usingDeclaration = syntax.AncestorsAndSelf()
                .OfType<LocalDeclarationStatementSyntax>()
                .FirstOrDefault(statement => statement.UsingKeyword.IsKind(SyntaxKind.UsingKeyword));
            if (usingDeclaration is not null)
            {
                return usingDeclaration.Parent?.Span ?? usingDeclaration.Span;
            }

            return null;
        }

        private void Report(DiagnosticDescriptor descriptor, SyntaxNode syntax, params object[] arguments)
        {
            string key = descriptor.Id + ":" + syntax.SpanStart + ":" + string.Join("|", arguments);
            if (!_reported.Add(key))
            {
                return;
            }

            _context.ReportDiagnostic(Diagnostic.Create(descriptor, syntax.GetLocation(), messageArgs: arguments));
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
            internal int Generation { get; set; }
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
                copy.Generation = Generation;
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
            internal int Generation { get; }
            internal bool IsUsing { get; }
            internal bool Returned { get; set; }
            internal SyntaxNode Syntax { get; }
            internal string DisplayName => Symbol?.Name ?? Owner.Type.Name;

            internal HandleState Clone(OwnerState owner)
            {
                HandleState copy = new(Symbol, owner, Generation, IsUsing, Syntax)
                {
                    Returned = Returned
                };
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
