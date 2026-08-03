using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
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

        IMethodSymbol? sourceMethod = ResolveSourceMethod(
            invocation,
            context.CancellationToken);
        if (sourceMethod is null
            || !SymbolEqualityComparer.Default.Equals(
                sourceMethod.ContainingAssembly,
                _compilation.Assembly)
            || sourceMethod.DeclaringSyntaxReferences.Length == 0)
        {
            return;
        }

        if (sourceMethod.IsAsync)
        {
            Report(
                context,
                invocation.Syntax,
                "async state-machine creation",
                region,
                invocation.Syntax);
        }

        if (IsIterator(sourceMethod, context.CancellationToken))
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
            Report(
                context,
                invocation.Syntax,
                finding.Kind + " in source method '" + sourceMethod.Name + "'",
                region,
                finding.Source);
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

            IMethodSymbol? sourceMethod = ResolveDelegateSource(
                argument.Value,
                context.CancellationToken);
            if (sourceMethod is null
                || !SymbolEqualityComparer.Default.Equals(
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

    private bool IsCanonicalAllocation(IMethodSymbol method)
    {
        return _canonicalAllocations.Contains(method.OriginalDefinition);
    }

    private IMethodSymbol? ResolveSourceMethod(
        IInvocationOperation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.TargetMethod.MethodKind != MethodKind.DelegateInvoke)
        {
            return invocation.TargetMethod;
        }

        return ResolveDelegateSource(
            invocation.Instance,
            cancellationToken);
    }

    private IMethodSymbol? ResolveDelegateSource(
        IOperation? operation,
        CancellationToken cancellationToken)
    {
        IOperation? value = Unwrap(operation);
        if (value is IDelegateCreationOperation delegateCreation)
        {
            return GetDelegateTargetMethod(delegateCreation.Target);
        }

        IMethodSymbol? directMethod = value is null
            ? null
            : GetDelegateTargetMethod(value);
        if (directMethod is not null)
        {
            return directMethod;
        }

        if (value is ILocalReferenceOperation localReference)
        {
            SyntaxNode? declaration = localReference.Local.DeclaringSyntaxReferences
                .FirstOrDefault()
                ?.GetSyntax(cancellationToken);
            if (declaration is VariableDeclaratorSyntax
                {
                    Initializer.Value: ExpressionSyntax initializer
                })
            {
                SemanticModel model = _compilation.GetSemanticModel(
                    initializer.SyntaxTree);
                IOperation? localValue = Unwrap(model.GetOperation(
                    initializer,
                    cancellationToken));
                if (localValue is IDelegateCreationOperation localDelegate)
                {
                    return GetDelegateTargetMethod(localDelegate.Target);
                }

                return localValue is null
                    ? null
                    : GetDelegateTargetMethod(localValue);
            }
        }

        return null;
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
