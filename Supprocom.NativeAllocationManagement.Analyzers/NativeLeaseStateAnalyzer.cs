using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Supprocom.NativeAllocationManagement.Analyzers;

/// <summary>Enforces bounded concurrent lease state authority.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NativeLeaseStateAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor CallbackAuthority = Create(
        "NAM1047",
        "Concurrent lease callback requires direct authority",
        "Concurrent lease callback '{0}' cannot use '{1}'. Use one exact static callback with scoped parameters.");

    private static readonly DiagnosticDescriptor OwnerBearingState = Create(
        "NAM1048",
        "Concurrent lease state cannot contain native ownership",
        "Concurrent lease state '{0}' contains '{1}'. Pass application state without NAM ownership.");

    private static readonly DiagnosticDescriptor StateEscape = Create(
        "NAM1049",
        "Concurrent lease state cannot escape",
        "Concurrent lease state '{0}' cannot escape through '{1}'. Keep it inside the Process callback.");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(
            CallbackAuthority,
            OwnerBearingState,
            StateEscape,
            NativeAllocationDiagnosticDescriptors.TransferViewEscape);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(
            GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(startContext =>
        {
            Symbols symbols = new(startContext.Compilation);
            if (!symbols.IsAvailable)
            {
                return;
            }

            startContext.RegisterSyntaxNodeAction(
                nodeContext => AnalyzeInvocation(
                    nodeContext,
                    symbols),
                SyntaxKind.InvocationExpression);
        });
    }

    private static void AnalyzeInvocation(
        SyntaxNodeAnalysisContext context,
        Symbols symbols)
    {
        if (context.Node is not InvocationExpressionSyntax syntax
            || !IsPotentialProcess(syntax))
        {
            return;
        }

        if (context.SemanticModel.GetOperation(
                syntax,
                context.CancellationToken)
                is not IInvocationOperation invocation
            || !symbols.IsLeaseStateProcess(invocation.TargetMethod))
        {
            ReportInvalidNestedCaptures(
                context,
                syntax,
                symbols);
            return;
        }

        IArgumentOperation? callbackArgument = invocation.Arguments
            .FirstOrDefault(argument =>
                symbols.IsStateCallback(argument.Parameter?.Type));
        IArgumentOperation? stateArgument = invocation.Arguments
            .FirstOrDefault(argument =>
                argument.Parameter?.RefKind == RefKind.In);
        if (callbackArgument?.Parameter?.Type
                is not INamedTypeSymbol callbackType
            || callbackType.TypeArguments.Length != 3
            || stateArgument is null)
        {
            return;
        }

        ITypeSymbol stateType = callbackType.TypeArguments[1];
        if (symbols.IsOwnerBearingState(stateType))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                OwnerBearingState,
                stateArgument.Syntax.GetLocation(),
                "state",
                stateType.ToDisplayString(
                    SymbolDisplayFormat.MinimallyQualifiedFormat)));
        }

        IOperation callback = UnwrapCallbackValue(
            callbackArgument.Value);
        HashSet<DiagnosticKey> reported = [];
        HashSet<IMethodSymbol> visitedMethods = new(
            SymbolEqualityComparer.Default);
        if (callback is IAnonymousFunctionOperation anonymous)
        {
            if (!IsDirectStaticCallback(
                    anonymous,
                    stateType,
                    symbols))
            {
                Report(
                    context.ReportDiagnostic,
                    reported,
                    CallbackAuthority,
                    callbackArgument.Syntax,
                    "callback",
                    "an indirect or capturing callback");
                return;
            }

            ReportNestedCaptures(
                context,
                anonymous,
                reported);
            AnalyzeBody(
                anonymous.Body,
                [anonymous.Symbol.Parameters[0]],
                [anonymous.Symbol.Parameters[1]],
                symbols,
                context.ReportDiagnostic,
                reported,
                visitedMethods,
                expressionReturn: false);
            return;
        }

        if (callback is not IMethodReferenceOperation methodReference
            || !IsDirectStaticCallback(
                methodReference.Method,
                stateType,
                symbols))
        {
            Report(
                context.ReportDiagnostic,
                reported,
                CallbackAuthority,
                callbackArgument.Syntax,
                "callback",
                "an indirect or capturing callback");
            return;
        }

        AnalyzeSourceMethod(
            methodReference.Method,
            [0],
            [1],
            symbols,
            context.ReportDiagnostic,
            reported,
            visitedMethods);
    }

    private static void ReportInvalidNestedCaptures(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax invocation,
        Symbols symbols)
    {
        SymbolInfo symbolInfo = context.SemanticModel.GetSymbolInfo(
            invocation,
            context.CancellationToken);
        IMethodSymbol? target = symbolInfo.Symbol as IMethodSymbol
            ?? symbolInfo.CandidateSymbols
                .OfType<IMethodSymbol>()
                .FirstOrDefault(symbols.IsLeaseStateProcess);
        if (target is null
            || invocation.ArgumentList.Arguments.LastOrDefault()
                ?.Expression is not { } callbackExpression)
        {
            return;
        }

        AnonymousFunctionExpressionSyntax? callback = callbackExpression
            .DescendantNodesAndSelf()
            .OfType<AnonymousFunctionExpressionSyntax>()
            .FirstOrDefault();
        ParameterSyntax[] parameters = callback switch
        {
            SimpleLambdaExpressionSyntax simple =>
                [simple.Parameter],
            ParenthesizedLambdaExpressionSyntax parenthesized =>
                parenthesized.ParameterList.Parameters.ToArray(),
            AnonymousMethodExpressionSyntax anonymous =>
                anonymous.ParameterList?.Parameters.ToArray()
                ?? [],
            _ => []
        };
        if (callback is null || parameters.Length != 2)
        {
            return;
        }

        HashSet<DiagnosticKey> reported = [];
        for (int ordinal = 0; ordinal < parameters.Length; ordinal++)
        {
            IParameterSymbol? parameter = context.SemanticModel
                .GetDeclaredSymbol(
                    parameters[ordinal],
                    context.CancellationToken);
            string name = parameters[ordinal].Identifier.ValueText;
            foreach (SyntaxNode nested in callback.DescendantNodes()
                .Where(node => node
                    is AnonymousFunctionExpressionSyntax
                        or LocalFunctionStatementSyntax))
            {
                bool captured = nested.DescendantNodes()
                    .OfType<IdentifierNameSyntax>()
                    .Any(identifier =>
                    {
                        ISymbol? referenced = context.SemanticModel
                            .GetSymbolInfo(
                                identifier,
                                context.CancellationToken).Symbol;
                        return parameter is not null
                            ? SymbolEqualityComparer.Default.Equals(
                                parameter,
                                referenced)
                            : identifier.Identifier.ValueText == name;
                    });
                if (!captured)
                {
                    continue;
                }

                Report(
                    context.ReportDiagnostic,
                    reported,
                    ordinal == 0
                        ? NativeAllocationDiagnosticDescriptors.TransferViewEscape
                        : StateEscape,
                    nested,
                    name,
                    nested is LocalFunctionStatementSyntax
                        ? "a nested local function"
                        : "a nested callback");
            }
        }
    }

    private static void ReportNestedCaptures(
        SyntaxNodeAnalysisContext context,
        IAnonymousFunctionOperation callback,
        HashSet<DiagnosticKey> reported)
    {
        foreach (SyntaxNode nested in callback.Syntax
            .DescendantNodes()
            .Where(node => node
                is AnonymousFunctionExpressionSyntax
                    or LocalFunctionStatementSyntax))
        {
            foreach (IdentifierNameSyntax identifier in nested
                .DescendantNodes()
                .OfType<IdentifierNameSyntax>())
            {
                ISymbol? symbol = context.SemanticModel.GetSymbolInfo(
                    identifier,
                    context.CancellationToken).Symbol;
                IParameterSymbol? parameter = callback.Symbol.Parameters
                    .FirstOrDefault(candidate =>
                        SymbolEqualityComparer.Default.Equals(
                            candidate,
                            symbol));
                if (parameter is null)
                {
                    continue;
                }

                Report(
                    context.ReportDiagnostic,
                    reported,
                    parameter.Ordinal == 0
                        ? NativeAllocationDiagnosticDescriptors.TransferViewEscape
                        : StateEscape,
                    nested,
                    parameter.Name,
                    nested is LocalFunctionStatementSyntax
                        ? "a nested local function"
                        : "a nested callback");
            }
        }
    }

    private static bool IsPotentialProcess(
        InvocationExpressionSyntax invocation)
    {
        SimpleNameSyntax? name = invocation.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name,
            MemberBindingExpressionSyntax binding => binding.Name,
            IdentifierNameSyntax identifier => identifier,
            GenericNameSyntax generic => generic,
            _ => null
        };
        return name?.Identifier.ValueText == "Process";
    }

    private static bool IsDirectStaticCallback(
        IAnonymousFunctionOperation anonymous,
        ITypeSymbol stateType,
        Symbols symbols) =>
        anonymous.Syntax is AnonymousFunctionExpressionSyntax syntax
        && syntax.Modifiers.Any(modifier =>
            modifier.IsKind(SyntaxKind.StaticKeyword))
        && anonymous.Symbol.IsStatic
        && !anonymous.Symbol.IsAsync
        && HasCallbackParameters(
            anonymous.Symbol.Parameters,
            stateType,
            symbols);

    private static bool IsDirectStaticCallback(
        IMethodSymbol method,
        ITypeSymbol stateType,
        Symbols symbols)
    {
        IMethodSymbol declaration = method.OriginalDefinition;
        return method.IsStatic
            && !method.IsAsync
            && declaration.DeclaringSyntaxReferences.Length == 1
            && !HasDirectYield(
                declaration.DeclaringSyntaxReferences[0].GetSyntax())
            && HasCallbackParameters(
                method.Parameters,
                stateType,
                symbols);
    }

    private static bool HasCallbackParameters(
        ImmutableArray<IParameterSymbol> parameters,
        ITypeSymbol stateType,
        Symbols symbols) =>
        parameters.Length == 2
        && parameters[0].RefKind == RefKind.None
        && symbols.IsLeaseView(parameters[0].Type)
        && parameters[1].RefKind == RefKind.None
        && SymbolEqualityComparer.Default.Equals(
            parameters[1].Type,
            stateType);

    private static IOperation UnwrapCallbackValue(IOperation value)
    {
        IOperation current = value;
        while (true)
        {
            if (current is IParenthesizedOperation parenthesized)
            {
                current = parenthesized.Operand;
                continue;
            }

            if (current is IConversionOperation conversion
                && conversion.OperatorMethod is null
                && (conversion.Conversion.IsIdentity
                    || conversion.Conversion.IsReference))
            {
                current = conversion.Operand;
                continue;
            }

            if (current is IDelegateCreationOperation creation)
            {
                current = creation.Target;
                continue;
            }

            return current;
        }
    }

    private static void AnalyzeSourceMethod(
        IMethodSymbol method,
        IReadOnlyCollection<int> viewOrdinals,
        IReadOnlyCollection<int> stateOrdinals,
        Symbols symbols,
        Action<Diagnostic> report,
        HashSet<DiagnosticKey> reported,
        HashSet<IMethodSymbol> visitedMethods)
    {
        IMethodSymbol declaration = method.OriginalDefinition;
        if (!visitedMethods.Add(declaration)
            || declaration.DeclaringSyntaxReferences.Length != 1)
        {
            return;
        }

        SyntaxNode declarationSyntax = declaration
            .DeclaringSyntaxReferences[0]
            .GetSyntax();
        SyntaxNode? body = declarationSyntax switch
        {
            MethodDeclarationSyntax methodSyntax =>
                (SyntaxNode?)methodSyntax.Body
                    ?? methodSyntax.ExpressionBody?.Expression,
            LocalFunctionStatementSyntax localSyntax =>
                (SyntaxNode?)localSyntax.Body
                    ?? localSyntax.ExpressionBody?.Expression,
            _ => null
        };
        if (body is null)
        {
            return;
        }

#pragma warning disable RS1030
        SemanticModel model = symbols.Compilation.GetSemanticModel(
            body.SyntaxTree);
#pragma warning restore RS1030
        if (model.GetOperation(body) is not { } operation)
        {
            return;
        }

        AnalyzeBody(
            operation,
            viewOrdinals.Select(ordinal =>
                declaration.Parameters[ordinal]).ToArray(),
            stateOrdinals.Select(ordinal =>
                declaration.Parameters[ordinal]).ToArray(),
            symbols,
            report,
            reported,
            visitedMethods,
            expressionReturn: body
                is not BlockSyntax);
    }

    private static void AnalyzeBody(
        IOperation body,
        IParameterSymbol[] viewParameters,
        IParameterSymbol[] stateParameters,
        Symbols symbols,
        Action<Diagnostic> report,
        HashSet<DiagnosticKey> reported,
        HashSet<IMethodSymbol> visitedMethods,
        bool expressionReturn)
    {
        CallbackUsageWalker walker = new(
            body,
            viewParameters,
            stateParameters,
            symbols,
            report,
            reported,
            visitedMethods);
        if (expressionReturn)
        {
            walker.ReportExpressionReturn(body);
        }

        walker.Visit(body);
    }

    private static bool HasDirectYield(SyntaxNode declaration) =>
        declaration.DescendantNodes(descendIntoChildren: node =>
            node is not AnonymousFunctionExpressionSyntax
                and not LocalFunctionStatementSyntax)
            .Any(node => node is YieldStatementSyntax);

    private static void Report(
        Action<Diagnostic> report,
        HashSet<DiagnosticKey> reported,
        DiagnosticDescriptor descriptor,
        SyntaxNode syntax,
        string name,
        string destination)
    {
        TextSpan span = syntax.Span;
        DiagnosticKey key = new(
            descriptor.Id,
            syntax.SyntaxTree,
            span.Start,
            span.Length);
        if (reported.Add(key))
        {
            report(Diagnostic.Create(
                descriptor,
                syntax.GetLocation(),
                name,
                destination));
        }
    }

    private static DiagnosticDescriptor Create(
        string id,
        string title,
        string message) =>
        new(
            id,
            title,
            message,
            "Supprocom.NativeAllocationManagement",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: message,
            helpLinkUri: "https://github.com/Supprocom/NativeAllocationManagement#ownership-diagnostics",
            customTags: WellKnownDiagnosticTags.Telemetry);

    private sealed class Symbols
    {
        private const string Namespace =
            "Supprocom.NativeAllocationManagement.";

        internal Symbols(Compilation compilation)
        {
            Compilation = compilation;
            ConcurrentPooled = compilation.GetTypeByMetadataName(
                Namespace + "ConcurrentPooled`1");
            IAssemblySymbol? runtime = ConcurrentPooled?.ContainingAssembly;
            if (runtime is null)
            {
                return;
            }

            StateCallback = runtime.GetTypeByMetadataName(
                Namespace + "NativeLeaseStateFunc`3");
            LeaseView = runtime.GetTypeByMetadataName(
                Namespace + "NativeLeaseView`1");
        }

        internal Compilation Compilation { get; }

        private INamedTypeSymbol? ConcurrentPooled { get; }

        private INamedTypeSymbol? StateCallback { get; }

        private INamedTypeSymbol? LeaseView { get; }

        internal bool IsAvailable =>
            ConcurrentPooled is not null
            && StateCallback is not null
            && LeaseView is not null;

        internal bool IsLeaseStateProcess(IMethodSymbol method) =>
            method.Name == "Process"
            && Is(method.ContainingType, ConcurrentPooled)
            && method.Parameters.Length == 3
            && method.Parameters[0].Type.SpecialType
                == SpecialType.System_Int32
            && method.Parameters[1].RefKind == RefKind.In
            && IsStateCallback(method.Parameters[2].Type);

        internal bool IsStateCallback(ITypeSymbol? type) =>
            Is(type, StateCallback);

        internal bool IsLeaseView(ITypeSymbol? type) =>
            Is(type, LeaseView);

        internal bool IsViewLike(ITypeSymbol? type)
        {
            if (IsLeaseView(type))
            {
                return true;
            }

            if (type is not INamedTypeSymbol named)
            {
                return type?.TypeKind == TypeKind.Pointer;
            }

            string name = named.OriginalDefinition.ToDisplayString();
            return name is "System.Span<T>"
                or "System.ReadOnlySpan<T>";
        }

        internal bool IsOwnerBearingState(ITypeSymbol type) =>
            IsOwnerBearingState(
                type,
                new HashSet<ITypeSymbol>(
                    SymbolEqualityComparer.Default));

        private bool IsOwnerBearingState(
            ITypeSymbol type,
            HashSet<ITypeSymbol> visited)
        {
            if (!visited.Add(type)
                || type.SpecialType != SpecialType.None
                    && type.SpecialType != SpecialType.System_Object)
            {
                return false;
            }

            if (type.SpecialType == SpecialType.System_Object
                || type.TypeKind is TypeKind.Dynamic
                    or TypeKind.Interface
                    or TypeKind.Delegate
                    or TypeKind.Pointer
                    or TypeKind.FunctionPointer
                    or TypeKind.TypeParameter)
            {
                return true;
            }

            if (type is IArrayTypeSymbol array)
            {
                return IsOwnerBearingState(
                    array.ElementType,
                    visited);
            }

            if (type is not INamedTypeSymbol named)
            {
                return false;
            }

            if (IsNamOwnershipType(named))
            {
                return true;
            }

            if (named.IsTupleType
                && named.TupleElements.Any(element =>
                    IsOwnerBearingState(
                        element.Type,
                        visited)))
            {
                return true;
            }

            if (!named.Locations.Any(location =>
                    location.IsInSource))
            {
                return false;
            }

            return named.GetMembers()
                .OfType<IFieldSymbol>()
                .Where(field => !field.IsStatic)
                .Any(field => IsOwnerBearingState(
                    field.Type,
                    visited));
        }

        private static bool IsNamOwnershipType(
            INamedTypeSymbol type)
        {
            if (type.ContainingNamespace.ToDisplayString()
                != "Supprocom.NativeAllocationManagement")
            {
                return false;
            }

            return type.OriginalDefinition.Name is
                "NativePool"
                or "NativeConcurrentPool"
                or "NativeRegion"
                or "NativeArena"
                or "NativeConcurrentArena"
                or "Pooled"
                or "ConcurrentPooled"
                or "Local"
                or "ArenaLease"
                or "ConcurrentArenaLease"
                or "NativeTransfer"
                or "NativeBuilder"
                or "NativeBuilderBorrow"
                or "NativeBuilderWriter"
                or "NativeLeaseView"
                or "NativeLeaseWriter"
                or "NativeSequentialLeaseWriter"
                or "NativeWorkspace";
        }

        private static bool Is(
            ITypeSymbol? candidate,
            INamedTypeSymbol? expected) =>
            candidate is INamedTypeSymbol named
            && SymbolEqualityComparer.Default.Equals(
                named.OriginalDefinition,
                expected);
    }

    private sealed class CallbackUsageWalker : OperationWalker
    {
        private readonly IParameterSymbol[] _viewParameters;
        private readonly IParameterSymbol[] _stateParameters;
        private readonly Symbols _symbols;
        private readonly Action<Diagnostic> _report;
        private readonly HashSet<DiagnosticKey> _reported;
        private readonly HashSet<IMethodSymbol> _visitedMethods;
        private readonly HashSet<ILocalSymbol> _bodyLocals =
            new(SymbolEqualityComparer.Default);
        private readonly HashSet<ILocalSymbol> _viewAliases =
            new(SymbolEqualityComparer.Default);
        private readonly HashSet<ILocalSymbol> _derivedViews =
            new(SymbolEqualityComparer.Default);
        private readonly HashSet<ILocalSymbol> _stateAliases =
            new(SymbolEqualityComparer.Default);
        private readonly HashSet<TextSpan> _permittedArguments = [];

        internal CallbackUsageWalker(
            IOperation body,
            IParameterSymbol[] viewParameters,
            IParameterSymbol[] stateParameters,
            Symbols symbols,
            Action<Diagnostic> report,
            HashSet<DiagnosticKey> reported,
            HashSet<IMethodSymbol> visitedMethods)
        {
            _viewParameters = viewParameters;
            _stateParameters = stateParameters;
            _symbols = symbols;
            _report = report;
            _reported = reported;
            _visitedMethods = visitedMethods;
            foreach (IVariableDeclaratorOperation declaration in body
                .DescendantsAndSelf()
                .OfType<IVariableDeclaratorOperation>())
            {
                _bodyLocals.Add(declaration.Symbol);
            }
        }

        internal void ReportExpressionReturn(IOperation operation)
        {
            if (IsStateValue(operation))
            {
                ReportState(
                    operation.Syntax,
                    StateName(operation),
                    "the callback return");
            }

            if (IsViewDerived(operation))
            {
                ReportView(
                    operation.Syntax,
                    ViewName(operation),
                    "the callback return");
            }
        }

        public override void VisitVariableDeclarator(
            IVariableDeclaratorOperation operation)
        {
            IOperation? value = operation.Initializer?.Value;
            if (IsStateValue(value))
            {
                _stateAliases.Add(operation.Symbol);
                ReportState(
                    operation.Syntax,
                    StateName(value),
                    "a local alias");
            }
            else if (IsExactViewValue(value))
            {
                _viewAliases.Add(operation.Symbol);
                ReportView(
                    operation.Syntax,
                    ViewName(value),
                    "a local alias");
            }
            else if (IsViewDerived(value)
                && _symbols.IsViewLike(operation.Symbol.Type))
            {
                _derivedViews.Add(operation.Symbol);
            }

            base.VisitVariableDeclarator(operation);
        }

        public override void VisitSimpleAssignment(
            ISimpleAssignmentOperation operation)
        {
            if (IsStateValue(operation.Value))
            {
                ReportState(
                    operation.Syntax,
                    StateName(operation.Value),
                    "an assignment");
            }

            if (IsExactViewValue(operation.Value))
            {
                ReportView(
                    operation.Syntax,
                    ViewName(operation.Value),
                    "an assignment");
            }
            else if (IsViewDerived(operation.Value))
            {
                if (operation.Target is ILocalReferenceOperation local
                    && _bodyLocals.Contains(local.Local)
                    && _symbols.IsViewLike(local.Type))
                {
                    _derivedViews.Add(local.Local);
                }
                else
                {
                    ReportView(
                        operation.Syntax,
                        ViewName(operation.Value),
                        "a nonlocal assignment");
                }
            }

            base.VisitSimpleAssignment(operation);
        }

        public override void VisitReturn(IReturnOperation operation)
        {
            if (IsStateValue(operation.ReturnedValue))
            {
                ReportState(
                    operation.Syntax,
                    StateName(operation.ReturnedValue),
                    "the callback return");
            }

            if (IsViewDerived(operation.ReturnedValue))
            {
                ReportView(
                    operation.Syntax,
                    ViewName(operation.ReturnedValue),
                    "the callback return");
            }

            base.VisitReturn(operation);
        }

        public override void VisitConversion(
            IConversionOperation operation)
        {
            if (IsStateValue(operation.Operand)
                && !SymbolEqualityComparer.Default.Equals(
                    operation.Type,
                    operation.Operand.Type))
            {
                ReportState(
                    operation.Syntax,
                    StateName(operation.Operand),
                    "a conversion or box");
            }

            if (IsViewDerived(operation.Operand)
                && !_symbols.IsViewLike(operation.Type))
            {
                ReportView(
                    operation.Syntax,
                    ViewName(operation.Operand),
                    "a conversion or box");
            }

            base.VisitConversion(operation);
        }

        public override void VisitInvocation(
            IInvocationOperation operation)
        {
            IArgumentOperation[] tracked = operation.Arguments
                .Where(argument =>
                    IsStateValue(argument.Value)
                    || IsExactViewValue(argument.Value))
                .ToArray();
            if (tracked.Length != 0)
            {
                if (TryAuthorizeHelper(operation, tracked))
                {
                    foreach (IArgumentOperation argument in tracked)
                    {
                        _permittedArguments.Add(argument.Syntax.Span);
                    }
                }
                else
                {
                    foreach (IArgumentOperation argument in tracked)
                    {
                        ReportInvalidArgument(argument, operation);
                    }
                }
            }

            base.VisitInvocation(operation);
        }

        public override void VisitArgument(
            IArgumentOperation operation)
        {
            if (!_permittedArguments.Contains(operation.Syntax.Span))
            {
                if (IsStateValue(operation.Value))
                {
                    ReportState(
                        operation.Syntax,
                        StateName(operation.Value),
                        "an unscoped call");
                }
                else if (IsExactViewValue(operation.Value))
                {
                    ReportView(
                        operation.Syntax,
                        ViewName(operation.Value),
                        "an unscoped call");
                }
            }

            base.VisitArgument(operation);
        }

        public override void VisitAnonymousFunction(
            IAnonymousFunctionOperation operation)
        {
            IOperation? captured = operation.Body
                .DescendantsAndSelf()
                .FirstOrDefault(item =>
                    IsTrackedStateReference(item)
                    || IsTrackedViewReference(item));
            if (captured is not null)
            {
                if (IsTrackedStateReference(captured))
                {
                    ReportState(
                        operation.Syntax,
                        StateName(captured),
                        "a nested callback");
                }
                else
                {
                    ReportView(
                        operation.Syntax,
                        ViewName(captured),
                        "a nested callback");
                }
            }
        }

        public override void VisitLocalFunction(
            ILocalFunctionOperation operation)
        {
            IOperation? captured = operation.Body?
                .DescendantsAndSelf()
                .FirstOrDefault(item =>
                    IsTrackedStateReference(item)
                    || IsTrackedViewReference(item));
            if (captured is not null)
            {
                if (IsTrackedStateReference(captured))
                {
                    ReportState(
                        operation.Syntax,
                        StateName(captured),
                        "a nested local function");
                }
                else
                {
                    ReportView(
                        operation.Syntax,
                        ViewName(captured),
                        "a nested local function");
                }
            }
        }

        private bool TryAuthorizeHelper(
            IInvocationOperation invocation,
            IReadOnlyCollection<IArgumentOperation> tracked)
        {
            IMethodSymbol method = invocation.TargetMethod;
            IMethodSymbol declaration = method.OriginalDefinition;
            if (!method.IsStatic
                || method.IsAsync
                || declaration.DeclaringSyntaxReferences.Length != 1
                || HasDirectYield(
                    declaration.DeclaringSyntaxReferences[0]
                        .GetSyntax()))
            {
                return false;
            }

            List<int> viewOrdinals = [];
            List<int> stateOrdinals = [];
            foreach (IArgumentOperation argument in tracked)
            {
                IParameterSymbol? parameter = argument.Parameter;
                if (parameter is null)
                {
                    return false;
                }

                if (IsExactViewValue(argument.Value))
                {
                    if (parameter.RefKind != RefKind.None
                        || parameter.ScopedKind == ScopedKind.None
                        || !_symbols.IsLeaseView(parameter.Type))
                    {
                        return false;
                    }

                    viewOrdinals.Add(parameter.Ordinal);
                    continue;
                }

                if (parameter.RefKind != RefKind.In
                    || parameter.ScopedKind == ScopedKind.None
                    || !SymbolEqualityComparer.Default.Equals(
                        parameter.Type,
                        argument.Value.Type))
                {
                    return false;
                }

                stateOrdinals.Add(parameter.Ordinal);
            }

            if (viewOrdinals.Count > 1
                || stateOrdinals.Count > 1)
            {
                return false;
            }

            AnalyzeSourceMethod(
                method,
                viewOrdinals,
                stateOrdinals,
                _symbols,
                _report,
                _reported,
                _visitedMethods);
            return true;
        }

        private void ReportInvalidArgument(
            IArgumentOperation argument,
            IInvocationOperation invocation)
        {
            string destination = invocation.TargetMethod.ToDisplayString(
                SymbolDisplayFormat.MinimallyQualifiedFormat);
            if (IsStateValue(argument.Value))
            {
                ReportState(
                    argument.Syntax,
                    StateName(argument.Value),
                    destination);
            }
            else
            {
                ReportView(
                    argument.Syntax,
                    ViewName(argument.Value),
                    destination);
            }
        }

        private bool IsStateValue(IOperation? operation)
        {
            IOperation? current = UnwrapValue(operation);
            if (current is IParameterReferenceOperation parameter)
            {
                return _stateParameters.Any(state =>
                    SymbolEqualityComparer.Default.Equals(
                        state,
                        parameter.Parameter));
            }

            if (current is ILocalReferenceOperation local)
            {
                return _stateAliases.Contains(local.Local);
            }

            if (current is IConditionalOperation conditional)
            {
                return IsStateValue(conditional.WhenTrue)
                    || IsStateValue(conditional.WhenFalse);
            }

            if (current is ICoalesceOperation coalesce)
            {
                return IsStateValue(coalesce.Value)
                    || IsStateValue(coalesce.WhenNull);
            }

            return current is ITupleOperation tuple
                && tuple.Elements.Any(IsStateValue);
        }

        private bool IsExactViewValue(IOperation? operation)
        {
            IOperation? current = UnwrapValue(operation);
            if (current is IParameterReferenceOperation parameter)
            {
                return _viewParameters.Any(view =>
                    SymbolEqualityComparer.Default.Equals(
                        view,
                        parameter.Parameter));
            }

            if (current is ILocalReferenceOperation local)
            {
                return _viewAliases.Contains(local.Local);
            }

            if (current is IConditionalOperation conditional)
            {
                return IsExactViewValue(conditional.WhenTrue)
                    || IsExactViewValue(conditional.WhenFalse);
            }

            return current is ICoalesceOperation coalesce
                && (IsExactViewValue(coalesce.Value)
                    || IsExactViewValue(coalesce.WhenNull));
        }

        private bool IsViewDerived(IOperation? operation)
        {
            if (operation is null
                || !_symbols.IsViewLike(operation.Type))
            {
                return false;
            }

            return operation.DescendantsAndSelf().Any(item =>
                IsTrackedViewReference(item));
        }

        private bool IsTrackedStateReference(IOperation operation) =>
            operation is IParameterReferenceOperation parameter
                && _stateParameters.Any(state =>
                    SymbolEqualityComparer.Default.Equals(
                        state,
                        parameter.Parameter))
            || operation is ILocalReferenceOperation local
                && _stateAliases.Contains(local.Local);

        private bool IsTrackedViewReference(IOperation operation) =>
            operation is IParameterReferenceOperation parameter
                && _viewParameters.Any(view =>
                    SymbolEqualityComparer.Default.Equals(
                        view,
                        parameter.Parameter))
            || operation is ILocalReferenceOperation local
                && (_viewAliases.Contains(local.Local)
                    || _derivedViews.Contains(local.Local));

        private static IOperation? UnwrapValue(IOperation? operation)
        {
            IOperation? current = operation;
            while (current is IParenthesizedOperation parenthesized
                || current is IConversionOperation
                {
                    OperatorMethod: null
                } conversion
                && conversion.Conversion.IsIdentity)
            {
                current = current switch
                {
                    IParenthesizedOperation item => item.Operand,
                    IConversionOperation item => item.Operand,
                    _ => current
                };
            }

            return current;
        }

        private string StateName(IOperation? operation) =>
            operation?.DescendantsAndSelf()
                .OfType<IParameterReferenceOperation>()
                .FirstOrDefault(reference =>
                    _stateParameters.Any(state =>
                        SymbolEqualityComparer.Default.Equals(
                            state,
                            reference.Parameter)))
                ?.Parameter.Name
            ?? "state";

        private string ViewName(IOperation? operation) =>
            operation?.DescendantsAndSelf()
                .OfType<IParameterReferenceOperation>()
                .FirstOrDefault(reference =>
                    _viewParameters.Any(view =>
                        SymbolEqualityComparer.Default.Equals(
                            view,
                            reference.Parameter)))
                ?.Parameter.Name
            ?? "view";

        private void ReportState(
            SyntaxNode syntax,
            string name,
            string destination) =>
            Report(
                _report,
                _reported,
                StateEscape,
                syntax,
                name,
                destination);

        private void ReportView(
            SyntaxNode syntax,
            string name,
            string destination) =>
            Report(
                _report,
                _reported,
                NativeAllocationDiagnosticDescriptors.TransferViewEscape,
                syntax,
                name,
                destination);
    }

    private readonly struct DiagnosticKey : IEquatable<DiagnosticKey>
    {
        internal DiagnosticKey(
            string id,
            SyntaxTree tree,
            int start,
            int length)
        {
            Id = id;
            Tree = tree;
            Start = start;
            Length = length;
        }

        private string Id { get; }

        private SyntaxTree Tree { get; }

        private int Start { get; }

        private int Length { get; }

        public bool Equals(DiagnosticKey other) =>
            Id == other.Id
            && ReferenceEquals(Tree, other.Tree)
            && Start == other.Start
            && Length == other.Length;

        public override bool Equals(object? obj) =>
            obj is DiagnosticKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Id.GetHashCode();
                hash = (hash * 397) ^ Tree.GetHashCode();
                hash = (hash * 397) ^ Start;
                return (hash * 397) ^ Length;
            }
        }
    }
}
