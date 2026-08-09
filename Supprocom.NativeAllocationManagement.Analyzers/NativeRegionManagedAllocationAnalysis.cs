using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Supprocom.NativeAllocationManagement.Analyzers;

internal sealed class NativeRegionManagedAllocationAnalysis
{
    private const string AllowMarker = "// NAMALLOW";
    private const string LeaseInitializerMetadataName =
        "Supprocom.NativeAllocationManagement.NativeLeaseInitializer`1";

    private readonly Compilation _compilation;
    private readonly NativeAllocationAnalyzer.NativeSymbols _symbols;
    private readonly INamedTypeSymbol? _leaseInitializer;
    private readonly HashSet<ISymbol> _canonicalAllocations = new(
        SymbolEqualityComparer.Default);
    private readonly object _scopeGate = new();
    private readonly Dictionary<SyntaxTree, ImmutableArray<RegionScope>> _scopeCache = [];
    private readonly object _summaryGate = new();
    private readonly Dictionary<ISymbol, ImmutableArray<AllocationFinding>> _summaryCache = new(
        SymbolEqualityComparer.Default);

    internal NativeRegionManagedAllocationAnalysis(
        Compilation compilation,
        NativeAllocationAnalyzer.NativeSymbols symbols)
    {
        _compilation = compilation;
        _symbols = symbols;
        INamedTypeSymbol? leaseInitializer =
            compilation.GetTypeByMetadataName(
                LeaseInitializerMetadataName);
        IAssemblySymbol? runtimeAssembly =
            symbols.Region?.ContainingAssembly
            ?? symbols.Arena?.ContainingAssembly
            ?? symbols.Pool?.ContainingAssembly;
        _leaseInitializer = SymbolEqualityComparer.Default.Equals(
            leaseInitializer?.ContainingAssembly,
            runtimeAssembly)
                ? leaseInitializer
                : null;
        AddCanonicalAllocations(symbols.Region, symbols.Local);
        AddCanonicalAllocations(symbols.Arena, symbols.ArenaLease);
        AddCanonicalAllocations(
            symbols.ConcurrentArena,
            symbols.ConcurrentArenaLease);
        AddCanonicalAllocations(symbols.Pool, symbols.Pooled);
    }

    internal void Register(CompilationStartAnalysisContext context)
    {
        context.RegisterOperationAction(
            AnalyzeOperation,
            OperationKind.ObjectCreation,
            OperationKind.ArrayCreation,
            OperationKind.AnonymousObjectCreation,
            OperationKind.DelegateCreation,
            OperationKind.Conversion,
            OperationKind.With,
            OperationKind.Binary,
            OperationKind.InterpolatedString,
            OperationKind.CollectionExpression,
            OperationKind.Invocation);
    }

    private void AddCanonicalAllocations(
        INamedTypeSymbol? ownerType,
        INamedTypeSymbol? resultType)
    {
        if (ownerType is null || resultType is null)
        {
            return;
        }

        foreach (IMethodSymbol method in ownerType.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(method => NativeAllocationAnalyzer.NativeSymbols.Is(
                method.ReturnType,
                resultType)))
        {
            _canonicalAllocations.Add(method.OriginalDefinition);
        }
    }

    private void AnalyzeOperation(OperationAnalysisContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        IOperation operation = context.Operation;
        if (IsDeferredOperation(operation.Syntax, declarationSyntax: null))
        {
            return;
        }

        if (!TryGetActiveRegion(
            operation.Syntax,
            context.CancellationToken,
            out RegionScope region))
        {
            return;
        }

        if (operation is IInvocationOperation invocation)
        {
            AnalyzeInvocation(context, invocation, region);
            return;
        }

        foreach (AllocationFinding finding in GetAllocationFindings(
            operation,
            context.CancellationToken))
        {
            Report(
                context,
                operation.Syntax,
                finding.Kind,
                region,
                finding.Source);
        }
    }

    private void AnalyzeInvocation(
        OperationAnalysisContext context,
        IInvocationOperation invocation,
        RegionScope region)
    {
        if (IsCanonicalAllocation(invocation.TargetMethod))
        {
            AnalyzeCanonicalInitializers(
                context,
                invocation,
                region);
            return;
        }

        ImmutableArray<IMethodSymbol> sourceMethods = ResolveSourceMethods(
            invocation,
            context.CancellationToken);
        HashSet<string> stateMachines = new(StringComparer.Ordinal);
        HashSet<AllocationFinding> findings = new(
            AllocationFindingComparer.Instance);
        foreach (IMethodSymbol sourceMethod in sourceMethods)
        {
            if (!SymbolEqualityComparer.Default.Equals(
                    sourceMethod.ContainingAssembly,
                    _compilation.Assembly)
                || sourceMethod.DeclaringSyntaxReferences.Length == 0)
            {
                continue;
            }

            if (sourceMethod.IsAsync
                && stateMachines.Add("async state-machine creation"))
            {
                Report(
                    context,
                    invocation.Syntax,
                    "async state-machine creation",
                    region,
                    invocation.Syntax);
            }

            if (IsIterator(sourceMethod, context.CancellationToken)
                && stateMachines.Add("iterator state-machine creation"))
            {
                Report(
                    context,
                    invocation.Syntax,
                    "iterator state-machine creation",
                    region,
                    invocation.Syntax);
            }

            ImmutableArray<AllocationFinding> summary = GetDirectSummary(
                sourceMethod,
                context.CancellationToken);
            foreach (AllocationFinding finding in summary)
            {
                if (!findings.Add(finding))
                {
                    continue;
                }

                Report(
                    context,
                    invocation.Syntax,
                    finding.Kind + " in source method '" + sourceMethod.Name + "'",
                    region,
                    finding.Source);
            }
        }
    }

    private void AnalyzeCanonicalInitializers(
        OperationAnalysisContext context,
        IInvocationOperation invocation,
        RegionScope region)
    {
        if (_leaseInitializer is null)
        {
            return;
        }

        foreach (IArgumentOperation argument in invocation.Arguments)
        {
            if (!NativeAllocationAnalyzer.NativeSymbols.Is(
                    argument.Parameter?.Type,
                    _leaseInitializer))
            {
                continue;
            }

            ImmutableArray<IMethodSymbol> sourceMethods = ResolveDelegateSources(
                argument.Value,
                context.CancellationToken);
            foreach (IMethodSymbol sourceMethod in sourceMethods)
            {
                if (!SymbolEqualityComparer.Default.Equals(
                        sourceMethod.ContainingAssembly,
                        _compilation.Assembly)
                    || sourceMethod.DeclaringSyntaxReferences.Length == 0)
                {
                    continue;
                }

                ImmutableArray<AllocationFinding> summary = GetDirectSummary(
                    sourceMethod,
                    context.CancellationToken);
                foreach (AllocationFinding finding in summary)
                {
                    Report(
                        context,
                        finding.Source,
                        finding.Kind + " in synchronous native initializer",
                        region,
                        finding.Source);
                }
            }
        }
    }

    private bool IsCanonicalAllocation(IMethodSymbol method)
    {
        return _canonicalAllocations.Contains(method.OriginalDefinition);
    }

    private ImmutableArray<IMethodSymbol> ResolveSourceMethods(
        IInvocationOperation invocation,
        CancellationToken cancellationToken,
        HashSet<ISymbol>? activeResolutions = null)
    {
        if (invocation.TargetMethod.MethodKind != MethodKind.DelegateInvoke)
        {
            return [invocation.TargetMethod];
        }

        return ResolveDelegateSources(
            invocation.Instance,
            cancellationToken,
            activeResolutions);
    }

    private ImmutableArray<IMethodSymbol> ResolveDelegateSources(
        IOperation? operation,
        CancellationToken cancellationToken,
        HashSet<ISymbol>? activeResolutions = null)
    {
        IOperation? value = Unwrap(operation);
        if (value is IDelegateCreationOperation delegateCreation)
        {
            return ToMethodArray(GetDelegateTargetMethod(
                delegateCreation.Target));
        }

        IMethodSymbol? directMethod = value is null
            ? null
            : GetDelegateTargetMethod(value);
        if (directMethod is not null)
        {
            return [directMethod];
        }

        if (value is ILocalReferenceOperation localReference)
        {
            activeResolutions ??= new HashSet<ISymbol>(
                SymbolEqualityComparer.Default);
            if (!activeResolutions.Add(localReference.Local))
            {
                return [];
            }

            try
            {
                return ResolveReachingDelegateSources(
                    localReference.Local,
                    localReference.Syntax,
                    cancellationToken,
                    activeResolutions);
            }
            finally
            {
                activeResolutions.Remove(localReference.Local);
            }
        }

        return [];
    }

    private ImmutableArray<IMethodSymbol> ResolveReachingDelegateSources(
        ILocalSymbol local,
        SyntaxNode useSyntax,
        CancellationToken cancellationToken,
        HashSet<ISymbol> activeResolutions)
    {
        SemanticModel model = _compilation.GetSemanticModel(
            useSyntax.SyntaxTree);
        ControlFlowGraph? graph = TryCreateControlFlowGraph(
            model,
            useSyntax,
            cancellationToken);
        if (graph is null
            || !TryGetContainingBlock(
                graph,
                useSyntax,
                out BasicBlock useBlock))
        {
            return [];
        }

        DelegateFlowState?[] inputs =
            new DelegateFlowState?[graph.Blocks.Length];
        DelegateFlowState?[] outputs =
            new DelegateFlowState?[graph.Blocks.Length];
        int maximumPasses = graph.Blocks.Length > 64
            ? 4096
            : Math.Max(8, graph.Blocks.Length * graph.Blocks.Length);
        bool changed = true;
        for (int pass = 0; pass < maximumPasses && changed; pass++)
        {
            changed = false;
            foreach (BasicBlock block in graph.Blocks)
            {
                DelegateFlowState? input = block.Ordinal == 0
                    ? DelegateFlowState.Unknown()
                    : MergePredecessors(block, outputs);
                if (!DelegateFlowState.AreEqual(
                    inputs[block.Ordinal],
                    input))
                {
                    inputs[block.Ordinal] = input?.Clone();
                    changed = true;
                }

                DelegateFlowState? output = input?.Clone();
                if (output is not null)
                {
                    ApplyBlockMutations(
                        block,
                        output,
                        local,
                        model,
                        int.MaxValue,
                        cancellationToken,
                        activeResolutions);
                }

                if (!DelegateFlowState.AreEqual(
                    outputs[block.Ordinal],
                    output))
                {
                    outputs[block.Ordinal] = output;
                    changed = true;
                }
            }
        }

        if (changed || inputs[useBlock.Ordinal] is not DelegateFlowState state)
        {
            return [];
        }

        DelegateFlowState reaching = state.Clone();
        ApplyBlockMutations(
            useBlock,
            reaching,
            local,
            model,
            useSyntax.SpanStart,
            cancellationToken,
            activeResolutions);
        if (reaching.HasUnknownSource)
        {
            return [];
        }

        return reaching.Sources
            .OrderBy(GetDeclarationStart)
            .ThenBy(method => method.Name, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static ControlFlowGraph? TryCreateControlFlowGraph(
        SemanticModel model,
        SyntaxNode useSyntax,
        CancellationToken cancellationToken)
    {
        foreach (SyntaxNode candidate in useSyntax.AncestorsAndSelf())
        {
            cancellationToken.ThrowIfCancellationRequested();
            IOperation? operation = model.GetOperation(
                candidate,
                cancellationToken);
            if (operation is not IMethodBodyOperation
                && operation is not IConstructorBodyOperation
                && operation is not IBlockOperation { Parent: null })
            {
                continue;
            }

            try
            {
                return ControlFlowGraph.Create(
                    candidate,
                    model,
                    cancellationToken);
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        return null;
    }

    private static bool TryGetContainingBlock(
        ControlFlowGraph graph,
        SyntaxNode useSyntax,
        out BasicBlock result)
    {
        foreach (BasicBlock block in graph.Blocks)
        {
            if (block.Operations.Any(operation => ContainsUse(
                    operation,
                    useSyntax))
                || block.BranchValue is not null
                    && ContainsUse(block.BranchValue, useSyntax))
            {
                result = block;
                return true;
            }
        }

        result = null!;
        return false;
    }

    private static bool ContainsUse(
        IOperation operation,
        SyntaxNode useSyntax)
    {
        return ReferenceEquals(
                operation.Syntax.SyntaxTree,
                useSyntax.SyntaxTree)
            && operation.Syntax.Span.Contains(useSyntax.Span);
    }

    private static DelegateFlowState? MergePredecessors(
        BasicBlock block,
        DelegateFlowState?[] outputs)
    {
        DelegateFlowState? merged = null;
        foreach (ControlFlowBranch branch in block.Predecessors)
        {
            DelegateFlowState? predecessor = outputs[branch.Source.Ordinal];
            if (predecessor is null)
            {
                continue;
            }

            if (merged is null)
            {
                merged = predecessor.Clone();
            }
            else
            {
                merged.Merge(predecessor);
            }
        }

        return merged;
    }

    private void ApplyBlockMutations(
        BasicBlock block,
        DelegateFlowState state,
        ILocalSymbol local,
        SemanticModel model,
        int beforePosition,
        CancellationToken cancellationToken,
        HashSet<ISymbol> activeResolutions)
    {
        foreach (IOperation operation in block.Operations)
        {
            ApplyOperationMutations(
                operation,
                state,
                local,
                model,
                beforePosition,
                cancellationToken,
                activeResolutions);
        }

        if (block.BranchValue is not null)
        {
            ApplyOperationMutations(
                block.BranchValue,
                state,
                local,
                model,
                beforePosition,
                cancellationToken,
                activeResolutions);
        }
    }

    private void ApplyOperationMutations(
        IOperation root,
        DelegateFlowState state,
        ILocalSymbol local,
        SemanticModel model,
        int beforePosition,
        CancellationToken cancellationToken,
        HashSet<ISymbol> activeResolutions)
    {
        IEnumerable<IOperation> mutations = root.DescendantsAndSelf()
            .Where(operation => operation.Syntax.Span.End <= beforePosition)
            .Where(operation => !IsDeferredOperation(
                operation.Syntax,
                root.Syntax))
            .Where(operation => IsDelegateMutation(
                operation,
                local))
            .OrderBy(operation => operation.Syntax.Span.End)
            .ThenBy(operation => operation.Syntax.SpanStart);
        foreach (IOperation mutation in mutations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (mutation)
            {
                case IInvocationOperation invocation:
                    ApplyInvocationMutation(
                        state,
                        invocation,
                        local,
                        cancellationToken,
                        activeResolutions);
                    break;

                case ISimpleAssignmentOperation assignment
                    when IsExactLocalReference(
                        assignment.Target,
                        local):
                    ReplaceWithAssignedSource(
                        state,
                        GetOriginalAssignedValue(
                            assignment,
                            local,
                            model,
                            cancellationToken));
                    break;

                case IVariableDeclaratorOperation declarator
                    when SymbolEqualityComparer.Default.Equals(
                        declarator.Symbol,
                        local):
                    ReplaceWithAssignedSource(
                        state,
                        declarator.Initializer?.Value);
                    break;

                default:
                    state.ReplaceWithUnknown();
                    break;
            }
        }
    }

    private static bool IsDelegateMutation(
        IOperation operation,
        ILocalSymbol local)
    {
        return operation switch
        {
            ISimpleAssignmentOperation assignment =>
                ReferencesLocal(assignment.Target, local)
                || assignment.IsRef
                    && ReferencesLocal(assignment.Value, local),
            ICompoundAssignmentOperation assignment =>
                ReferencesLocal(assignment.Target, local),
            IDeconstructionAssignmentOperation assignment =>
                ReferencesLocal(assignment.Target, local),
            IVariableDeclaratorOperation declarator =>
                SymbolEqualityComparer.Default.Equals(
                    declarator.Symbol,
                    local)
                && declarator.Initializer is not null,
            IArgumentOperation argument =>
                IsWritableReference(argument.Parameter)
                && ReferencesLocal(argument.Value, local),
            IInvocationOperation => true,
            _ => false
        };
    }

    private void ApplyInvocationMutation(
        DelegateFlowState state,
        IInvocationOperation invocation,
        ILocalSymbol local,
        CancellationToken cancellationToken,
        HashSet<ISymbol> activeResolutions)
    {
        ImmutableArray<IMethodSymbol> sourceMethods =
            GetInvocationMutationSources(
                invocation,
                cancellationToken,
                activeResolutions);
        if (sourceMethods.IsDefaultOrEmpty)
        {
            return;
        }

        DelegateFlowState input = state.Clone();
        DelegateFlowState? merged = null;
        foreach (IMethodSymbol sourceMethod in sourceMethods)
        {
            DelegateFlowState alternative = input.Clone();
            List<DelegateMutationEffect> effects = [];
            AddMutationEffect(
                effects,
                GetDelegateMutationEffect(
                    sourceMethod,
                    local,
                    cancellationToken));
            foreach (IArgumentOperation argument in invocation.Arguments)
            {
                if (argument.Parameter is not IParameterSymbol parameter
                    || !IsWritableReference(parameter)
                    || !ReferencesLocal(argument.Value, local)
                    || parameter.Ordinal < 0
                    || parameter.Ordinal >= sourceMethod.Parameters.Length)
                {
                    continue;
                }

                AddMutationEffect(
                    effects,
                    GetDelegateMutationEffect(
                        sourceMethod,
                        sourceMethod.Parameters[parameter.Ordinal],
                        cancellationToken));
            }

            if (effects.Count > 1)
            {
                alternative.ReplaceWithUnknown();
            }
            else if (effects.Count == 1)
            {
                alternative.Apply(effects[0]);
            }

            if (merged is null)
            {
                merged = alternative;
            }
            else
            {
                merged.Merge(alternative);
            }
        }

        if (merged is not null)
        {
            state.CopyFrom(merged);
        }
    }

    private ImmutableArray<IMethodSymbol> GetInvocationMutationSources(
        IInvocationOperation invocation,
        CancellationToken cancellationToken,
        HashSet<ISymbol> activeResolutions)
    {
        if (!IsCanonicalAllocation(invocation.TargetMethod))
        {
            return ResolveSourceMethods(
                invocation,
                cancellationToken,
                activeResolutions);
        }

        ImmutableArray<IMethodSymbol>.Builder sources =
            ImmutableArray.CreateBuilder<IMethodSymbol>();
        foreach (IArgumentOperation argument in invocation.Arguments)
        {
            if (!NativeAllocationAnalyzer.NativeSymbols.Is(
                    argument.Parameter?.Type,
                    _leaseInitializer))
            {
                continue;
            }

            sources.AddRange(ResolveDelegateSources(
                argument.Value,
                cancellationToken,
                activeResolutions));
        }

        return sources
            .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default)
            .ToImmutableArray();
    }

    private static void AddMutationEffect(
        ICollection<DelegateMutationEffect> effects,
        DelegateMutationEffect effect)
    {
        if (effect.HasMutation)
        {
            effects.Add(effect);
        }
    }

    private DelegateMutationEffect GetDelegateMutationEffect(
        IMethodSymbol sourceMethod,
        ISymbol target,
        CancellationToken cancellationToken)
    {
        if (!SymbolEqualityComparer.Default.Equals(
                sourceMethod.ContainingAssembly,
                _compilation.Assembly)
            || !TryGetMethodBody(
                sourceMethod,
                cancellationToken,
                out IOperation? body,
                out SyntaxNode? declaration)
            || body is null
            || declaration is null)
        {
            return DelegateMutationEffect.None;
        }

        List<IOperation> mutations = body.DescendantsAndSelf()
            .Where(operation => !IsDeferredOperation(
                operation.Syntax,
                declaration))
            .Where(operation => IsSymbolMutation(
                operation,
                target))
            .OrderBy(operation => operation.Syntax.Span.End)
            .ThenBy(operation => operation.Syntax.SpanStart)
            .ToList();
        if (mutations.Count == 0)
        {
            return DelegateMutationEffect.None;
        }

        if (mutations.Count != 1
            || mutations[0] is not ISimpleAssignmentOperation assignment
            || !IsExactSymbolReference(assignment.Target, target))
        {
            return DelegateMutationEffect.Unknown;
        }

        SemanticModel model = _compilation.GetSemanticModel(
            assignment.Syntax.SyntaxTree);
        IOperation? value = assignment.Syntax is AssignmentExpressionSyntax syntax
            ? model.GetOperation(syntax.Right, cancellationToken)
            : assignment.Value;
        ImmutableArray<IMethodSymbol> sources =
            GetDirectDelegateSources(value);
        if (sources.IsDefaultOrEmpty)
        {
            return DelegateMutationEffect.Unknown;
        }

        return DelegateMutationEffect.Known(
            sources,
            preservesInput: !IsDefinitelyExecuted(
                assignment.Syntax,
                declaration,
                model,
                target));
    }

    private static bool IsSymbolMutation(
        IOperation operation,
        ISymbol target)
    {
        return operation switch
        {
            ISimpleAssignmentOperation assignment =>
                ReferencesSymbol(assignment.Target, target)
                || assignment.IsRef
                    && ReferencesSymbol(assignment.Value, target),
            ICompoundAssignmentOperation assignment =>
                ReferencesSymbol(assignment.Target, target),
            IDeconstructionAssignmentOperation assignment =>
                ReferencesSymbol(assignment.Target, target),
            IArgumentOperation argument =>
                IsWritableReference(argument.Parameter)
                && ReferencesSymbol(argument.Value, target),
            _ => false
        };
    }

    private static bool IsWritableReference(IParameterSymbol? parameter)
    {
        return parameter?.RefKind is RefKind.Ref or RefKind.Out;
    }

    private static bool IsExactSymbolReference(
        IOperation operation,
        ISymbol target)
    {
        IOperation? value = Unwrap(operation);
        return value switch
        {
            ILocalReferenceOperation local =>
                SymbolEqualityComparer.Default.Equals(
                    local.Local,
                    target),
            IParameterReferenceOperation parameter =>
                SymbolEqualityComparer.Default.Equals(
                    parameter.Parameter,
                    target),
            _ => false
        };
    }

    private static bool ReferencesSymbol(
        IOperation operation,
        ISymbol target)
    {
        return operation.DescendantsAndSelf().Any(candidate =>
            candidate is ILocalReferenceOperation local
                && SymbolEqualityComparer.Default.Equals(
                    local.Local,
                    target)
            || candidate is IParameterReferenceOperation parameter
                && SymbolEqualityComparer.Default.Equals(
                    parameter.Parameter,
                    target));
    }

    private static bool IsDefinitelyExecuted(
        SyntaxNode mutation,
        SyntaxNode declaration,
        SemanticModel model,
        ISymbol target)
    {
        SyntaxNode? executable = declaration switch
        {
            AnonymousFunctionExpressionSyntax anonymous => anonymous.Body,
            LocalFunctionStatementSyntax { Body: not null } local => local.Body,
            LocalFunctionStatementSyntax { ExpressionBody: not null } local =>
                local.ExpressionBody.Expression,
            BaseMethodDeclarationSyntax { Body: not null } method => method.Body,
            BaseMethodDeclarationSyntax { ExpressionBody: not null } method =>
                method.ExpressionBody.Expression,
            _ => null
        };
        if (executable is not null)
        {
            try
            {
                DataFlowAnalysis analysis = model.AnalyzeDataFlow(executable);
                if (analysis.Succeeded
                    && analysis.AlwaysAssigned.Any(symbol =>
                        SymbolEqualityComparer.Default.Equals(
                            symbol,
                            target)))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        if (executable is ExpressionSyntax expression)
        {
            return expression.Span.Equals(mutation.Span);
        }

        if (executable is not BlockSyntax block)
        {
            return false;
        }

        StatementSyntax? statement = mutation.AncestorsAndSelf()
            .OfType<StatementSyntax>()
            .FirstOrDefault(candidate => ReferenceEquals(
                candidate.Parent,
                block));
        return statement is not null
            && block.Statements.Count > 0
            && ReferenceEquals(block.Statements[0], statement)
            && statement is ExpressionStatementSyntax expressionStatement
            && expressionStatement.Expression.Span.Equals(mutation.Span);
    }

    private static bool IsExactLocalReference(
        IOperation operation,
        ILocalSymbol local)
    {
        return Unwrap(operation) is ILocalReferenceOperation reference
            && SymbolEqualityComparer.Default.Equals(
                reference.Local,
                local);
    }

    private static bool ReferencesLocal(
        IOperation operation,
        ILocalSymbol local)
    {
        return operation.DescendantsAndSelf()
            .OfType<ILocalReferenceOperation>()
            .Any(reference => SymbolEqualityComparer.Default.Equals(
                reference.Local,
                local));
    }

    private static IOperation? GetOriginalAssignedValue(
        ISimpleAssignmentOperation assignment,
        ILocalSymbol local,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        if (assignment.Syntax is AssignmentExpressionSyntax syntax)
        {
            return model.GetOperation(
                syntax.Right,
                cancellationToken);
        }

        VariableDeclaratorSyntax? declarator = assignment.Syntax
            .AncestorsAndSelf()
            .OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(candidate => SymbolEqualityComparer.Default.Equals(
                model.GetDeclaredSymbol(candidate, cancellationToken),
                local));
        return declarator?.Initializer is { Value: ExpressionSyntax value }
            ? model.GetOperation(value, cancellationToken)
            : assignment.Value;
    }

    private static void ReplaceWithAssignedSource(
        DelegateFlowState state,
        IOperation? value)
    {
        ImmutableArray<IMethodSymbol> sources =
            GetDirectDelegateSources(value);
        if (sources.IsDefaultOrEmpty)
        {
            state.ReplaceWithUnknown();
            return;
        }

        state.Replace(sources);
    }

    private static ImmutableArray<IMethodSymbol> GetDirectDelegateSources(
        IOperation? operation)
    {
        IOperation? value = Unwrap(operation);
        if (value is IDelegateCreationOperation delegateCreation)
        {
            return ToMethodArray(GetDelegateTargetMethod(
                delegateCreation.Target));
        }

        return value is null
            ? []
            : ToMethodArray(GetDelegateTargetMethod(value));
    }

    private static ImmutableArray<IMethodSymbol> ToMethodArray(
        IMethodSymbol? method)
    {
        return method is null
            ? []
            : [method];
    }

    private static int GetDeclarationStart(IMethodSymbol method)
    {
        return method.DeclaringSyntaxReferences
            .FirstOrDefault()
            ?.Span.Start
            ?? int.MaxValue;
    }

    private static IMethodSymbol? GetDelegateTargetMethod(IOperation target)
    {
        IOperation? value = Unwrap(target);
        return value switch
        {
            IAnonymousFunctionOperation anonymous => anonymous.Symbol,
            IMethodReferenceOperation methodReference => methodReference.Method,
            _ => null
        };
    }

    private ImmutableArray<AllocationFinding> GetDirectSummary(
        IMethodSymbol method,
        CancellationToken cancellationToken)
    {
        lock (_summaryGate)
        {
            if (_summaryCache.TryGetValue(method, out ImmutableArray<AllocationFinding> cached))
            {
                return cached;
            }
        }

        ImmutableArray<AllocationFinding>.Builder findings = ImmutableArray.CreateBuilder<AllocationFinding>();
        foreach (SyntaxReference syntaxReference in method.DeclaringSyntaxReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SyntaxNode declaration = syntaxReference.GetSyntax(cancellationToken);
            SemanticModel model = _compilation.GetSemanticModel(declaration.SyntaxTree);
            IOperation? body = GetBodyOperation(
                model,
                declaration,
                cancellationToken);
            if (body is null)
            {
                continue;
            }

            foreach (IOperation operation in body.DescendantsAndSelf())
            {
                if (!IsAllocationOperation(operation)
                    || IsDeferredOperation(operation.Syntax, declaration))
                {
                    continue;
                }

                findings.AddRange(GetAllocationFindings(
                    operation,
                    cancellationToken));
            }
        }

        ImmutableArray<AllocationFinding> result = findings
            .Distinct(AllocationFindingComparer.Instance)
            .ToImmutableArray();
        lock (_summaryGate)
        {
            _summaryCache[method] = result;
        }

        return result;
    }

    private static IOperation? GetBodyOperation(
        SemanticModel model,
        SyntaxNode declaration,
        CancellationToken cancellationToken)
    {
        IOperation? operation = model.GetOperation(
            declaration,
            cancellationToken);
        return operation switch
        {
            IAnonymousFunctionOperation anonymous => anonymous.Body,
            ILocalFunctionOperation local => local.Body,
            IMethodBodyOperation methodBody => methodBody,
            _ => declaration switch
            {
                BaseMethodDeclarationSyntax { Body: not null } method =>
                    model.GetOperation(method.Body, cancellationToken),
                BaseMethodDeclarationSyntax { ExpressionBody: not null } method =>
                    model.GetOperation(method.ExpressionBody.Expression, cancellationToken),
                LocalFunctionStatementSyntax { Body: not null } local =>
                    model.GetOperation(local.Body, cancellationToken),
                LocalFunctionStatementSyntax { ExpressionBody: not null } local =>
                    model.GetOperation(local.ExpressionBody.Expression, cancellationToken),
                AnonymousFunctionExpressionSyntax anonymous =>
                    model.GetOperation(anonymous, cancellationToken) is IAnonymousFunctionOperation nested
                        ? nested.Body
                        : null,
                _ => null
            }
        };
    }

    private static bool IsAllocationOperation(IOperation operation)
    {
        return operation is IObjectCreationOperation
            or IArrayCreationOperation
            or IAnonymousObjectCreationOperation
            or IDelegateCreationOperation
            or IConversionOperation
            or IWithOperation
            or IBinaryOperation
            or IInterpolatedStringOperation
            or ICollectionExpressionOperation;
    }

    private ImmutableArray<AllocationFinding> GetAllocationFindings(
        IOperation operation,
        CancellationToken cancellationToken)
    {
        switch (operation)
        {
            case IObjectCreationOperation objectCreation
                when objectCreation.Type?.IsReferenceType == true:
                return [new AllocationFinding(
                    "class object creation",
                    objectCreation.Syntax)];

            case IArrayCreationOperation arrayCreation
                when !HasCollectionExpressionParent(arrayCreation):
                return [new AllocationFinding(
                    IsParamsArray(arrayCreation)
                        ? "implicit params-array creation"
                        : "array creation",
                    arrayCreation.Syntax)];

            case IAnonymousObjectCreationOperation anonymousObject:
                return [new AllocationFinding(
                    "anonymous object creation",
                    anonymousObject.Syntax)];

            case IDelegateCreationOperation delegateCreation:
                return GetDelegateFindings(
                    delegateCreation,
                    cancellationToken);

            case IConversionOperation conversion
                when IsBoxingConversion(conversion):
                return [new AllocationFinding(
                    "boxing conversion",
                    conversion.Syntax)];

            case IWithOperation withOperation
                when withOperation.Type?.IsReferenceType == true:
                return [new AllocationFinding(
                    "record-class with-expression cloning",
                    withOperation.Syntax)];

            case IBinaryOperation binary
                when IsAllocatingStringConcatenation(binary):
                return [new AllocationFinding(
                    "string concatenation",
                    binary.Syntax)];

            case IInterpolatedStringOperation interpolated
                when interpolated.Type?.SpecialType == SpecialType.System_String
                    && !interpolated.ConstantValue.HasValue:
                return [new AllocationFinding(
                    "interpolated string creation",
                    interpolated.Syntax)];

            case ICollectionExpressionOperation collection
                when IsManagedCollection(collection.Type):
                return [new AllocationFinding(
                    "collection expression with managed storage",
                    collection.Syntax)];

            default:
                return [];
        }
    }

    private ImmutableArray<AllocationFinding> GetDelegateFindings(
        IDelegateCreationOperation operation,
        CancellationToken cancellationToken)
    {
        IOperation? target = Unwrap(operation.Target);
        if (target is IAnonymousFunctionOperation anonymous)
        {
            if (!CapturesState(anonymous.Body, anonymous.Symbol, anonymous.Syntax))
            {
                return [];
            }

            return
            [
                new AllocationFinding("delegate creation", operation.Syntax),
                new AllocationFinding("capturing lambda closure creation", operation.Syntax)
            ];
        }

        if (target is not IMethodReferenceOperation methodReference)
        {
            return [new AllocationFinding(
                "delegate creation",
                operation.Syntax)];
        }

        IMethodSymbol method = methodReference.Method;
        if (method.IsStatic && method.MethodKind != MethodKind.LocalFunction)
        {
            return [];
        }

        if (method.MethodKind == MethodKind.LocalFunction
            && TryGetMethodBody(
                method,
                cancellationToken,
                out IOperation? body,
                out SyntaxNode? declaration)
            && body is not null
            && declaration is not null
            && CapturesState(body, method, declaration))
        {
            return
            [
                new AllocationFinding("delegate creation", operation.Syntax),
                new AllocationFinding("capturing local-function closure creation", operation.Syntax)
            ];
        }

        return [new AllocationFinding(
            "delegate creation",
            operation.Syntax)];
    }

    private bool TryGetMethodBody(
        IMethodSymbol method,
        CancellationToken cancellationToken,
        out IOperation? body,
        out SyntaxNode? declaration)
    {
        SyntaxReference? reference = method.DeclaringSyntaxReferences.FirstOrDefault();
        declaration = reference?.GetSyntax(cancellationToken);
        if (declaration is null)
        {
            body = null;
            return false;
        }

        SemanticModel model = _compilation.GetSemanticModel(declaration.SyntaxTree);
        body = GetBodyOperation(model, declaration, cancellationToken);
        return body is not null;
    }

    private static bool CapturesState(
        IOperation body,
        IMethodSymbol function,
        SyntaxNode declaration)
    {
        foreach (IOperation operation in body.DescendantsAndSelf())
        {
            if (IsDeferredOperation(operation.Syntax, declaration))
            {
                continue;
            }

            if (operation is ILocalReferenceOperation localReference)
            {
                SyntaxNode? localDeclaration = localReference.Local
                    .DeclaringSyntaxReferences
                    .FirstOrDefault()
                    ?.GetSyntax();
                if (localDeclaration is null
                    || !declaration.Span.Contains(localDeclaration.Span))
                {
                    return true;
                }
            }

            if (operation is IParameterReferenceOperation parameterReference
                && !SymbolEqualityComparer.Default.Equals(
                    parameterReference.Parameter.ContainingSymbol,
                    function))
            {
                return true;
            }

            if (operation is IInstanceReferenceOperation instanceReference
                && instanceReference.ReferenceKind
                    == InstanceReferenceKind.ContainingTypeInstance)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasCollectionExpressionParent(IOperation operation)
    {
        for (IOperation? parent = operation.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is ICollectionExpressionOperation)
            {
                return true;
            }

            if (!parent.IsImplicit)
            {
                return false;
            }
        }

        return false;
    }

    private static bool IsParamsArray(IArrayCreationOperation operation)
    {
        if (!operation.IsImplicit)
        {
            return false;
        }

        for (IOperation? parent = operation.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is IArgumentOperation argument)
            {
                return argument.ArgumentKind == ArgumentKind.ParamArray;
            }

            if (!parent.IsImplicit)
            {
                return false;
            }
        }

        return false;
    }

    private static bool IsAllocatingStringConcatenation(IBinaryOperation operation)
    {
        if (operation.Type?.SpecialType != SpecialType.System_String
            || operation.OperatorKind != BinaryOperatorKind.Add
            || operation.ConstantValue.HasValue)
        {
            return false;
        }

        return operation.Parent is not IBinaryOperation parent
            || parent.Type?.SpecialType != SpecialType.System_String
            || parent.OperatorKind != BinaryOperatorKind.Add;
    }

    private static bool IsManagedCollection(ITypeSymbol? type)
    {
        return type is not null
            && (type.TypeKind == TypeKind.Array || type.IsReferenceType);
    }

    private bool IsBoxingConversion(IConversionOperation operation)
    {
        return _compilation is CSharpCompilation compilation
            && operation.Operand.Type is ITypeSymbol source
            && operation.Type is ITypeSymbol destination
            && compilation.ClassifyConversion(source, destination).IsBoxing;
    }

    private bool TryGetActiveRegion(
        SyntaxNode syntax,
        CancellationToken cancellationToken,
        out RegionScope activeRegion)
    {
        ImmutableArray<RegionScope> scopes = GetRegionScopes(
            syntax.SyntaxTree,
            cancellationToken);
        int position = syntax.SpanStart;
        foreach (RegionScope scope in scopes
            .Where(scope => scope.Start <= position && position < scope.End)
            .OrderByDescending(scope => scope.Start))
        {
            if (IsDisposedBefore(
                scope,
                syntax,
                cancellationToken))
            {
                continue;
            }

            activeRegion = scope;
            return true;
        }

        activeRegion = default;
        return false;
    }

    private ImmutableArray<RegionScope> GetRegionScopes(
        SyntaxTree tree,
        CancellationToken cancellationToken)
    {
        lock (_scopeGate)
        {
            if (_scopeCache.TryGetValue(tree, out ImmutableArray<RegionScope> cached))
            {
                return cached;
            }
        }

        SyntaxNode root = tree.GetRoot(cancellationToken);
        SemanticModel model = _compilation.GetSemanticModel(tree);
        ImmutableArray<RegionScope>.Builder scopes = ImmutableArray.CreateBuilder<RegionScope>();
        foreach (UsingStatementSyntax statement in root.DescendantNodes()
            .OfType<UsingStatementSyntax>())
        {
            if (statement.Declaration is null)
            {
                continue;
            }

            AddRegionVariables(
                scopes,
                model,
                statement.Declaration.Variables,
                statement.Statement.SpanStart,
                statement.Statement.Span.End,
                cancellationToken);
        }

        foreach (LocalDeclarationStatementSyntax declaration in root.DescendantNodes()
            .OfType<LocalDeclarationStatementSyntax>()
            .Where(statement => statement.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)))
        {
            int end = declaration.Parent switch
            {
                GlobalStatementSyntax global => global.Parent?.Span.End ?? global.Span.End,
                SyntaxNode parent => parent.Span.End,
                null => declaration.SyntaxTree.GetRoot(cancellationToken).Span.End
            };
            AddRegionVariables(
                scopes,
                model,
                declaration.Declaration.Variables,
                declaration.Span.End,
                end,
                cancellationToken);
        }

        ImmutableArray<RegionScope> result = scopes
            .OrderBy(scope => scope.Start)
            .ToImmutableArray();
        lock (_scopeGate)
        {
            _scopeCache[tree] = result;
        }

        return result;
    }

    private void AddRegionVariables(
        ImmutableArray<RegionScope>.Builder scopes,
        SemanticModel model,
        SeparatedSyntaxList<VariableDeclaratorSyntax> variables,
        int start,
        int end,
        CancellationToken cancellationToken)
    {
        foreach (VariableDeclaratorSyntax variable in variables)
        {
            if (model.GetDeclaredSymbol(variable, cancellationToken)
                is not ILocalSymbol local
                || !NativeAllocationAnalyzer.NativeSymbols.Is(
                    local.Type,
                    _symbols.Region))
            {
                continue;
            }

            scopes.Add(new RegionScope(
                local,
                local.Name,
                start,
                end));
        }
    }

    private bool IsDisposedBefore(
        RegionScope scope,
        SyntaxNode site,
        CancellationToken cancellationToken)
    {
        SemanticModel model = _compilation.GetSemanticModel(site.SyntaxTree);
        foreach (BlockSyntax block in site.AncestorsAndSelf().OfType<BlockSyntax>())
        {
            StatementSyntax? siteStatement = site.AncestorsAndSelf()
                .OfType<StatementSyntax>()
                .FirstOrDefault(statement => ReferenceEquals(statement.Parent, block));
            if (siteStatement is null)
            {
                continue;
            }

            foreach (ExpressionStatementSyntax statement in block.Statements
                .OfType<ExpressionStatementSyntax>()
                .Where(statement => statement.SpanStart >= scope.Start
                    && statement.SpanStart < siteStatement.SpanStart))
            {
                if (model.GetOperation(
                    statement.Expression,
                    cancellationToken) is IInvocationOperation invocation
                    && IsRegionDispose(invocation, scope.Symbol))
                {
                    return true;
                }
            }
        }

        foreach (SwitchSectionSyntax section in site.AncestorsAndSelf()
            .OfType<SwitchSectionSyntax>())
        {
            StatementSyntax? siteStatement = site.AncestorsAndSelf()
                .OfType<StatementSyntax>()
                .FirstOrDefault(statement => ReferenceEquals(
                    statement.Parent,
                    section));
            if (siteStatement is not null
                && HasEarlierDispose(
                    section.Statements,
                    siteStatement,
                    scope,
                    model,
                    cancellationToken))
            {
                return true;
            }
        }

        GlobalStatementSyntax? siteGlobal = site.AncestorsAndSelf()
            .OfType<GlobalStatementSyntax>()
            .FirstOrDefault();
        if (siteGlobal?.Parent is CompilationUnitSyntax unit)
        {
            IEnumerable<StatementSyntax> statements = unit.Members
                .OfType<GlobalStatementSyntax>()
                .Select(global => global.Statement);
            if (HasEarlierDispose(
                statements,
                siteGlobal.Statement,
                scope,
                model,
                cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private bool HasEarlierDispose(
        IEnumerable<StatementSyntax> statements,
        StatementSyntax siteStatement,
        RegionScope scope,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        foreach (ExpressionStatementSyntax statement in statements
            .OfType<ExpressionStatementSyntax>()
            .Where(statement => statement.SpanStart >= scope.Start
                && statement.SpanStart < siteStatement.SpanStart))
        {
            if (model.GetOperation(
                statement.Expression,
                cancellationToken) is IInvocationOperation invocation
                && IsRegionDispose(invocation, scope.Symbol))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsRegionDispose(
        IInvocationOperation invocation,
        ISymbol regionSymbol)
    {
        IMethodSymbol method = invocation.TargetMethod;
        if (!NativeAllocationAnalyzer.NativeSymbols.Is(
                method.ContainingType,
                _symbols.Region)
            || method.Name != "Dispose"
            || method.Parameters.Length != 0
            || Unwrap(invocation.Instance) is not ILocalReferenceOperation local)
        {
            return false;
        }

        return SymbolEqualityComparer.Default.Equals(
            local.Local,
            regionSymbol);
    }

    private static bool IsDeferredOperation(
        SyntaxNode syntax,
        SyntaxNode? declarationSyntax)
    {
        foreach (SyntaxNode ancestor in syntax.Ancestors())
        {
            if (declarationSyntax is not null
                && ReferenceEquals(
                    ancestor.SyntaxTree,
                    declarationSyntax.SyntaxTree)
                && ancestor.RawKind == declarationSyntax.RawKind
                && ancestor.Span.Equals(declarationSyntax.Span))
            {
                return false;
            }

            if (ancestor is AnonymousFunctionExpressionSyntax
                or LocalFunctionStatementSyntax)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsIterator(
        IMethodSymbol method,
        CancellationToken cancellationToken)
    {
        foreach (SyntaxReference reference in method.DeclaringSyntaxReferences)
        {
            SyntaxNode declaration = reference.GetSyntax(cancellationToken);
            foreach (YieldStatementSyntax yield in declaration.DescendantNodes()
                .OfType<YieldStatementSyntax>())
            {
                if (!IsDeferredOperation(yield, declaration))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IOperation? Unwrap(IOperation? operation)
    {
        while (operation is IConversionOperation conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
    }

    private static void Report(
        OperationAnalysisContext context,
        SyntaxNode diagnosticSyntax,
        string kind,
        RegionScope region,
        SyntaxNode allocationSource)
    {
        if (HasAllowMarker(diagnosticSyntax)
            || HasAllowMarker(allocationSource))
        {
            return;
        }

        Location location = diagnosticSyntax.GetLocation();
        FileLinePositionSpan line = location.GetLineSpan();
        string sourceFile = string.IsNullOrEmpty(line.Path)
            ? diagnosticSyntax.SyntaxTree.FilePath is { Length: > 0 } path
                ? path
                : "<in-memory>"
            : line.Path;
        string source = sourceFile
            + ":"
            + (line.StartLinePosition.Line + 1).ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            + ":"
            + (line.StartLinePosition.Character + 1).ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        FileLinePositionSpan allocationLine = allocationSource
            .GetLocation()
            .GetLineSpan();
        string allocationPath = string.IsNullOrEmpty(allocationLine.Path)
            ? allocationSource.SyntaxTree.FilePath is { Length: > 0 } sourcePath
                ? sourcePath
                : "<in-memory>"
            : allocationLine.Path;
        string allocationLocation = allocationPath
            + ":"
            + (allocationLine.StartLinePosition.Line + 1).ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            + ":"
            + (allocationLine.StartLinePosition.Character + 1).ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        ImmutableDictionary<string, string?> properties =
            ImmutableDictionary<string, string?>.Empty
                .Add("NAM.DiagnosticId", NativeAllocationDiagnosticDescriptors.ManagedAllocationInRegion.Id)
                .Add("NAM.Provenance", kind)
                .Add("NAM.ProvenancePath", allocationLocation)
                .Add("NAM.Source", source)
                .Add("NAM.SourceFile", sourceFile)
                .Add(
                    "NAM.SourceLine",
                    (line.StartLinePosition.Line + 1).ToString(
                        System.Globalization.CultureInfo.InvariantCulture))
                .Add(
                    "NAM.SourceColumn",
                    (line.StartLinePosition.Character + 1).ToString(
                        System.Globalization.CultureInfo.InvariantCulture))
                .Add(
                    "NAM.Operation",
                    NativeAllocationDiagnosticDescriptors.ManagedAllocationInRegion.Title.ToString())
                .Add("NAM.OperationId", NativeAllocationDiagnosticDescriptors.ManagedAllocationInRegion.Id)
                .Add("NAM.AllocationSource", allocationLocation);
        context.ReportDiagnostic(Diagnostic.Create(
            NativeAllocationDiagnosticDescriptors.ManagedAllocationInRegion,
            location,
            properties,
            kind,
            source,
            region.Name,
            allocationLocation));
    }

    private static bool HasAllowMarker(SyntaxNode syntax)
    {
        StatementSyntax? statement = syntax.AncestorsAndSelf()
            .OfType<StatementSyntax>()
            .FirstOrDefault();
        if (statement is null)
        {
            return false;
        }

        if (statement.GetTrailingTrivia().Any(IsAllowMarker))
        {
            return true;
        }

        SourceText text = statement.SyntaxTree.GetText();
        int statementLine = text.Lines.GetLineFromPosition(
            statement.SpanStart).LineNumber;
        return statementLine > 0
            && text.Lines[statementLine - 1]
                .ToString()
                .Trim()
                .Equals(AllowMarker, StringComparison.Ordinal);
    }

    private static bool IsAllowMarker(SyntaxTrivia trivia)
    {
        return trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
            && trivia.ToString().Trim().Equals(
                AllowMarker,
                StringComparison.Ordinal);
    }

    private readonly struct RegionScope
    {
        internal RegionScope(
            ILocalSymbol symbol,
            string name,
            int start,
            int end)
        {
            Symbol = symbol;
            Name = name;
            Start = start;
            End = end;
        }

        internal ILocalSymbol Symbol { get; }

        internal string Name { get; }

        internal int Start { get; }

        internal int End { get; }
    }

    private readonly struct AllocationFinding
    {
        internal AllocationFinding(string kind, SyntaxNode source)
        {
            Kind = kind;
            Source = source;
        }

        internal string Kind { get; }

        internal SyntaxNode Source { get; }
    }

    private sealed class DelegateFlowState
    {
        private readonly HashSet<IMethodSymbol> _sources;

        private DelegateFlowState(bool hasUnknownSource)
        {
            HasUnknownSource = hasUnknownSource;
            _sources = new HashSet<IMethodSymbol>(
                SymbolEqualityComparer.Default);
        }

        private DelegateFlowState(DelegateFlowState source)
        {
            HasUnknownSource = source.HasUnknownSource;
            _sources = new HashSet<IMethodSymbol>(
                source._sources,
                SymbolEqualityComparer.Default);
        }

        internal bool HasUnknownSource { get; private set; }

        internal IEnumerable<IMethodSymbol> Sources => _sources;

        internal static DelegateFlowState Unknown() => new(true);

        internal DelegateFlowState Clone() => new(this);

        internal void Merge(DelegateFlowState other)
        {
            HasUnknownSource |= other.HasUnknownSource;
            _sources.UnionWith(other._sources);
        }

        internal void Replace(IEnumerable<IMethodSymbol> sources)
        {
            HasUnknownSource = false;
            _sources.Clear();
            _sources.UnionWith(sources);
        }

        internal void ReplaceWithUnknown()
        {
            HasUnknownSource = true;
            _sources.Clear();
        }

        internal void Apply(DelegateMutationEffect effect)
        {
            if (effect.HasUnknownSource)
            {
                ReplaceWithUnknown();
                return;
            }

            if (!effect.PreservesInput)
            {
                Replace(effect.Sources);
                return;
            }

            _sources.UnionWith(effect.Sources);
        }

        internal void CopyFrom(DelegateFlowState source)
        {
            HasUnknownSource = source.HasUnknownSource;
            _sources.Clear();
            _sources.UnionWith(source._sources);
        }

        internal static bool AreEqual(
            DelegateFlowState? first,
            DelegateFlowState? second)
        {
            if (ReferenceEquals(first, second))
            {
                return true;
            }

            return first is not null
                && second is not null
                && first.HasUnknownSource == second.HasUnknownSource
                && first._sources.SetEquals(second._sources);
        }
    }

    private readonly struct DelegateMutationEffect
    {
        private DelegateMutationEffect(
            bool hasMutation,
            bool hasUnknownSource,
            bool preservesInput,
            ImmutableArray<IMethodSymbol> sources)
        {
            HasMutation = hasMutation;
            HasUnknownSource = hasUnknownSource;
            PreservesInput = preservesInput;
            Sources = sources;
        }

        internal bool HasMutation { get; }

        internal bool HasUnknownSource { get; }

        internal bool PreservesInput { get; }

        internal ImmutableArray<IMethodSymbol> Sources { get; }

        internal static DelegateMutationEffect None { get; } = new(
            hasMutation: false,
            hasUnknownSource: false,
            preservesInput: true,
            sources: []);

        internal static DelegateMutationEffect Unknown { get; } = new(
            hasMutation: true,
            hasUnknownSource: true,
            preservesInput: false,
            sources: []);

        internal static DelegateMutationEffect Known(
            ImmutableArray<IMethodSymbol> sources,
            bool preservesInput) => new(
                hasMutation: true,
                hasUnknownSource: false,
                preservesInput,
                sources);
    }

    private sealed class AllocationFindingComparer : IEqualityComparer<AllocationFinding>
    {
        internal static AllocationFindingComparer Instance { get; } = new();

        public bool Equals(AllocationFinding x, AllocationFinding y)
        {
            return x.Kind == y.Kind
                && ReferenceEquals(x.Source.SyntaxTree, y.Source.SyntaxTree)
                && x.Source.Span.Equals(y.Source.Span);
        }

        public int GetHashCode(AllocationFinding finding)
        {
            unchecked
            {
                int hash = finding.Kind.GetHashCode();
                hash = (hash * 397) ^ finding.Source.SyntaxTree.GetHashCode();
                return (hash * 397) ^ finding.Source.Span.GetHashCode();
            }
        }
    }
}
