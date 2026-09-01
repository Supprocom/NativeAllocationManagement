using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Supprocom.NativeAllocationManagement.Analyzers;

/// <summary>Enforces bounded NativeBuilder write authority.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NativeBuilderWriteAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor ViewEscape = Create(
        "NAM1041",
        "Native builder write view cannot escape",
        "Builder write view '{0}' cannot escape through '{1}'. Use it only in the NativeBuilder.Write callback.");

    private static readonly DiagnosticDescriptor InvalidAuthority = Create(
        "NAM1042",
        "Native builder write authority must remain direct",
        "Builder writer '{0}' cannot transfer commit authority through '{1}'. Use only a source-visible scoped ref helper.");

    private static readonly DiagnosticDescriptor BorrowEscape = Create(
        "NAM1043",
        "Exclusive native builder borrow cannot escape",
        "Builder borrow '{0}' cannot escape through '{1}'. Use it only during the NativeBuilder.Borrow callback.");

    private static readonly DiagnosticDescriptor InvalidBorrowAuthority = Create(
        "NAM1044",
        "Exclusive native builder borrow requires scoped ref authority",
        "Builder borrow '{0}' cannot use '{1}'. Forward it only through a source-visible scoped ref parameter.");

    private static readonly DiagnosticDescriptor StateEscape = Create(
        "NAM1045",
        "Native builder write state cannot escape",
        "Builder write state '{0}' cannot escape through '{1}'. Keep it inside the active Write callback.");

    private static readonly DiagnosticDescriptor InvalidStateAuthority = Create(
        "NAM1046",
        "Native builder write state requires direct authority",
        "Builder write state '{0}' cannot use '{1}'. Use one exact static callback and scoped input state.");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(
            ViewEscape,
            InvalidAuthority,
            BorrowEscape,
            InvalidBorrowAuthority,
            StateEscape,
            InvalidStateAuthority);

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
                nodeContext => AnalyzeDeclaredMethod(
                    nodeContext,
                    symbols),
                SyntaxKind.MethodDeclaration,
                SyntaxKind.ConstructorDeclaration,
                SyntaxKind.DestructorDeclaration,
                SyntaxKind.OperatorDeclaration,
                SyntaxKind.ConversionOperatorDeclaration,
                SyntaxKind.LocalFunctionStatement);
            startContext.RegisterSyntaxNodeAction(
                nodeContext => AnalyzeInvocation(
                    nodeContext,
                    symbols),
                SyntaxKind.InvocationExpression);
            startContext.RegisterSyntaxNodeAction(
                nodeContext => AnalyzeAnonymousFunction(
                    nodeContext,
                    symbols),
                SyntaxKind.SimpleLambdaExpression,
                SyntaxKind.ParenthesizedLambdaExpression,
                SyntaxKind.AnonymousMethodExpression);
        });
    }

    private static void AnalyzeAnonymousFunction(
        SyntaxNodeAnalysisContext context,
        Symbols symbols)
    {
        if (!HasPotentialBuilderInvocationAncestor(context.Node)
            || context.SemanticModel.GetOperation(
            context.Node,
            context.CancellationToken)
            is not IAnonymousFunctionOperation anonymous
            || anonymous.Symbol.Parameters.Length == 0)
        {
            return;
        }

        IParameterSymbol[] parameters = anonymous.Symbol.Parameters.ToArray();
        if (parameters.Length == 1
            && symbols.IsWriter(parameters[0].Type))
        {
            AnalyzeAuthorityBody(
                anonymous.Body,
                parameters,
                [],
                [],
                symbols,
                context.ReportDiagnostic,
                expressionReturn: false);
            return;
        }

        if (parameters.Length == 2
            && (symbols.IsWriter(parameters[0].Type)
                || symbols.IsBorrow(parameters[0].Type))
            && !symbols.IsBorrow(parameters[1].Type))
        {
            AnalyzeAuthorityBody(
                anonymous.Body,
                symbols.IsWriter(parameters[0].Type)
                    ? [parameters[0]]
                    : [],
                symbols.IsBorrow(parameters[0].Type)
                    ? [parameters[0]]
                    : [],
                [parameters[1]],
                symbols,
                context.ReportDiagnostic,
                expressionReturn: false);
            return;
        }

        if (parameters.Length is not (1 or 2)
            || parameters.Any(parameter =>
                !symbols.IsBorrow(parameter.Type)))
        {
            return;
        }

        ReportInvalidBorrowParameters(
            parameters,
            context.ReportDiagnostic);
        AnalyzeAuthorityBody(
            anonymous.Body,
            [],
            parameters,
            [],
            symbols,
            context.ReportDiagnostic,
            expressionReturn: false);
    }

    private static bool HasPotentialBuilderInvocationAncestor(
        SyntaxNode node)
    {
        for (SyntaxNode? ancestor = node.Parent;
            ancestor is not null;
            ancestor = ancestor.Parent)
        {
            if (ancestor is InvocationExpressionSyntax invocation
                && IsPotentialBuilderInvocation(invocation))
            {
                return true;
            }
        }

        return false;
    }

    private static void AnalyzeDeclaredMethod(
        SyntaxNodeAnalysisContext context,
        Symbols symbols)
    {
        SyntaxNode? body;
        IMethodSymbol? method;
        bool expressionBody;
        if (context.Node is BaseMethodDeclarationSyntax baseMethod)
        {
            method = context.SemanticModel.GetDeclaredSymbol(
                baseMethod,
                context.CancellationToken) as IMethodSymbol;
            body = (SyntaxNode?)baseMethod.Body
                ?? baseMethod.ExpressionBody?.Expression;
            expressionBody = baseMethod.ExpressionBody is not null;
        }
        else if (context.Node
            is LocalFunctionStatementSyntax localFunction)
        {
            method = context.SemanticModel.GetDeclaredSymbol(
                localFunction,
                context.CancellationToken) as IMethodSymbol;
            body = (SyntaxNode?)localFunction.Body
                ?? localFunction.ExpressionBody?.Expression;
            expressionBody = localFunction.ExpressionBody is not null;
        }
        else
        {
            return;
        }

        if (method is null)
        {
            return;
        }

        IParameterSymbol[] writers = method.Parameters
            .Where(parameter => symbols.IsWriter(parameter.Type))
            .ToArray();
        IParameterSymbol[] borrows = method.Parameters
            .Where(parameter => symbols.IsBorrow(parameter.Type))
            .ToArray();
        IParameterSymbol[] states = writers.Length + borrows.Length == 1
                && method.Parameters.Length == 2
            ? method.Parameters
                .Where(parameter =>
                    !writers.Any(writer =>
                        SymbolEqualityComparer.Default.Equals(
                            parameter,
                            writer))
                    && !borrows.Any(borrow =>
                        SymbolEqualityComparer.Default.Equals(
                            parameter,
                            borrow)))
                .ToArray()
            : [];
        if (writers.Length == 0
            && borrows.Length == 0
            && states.Length == 0)
        {
            return;
        }

        if (borrows.Length != 0)
        {
            ReportInvalidBorrowParameters(
                borrows,
                context.ReportDiagnostic);
        }

        if (body is null
            || context.SemanticModel.GetOperation(
                body,
                context.CancellationToken)
                is not { } operation)
        {
            return;
        }

        AnalyzeAuthorityBody(
            operation,
            writers,
            borrows,
            states,
            symbols,
            context.ReportDiagnostic,
            expressionBody && !method.ReturnsVoid);
    }

    private static bool IsPotentialBuilderInvocation(
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
        return name?.Identifier.ValueText is "Write" or "Borrow";
    }

    private static void AnalyzeInvocation(
        SyntaxNodeAnalysisContext context,
        Symbols symbols)
    {
        if (context.Node is not InvocationExpressionSyntax syntax
            || !IsPotentialBuilderInvocation(syntax)
            || context.SemanticModel.GetOperation(
                syntax,
                context.CancellationToken)
                is not IInvocationOperation invocation)
        {
            return;
        }

        if (symbols.IsBuilderStateWrite(invocation.TargetMethod))
        {
            AnalyzeStateCallbackArgument(
                context.ReportDiagnostic,
                invocation,
                symbols,
                symbols.IsWriteStateAction,
                symbols.IsWriter,
                RefKind.None,
                "writer state");
            return;
        }

        if (symbols.IsBuilderCompileTimeStateWrite(
                invocation.TargetMethod))
        {
            AnalyzeCompileTimeStateWrite(
                context.ReportDiagnostic,
                invocation,
                symbols);
            return;
        }

        if (symbols.IsBuilderWrite(invocation.TargetMethod))
        {
            AnalyzeCallbackArgument(
                context.ReportDiagnostic,
                invocation,
                symbols.GetWriteActionArity,
                symbols.IsWriter,
                RefKind.None,
                InvalidAuthority,
                "writer");
            return;
        }

        if (symbols.IsBuilderBorrow(invocation.TargetMethod))
        {
            AnalyzeCallbackArgument(
                context.ReportDiagnostic,
                invocation,
                symbols.GetBorrowActionArity,
                symbols.IsBorrow,
                RefKind.Ref,
                InvalidBorrowAuthority,
                "borrow");
            return;
        }

        if (symbols.IsBuilderStateBorrow(invocation.TargetMethod))
        {
            AnalyzeStateCallbackArgument(
                context.ReportDiagnostic,
                invocation,
                symbols,
                symbols.IsBorrowStateAction,
                symbols.IsBorrow,
                RefKind.Ref,
                "borrow state");
        }
    }

    private static void AnalyzeAuthorityBody(
        IOperation body,
        IParameterSymbol[] writers,
        IParameterSymbol[] borrows,
        IParameterSymbol[] states,
        Symbols symbols,
        Action<Diagnostic> report,
        bool expressionReturn)
    {
        if (writers.Length != 0)
        {
            WriterUsageWalker writerWalker = new(
                symbols,
                writers,
                report);
            if (expressionReturn)
            {
                writerWalker.ReportExpressionReturn(body);
            }

            writerWalker.Visit(body);
        }

        if (borrows.Length != 0)
        {
            BorrowUsageWalker borrowWalker = new(
                symbols,
                borrows,
                report);
            if (expressionReturn)
            {
                borrowWalker.ReportExpressionReturn(body);
            }

            borrowWalker.Visit(body);
        }

        if (states.Length != 0)
        {
            StateUsageWalker stateWalker = new(
                symbols,
                states,
                report);
            if (expressionReturn)
            {
                stateWalker.ReportExpressionReturn(body);
            }

            stateWalker.Visit(body);
        }
    }

    private static void AnalyzeCallbackArgument(
        Action<Diagnostic> report,
        IInvocationOperation invocation,
        Func<ITypeSymbol?, int> getActionArity,
        Func<ITypeSymbol?, bool> isAuthority,
        RefKind refKind,
        DiagnosticDescriptor descriptor,
        string name)
    {
        IArgumentOperation? callback = invocation.Arguments
            .FirstOrDefault(argument =>
                getActionArity(argument.Parameter?.Type) != 0);
        if (callback is null)
        {
            return;
        }

        int expectedParameterCount = getActionArity(
            callback.Parameter?.Type);
        if (!IsDirectCallback(
            callback.Value,
            isAuthority,
            refKind,
            expectedParameterCount))
        {
            report(Diagnostic.Create(
                descriptor,
                callback.Syntax.GetLocation(),
                name,
                "an indirect callback"));
        }
    }

    private static void AnalyzeStateCallbackArgument(
        Action<Diagnostic> report,
        IInvocationOperation invocation,
        Symbols symbols,
        Func<ITypeSymbol?, bool> isAction,
        Func<ITypeSymbol?, bool> isAuthority,
        RefKind authorityRefKind,
        string authorityName)
    {
        IArgumentOperation? callback = invocation.Arguments
            .FirstOrDefault(argument =>
                isAction(argument.Parameter?.Type));
        if (callback is null
            || callback.Parameter?.Type is not INamedTypeSymbol actionType
            || actionType.TypeArguments.Length != 2)
        {
            return;
        }

        ITypeSymbol stateType = actionType.TypeArguments[1];
        if (!IsDirectStateCallback(
                callback.Value,
                symbols,
                stateType,
                isAuthority,
                authorityRefKind))
        {
            report(Diagnostic.Create(
                InvalidStateAuthority,
                callback.Syntax.GetLocation(),
                authorityName,
                "an indirect or capturing callback"));
        }

        IArgumentOperation? state = invocation.Arguments
            .FirstOrDefault(argument =>
                argument.Parameter?.RefKind == RefKind.In);
        if (state is not null
            && symbols.IsOwnerBearingState(stateType))
        {
            report(Diagnostic.Create(
                InvalidStateAuthority,
                state.Syntax.GetLocation(),
                "state",
                "an ownership-bearing state type"));
        }
    }

    private static void AnalyzeCompileTimeStateWrite(
        Action<Diagnostic> report,
        IInvocationOperation invocation,
        Symbols symbols)
    {
        if (!symbols.TryGetCompileTimeStateWrite(
                invocation.TargetMethod,
                out ITypeSymbol stateType,
                out IMethodSymbol action))
        {
            report(Diagnostic.Create(
                InvalidStateAuthority,
                invocation.Syntax.GetLocation(),
                "writer state",
                "an unresolved compile-time callback"));
            return;
        }

        if (action.IsAsync
            || action.DeclaringSyntaxReferences.Length != 1
            || HasDirectYield(action.DeclaringSyntaxReferences[0]
                .GetSyntax()))
        {
            report(Diagnostic.Create(
                InvalidStateAuthority,
                invocation.Syntax.GetLocation(),
                "writer state",
                "an asynchronous or iterator callback"));
        }

        IArgumentOperation? state = invocation.Arguments
            .FirstOrDefault(argument =>
                argument.Parameter?.RefKind == RefKind.In);
        if (state is not null
            && symbols.IsOwnerBearingState(stateType))
        {
            report(Diagnostic.Create(
                InvalidStateAuthority,
                state.Syntax.GetLocation(),
                "state",
                "an ownership-bearing state type"));
        }
    }

    private static bool HasDirectYield(SyntaxNode declaration) =>
        declaration.DescendantNodes(descendIntoChildren: node =>
            node is not AnonymousFunctionExpressionSyntax
                and not LocalFunctionStatementSyntax)
            .Any(node => node is YieldStatementSyntax);

    private static bool IsDirectStateCallback(
        IOperation value,
        Symbols symbols,
        ITypeSymbol stateType,
        Func<ITypeSymbol?, bool> isAuthority,
        RefKind authorityRefKind)
    {
        IOperation callback = UnwrapCallbackValue(value);
        if (callback is IAnonymousFunctionOperation anonymous)
        {
            return anonymous.Syntax
                    is AnonymousFunctionExpressionSyntax syntax
                && syntax.Modifiers.Any(modifier =>
                    modifier.IsKind(SyntaxKind.StaticKeyword))
                && !anonymous.Symbol.IsAsync
                && HasStateCallbackParameters(
                    anonymous.Symbol.Parameters,
                    symbols,
                    stateType,
                    isAuthority,
                    authorityRefKind);
        }

        if (callback is not IMethodReferenceOperation reference)
        {
            return false;
        }

        IMethodSymbol method = reference.Method;
        IMethodSymbol declaration = method.OriginalDefinition;
        return method.ReturnsVoid
            && method.IsStatic
            && !method.IsAsync
            && declaration.DeclaringSyntaxReferences.Length == 1
            && HasStateCallbackParameters(
                method.Parameters,
                symbols,
                stateType,
                isAuthority,
                authorityRefKind);
    }

    private static bool HasStateCallbackParameters(
        ImmutableArray<IParameterSymbol> parameters,
        Symbols symbols,
        ITypeSymbol stateType,
        Func<ITypeSymbol?, bool> isAuthority,
        RefKind authorityRefKind) =>
        parameters.Length == 2
        && parameters[0].RefKind == authorityRefKind
        && parameters[0].ScopedKind != ScopedKind.None
        && isAuthority(parameters[0].Type)
        && parameters[1].RefKind == RefKind.In
        && parameters[1].ScopedKind != ScopedKind.None
        && SymbolEqualityComparer.Default.Equals(
            parameters[1].Type,
            stateType);

    private static bool IsDirectCallback(
        IOperation value,
        Func<ITypeSymbol?, bool> isAuthority,
        RefKind refKind,
        int expectedParameterCount)
    {
        IOperation callback = UnwrapCallbackValue(value);
        if (callback is IAnonymousFunctionOperation anonymous)
        {
            return anonymous.Symbol.Parameters.Length
                    == expectedParameterCount
                && anonymous.Symbol.Parameters.All(parameter =>
                    parameter.RefKind == refKind
                    && isAuthority(parameter.Type));
        }

        if (callback is not IMethodReferenceOperation reference)
        {
            return false;
        }

        IMethodSymbol declaration = reference.Method.OriginalDefinition;
        return declaration.ReturnsVoid
            && !declaration.IsAsync
            && declaration.Parameters.Length == expectedParameterCount
            && declaration.Parameters.All(parameter =>
                parameter.RefKind == refKind
                && parameter.ScopedKind != ScopedKind.None
                && isAuthority(parameter.Type))
            && declaration.DeclaringSyntaxReferences.Length == 1;
    }

    private static IOperation UnwrapCallbackValue(IOperation value)
    {
        IOperation current = value;
        while (true)
        {
            switch (current)
            {
                case IParenthesizedOperation parenthesized:
                    current = parenthesized.Operand;
                    continue;
                case IConversionOperation conversion
                    when conversion.OperatorMethod is null
                    && (conversion.Conversion.IsIdentity
                        || conversion.Conversion.IsReference):
                    current = conversion.Operand;
                    continue;
                case IDelegateCreationOperation creation:
                    current = creation.Target;
                    continue;
                default:
                    return current;
            }
        }
    }

    private static void ReportInvalidBorrowParameters(
        IEnumerable<IParameterSymbol> borrows,
        Action<Diagnostic> report)
    {
        foreach (IParameterSymbol borrow in borrows)
        {
            if (borrow.RefKind == RefKind.Ref
                && borrow.ScopedKind != ScopedKind.None)
            {
                continue;
            }

            Location location = borrow.Locations
                .FirstOrDefault(static item => item.IsInSource)
                ?? Location.None;
            report(Diagnostic.Create(
                InvalidBorrowAuthority,
                location,
                borrow.Name,
                borrow.RefKind.ToString()));
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
            MemoryMarshal = compilation.GetTypeByMetadataName(
                "System.Runtime.InteropServices.MemoryMarshal");
            Span = compilation.GetTypeByMetadataName(
                "System.Span`1");
            Builder = compilation.GetTypeByMetadataName(
                Namespace + "NativeBuilder`1");
            IAssemblySymbol? runtimeAssembly =
                Builder?.ContainingAssembly;
            if (runtimeAssembly is null)
            {
                return;
            }

            Writer = runtimeAssembly.GetTypeByMetadataName(
                Namespace + "NativeBuilderWriter`1");
            WriteAction = runtimeAssembly.GetTypeByMetadataName(
                Namespace + "NativeBuilderWriteAction`1");
            WriteStateAction = runtimeAssembly.GetTypeByMetadataName(
                Namespace + "NativeBuilderWriteStateAction`2");
            CompileTimeWriteAction = runtimeAssembly.GetTypeByMetadataName(
                Namespace + "INativeBuilderWriteAction`2");
            Borrow = runtimeAssembly.GetTypeByMetadataName(
                Namespace + "NativeBuilderBorrow`1");
            BorrowAction = runtimeAssembly.GetTypeByMetadataName(
                Namespace + "NativeBuilderBorrowAction`1");
            PairBorrowAction = runtimeAssembly.GetTypeByMetadataName(
                Namespace + "NativeBuilderPairBorrowAction`1");
            BorrowStateAction = runtimeAssembly.GetTypeByMetadataName(
                Namespace + "NativeBuilderBorrowStateAction`2");
        }

        internal Compilation Compilation { get; }

        internal INamedTypeSymbol? Builder { get; }

        private INamedTypeSymbol? MemoryMarshal { get; }

        private INamedTypeSymbol? Span { get; }

        internal INamedTypeSymbol? Writer { get; }

        internal INamedTypeSymbol? WriteAction { get; }

        internal INamedTypeSymbol? WriteStateAction { get; }

        internal INamedTypeSymbol? CompileTimeWriteAction { get; }

        internal INamedTypeSymbol? Borrow { get; }

        internal INamedTypeSymbol? BorrowAction { get; }

        internal INamedTypeSymbol? PairBorrowAction { get; }

        internal INamedTypeSymbol? BorrowStateAction { get; }

        internal bool IsAvailable =>
            Builder is not null
            && Writer is not null
            && WriteAction is not null
            && WriteStateAction is not null
            && CompileTimeWriteAction is not null
            && Borrow is not null
            && BorrowAction is not null
            && PairBorrowAction is not null
            && BorrowStateAction is not null;

        internal bool IsBuilderWrite(IMethodSymbol method) =>
            method.Name == "Write"
            && (Is(method.ContainingType, Builder)
                || Is(method.ContainingType, Borrow))
            && method.Parameters.Any(parameter =>
                IsWriteAction(parameter.Type));

        internal bool IsBuilderStateWrite(IMethodSymbol method) =>
            method.Name == "Write"
            && (Is(method.ContainingType, Builder)
                || Is(method.ContainingType, Borrow))
            && method.Parameters.Any(parameter =>
                IsWriteStateAction(parameter.Type));

        internal bool IsBuilderCompileTimeStateWrite(
            IMethodSymbol method) =>
            method.Name == "Write"
            && (Is(method.ContainingType, Builder)
                || Is(method.ContainingType, Borrow))
            && method.TypeArguments.Length == 2
            && method.Parameters.Any(parameter =>
                parameter.RefKind == RefKind.In)
            && !method.Parameters.Any(parameter =>
                IsWriteStateAction(parameter.Type));

        internal bool TryGetCompileTimeStateWrite(
            IMethodSymbol method,
            out ITypeSymbol stateType,
            out IMethodSymbol action)
        {
            stateType = null!;
            action = null!;
            if (!IsBuilderCompileTimeStateWrite(method)
                || method.TypeArguments[1]
                    is not INamedTypeSymbol actionType
                || actionType.TypeKind != TypeKind.Struct
                || actionType.DeclaringSyntaxReferences.Length != 1
                || method.ContainingType.TypeArguments.Length != 1)
            {
                return false;
            }

            ITypeSymbol resolvedStateType = method.TypeArguments[0];
            ITypeSymbol elementType =
                method.ContainingType.TypeArguments[0];
            INamedTypeSymbol? contract = actionType.AllInterfaces
                .SingleOrDefault(candidate =>
                    Is(candidate, CompileTimeWriteAction)
                    && candidate.TypeArguments.Length == 2
                    && SymbolEqualityComparer.Default.Equals(
                        candidate.TypeArguments[0],
                        elementType)
                    && SymbolEqualityComparer.Default.Equals(
                        candidate.TypeArguments[1],
                        resolvedStateType));
            IMethodSymbol? contractMethod = contract?
                .GetMembers("Invoke")
                .OfType<IMethodSymbol>()
                .SingleOrDefault();
            IMethodSymbol? resolvedAction = contractMethod is null
                ? null
                : actionType.FindImplementationForInterfaceMember(
                    contractMethod) as IMethodSymbol;
            if (resolvedAction is null
                || !resolvedAction.IsStatic
                || !resolvedAction.ReturnsVoid
                || resolvedAction.Parameters.Length != 2
                || resolvedAction.Parameters[0].RefKind != RefKind.None
                || resolvedAction.Parameters[0].ScopedKind == ScopedKind.None
                || !IsWriter(resolvedAction.Parameters[0].Type)
                || resolvedAction.Parameters[1].RefKind != RefKind.In
                || resolvedAction.Parameters[1].ScopedKind == ScopedKind.None
                || !SymbolEqualityComparer.Default.Equals(
                    resolvedAction.Parameters[1].Type,
                    resolvedStateType))
            {
                return false;
            }

            stateType = resolvedStateType;
            action = resolvedAction;
            return true;
        }

        internal bool IsBuilderBorrow(IMethodSymbol method) =>
            method.Name == "Borrow"
            && Is(method.ContainingType, Builder)
            && method.Parameters.Any(parameter =>
                GetBorrowActionArity(parameter.Type) != 0);

        internal bool IsBuilderStateBorrow(IMethodSymbol method) =>
            method.Name == "Borrow"
            && Is(method.ContainingType, Builder)
            && method.Parameters.Any(parameter =>
                IsBorrowStateAction(parameter.Type));

        internal bool IsWriter(ITypeSymbol? type) =>
            Is(type, Writer);

        internal bool IsWriteAction(ITypeSymbol? type) =>
            Is(type, WriteAction);

        internal bool IsWriteStateAction(ITypeSymbol? type) =>
            Is(type, WriteStateAction);

        internal int GetWriteActionArity(ITypeSymbol? type) =>
            IsWriteAction(type) ? 1 : 0;

        internal bool IsBorrow(ITypeSymbol? type) =>
            Is(type, Borrow);

        internal bool IsBorrowAction(ITypeSymbol? type) =>
            Is(type, BorrowAction);

        internal bool IsBorrowStateAction(ITypeSymbol? type) =>
            Is(type, BorrowStateAction);

        internal int GetBorrowActionArity(ITypeSymbol? type)
        {
            if (Is(type, BorrowAction))
            {
                return 1;
            }

            return Is(type, PairBorrowAction) ? 2 : 0;
        }

        internal bool IsBuilder(ITypeSymbol? type) =>
            Is(type, Builder);

        internal bool IsOwnerBearingState(ITypeSymbol type) =>
            IsOwnerBearingState(
                type,
                new HashSet<ITypeSymbol>(
                    SymbolEqualityComparer.Default));

        internal bool IsMemoryMarshalWriteDestination(
            IArgumentOperation argument)
        {
            if (argument.Parameter?.Ordinal != 0
                || argument.Parent is not IInvocationOperation invocation)
            {
                return false;
            }

            IMethodSymbol method = invocation.TargetMethod;
            return MemoryMarshal is not null
                && Span is not null
                && method.IsStatic
                && method.ReturnsVoid
                && method.Name == "Write"
                && method.Arity == 1
                && method.Parameters.Length == 2
                && Is(method.ContainingType, MemoryMarshal)
                && method.Parameters[0].RefKind == RefKind.None
                && method.Parameters[0].Type is INamedTypeSymbol destination
                && Is(destination, Span)
                && destination.TypeArguments.Length == 1
                && destination.TypeArguments[0].SpecialType
                    == SpecialType.System_Byte
                && method.Parameters[1].RefKind == RefKind.In
                && SymbolEqualityComparer.Default.Equals(
                    method.Parameters[1].Type,
                    method.TypeArguments[0]);
        }

        internal bool IsScopedInForward(
            IParameterSymbol? parameter,
            ITypeSymbol stateType)
        {
            if (parameter is null
                || parameter.RefKind != RefKind.In
                || parameter.ScopedKind == ScopedKind.None
                || !SymbolEqualityComparer.Default.Equals(
                    parameter.Type,
                    stateType)
                || parameter.ContainingSymbol
                    is not IMethodSymbol method
                || method.DeclaringSyntaxReferences.Length != 1)
            {
                return false;
            }

            IParameterSymbol declaration =
                method.OriginalDefinition.Parameters[
                    parameter.Ordinal];
            return declaration.RefKind == RefKind.In
                && declaration.ScopedKind != ScopedKind.None;
        }

        internal bool IsStateBoundaryForward(
            IArgumentOperation argument,
            ITypeSymbol stateType) =>
            argument.Parent is IInvocationOperation invocation
            && argument.Parameter?.RefKind == RefKind.In
            && SymbolEqualityComparer.Default.Equals(
                argument.Parameter.Type,
                stateType)
            && (IsBuilderStateWrite(invocation.TargetMethod)
                || IsBuilderStateBorrow(invocation.TargetMethod)
                || IsBuilderCompileTimeStateWrite(
                    invocation.TargetMethod));

        internal bool IsScopedRefForward(
            IParameterSymbol? parameter,
            Func<ITypeSymbol?, bool> isAuthority)
        {
            if (parameter is null
                || parameter.RefKind != RefKind.Ref
                || parameter.ScopedKind == ScopedKind.None
                || !isAuthority(parameter.Type)
                || parameter.ContainingSymbol
                    is not IMethodSymbol method
                || method.DeclaringSyntaxReferences.Length != 1)
            {
                return false;
            }

            IParameterSymbol declaration =
                method.OriginalDefinition.Parameters[
                    parameter.Ordinal];
            return declaration.RefKind == RefKind.Ref
                && declaration.ScopedKind != ScopedKind.None
                && isAuthority(declaration.Type);
        }

        internal bool IsScopedAdapterForward(
            IParameterSymbol? parameter,
            ITypeSymbol adapterType)
        {
            if (parameter is null
                || parameter.RefKind == RefKind.Out
                || parameter.ScopedKind == ScopedKind.None
                || !SymbolEqualityComparer.Default.Equals(
                    parameter.Type,
                    adapterType)
                || parameter.ContainingSymbol
                    is not IMethodSymbol method
                || method.IsAsync
                || method.DeclaringSyntaxReferences.Length != 1)
            {
                return false;
            }

            IParameterSymbol declaration =
                method.OriginalDefinition.Parameters[
                    parameter.Ordinal];
            return declaration.RefKind != RefKind.Out
                && declaration.ScopedKind != ScopedKind.None
                && SymbolEqualityComparer.Default.Equals(
                    declaration.Type,
                    adapterType);
        }

        internal static bool IsViewLike(ITypeSymbol? type)
        {
            if (type?.TypeKind == TypeKind.Pointer)
            {
                return true;
            }

            if (type is not INamedTypeSymbol named)
            {
                return false;
            }

            string name = named.OriginalDefinition.ToDisplayString();
            return name is "System.Span<T>"
                or "System.ReadOnlySpan<T>";
        }

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

    private sealed class WriterUsageWalker : OperationWalker
    {
        private readonly Symbols _symbols;
        private readonly IParameterSymbol[] _writers;
        private readonly Action<Diagnostic> _report;
        private readonly HashSet<ILocalSymbol> _views =
            new(SymbolEqualityComparer.Default);
        private readonly HashSet<ILocalSymbol> _adapters =
            new(SymbolEqualityComparer.Default);
        private readonly HashSet<TextSpan> _adapterArguments = [];
        private readonly HashSet<DiagnosticKey> _reported = [];

        internal WriterUsageWalker(
            Symbols symbols,
            IParameterSymbol[] writers,
            Action<Diagnostic> report)
        {
            _symbols = symbols;
            _writers = writers;
            _report = report;
        }

        internal void ReportExpressionReturn(IOperation operation)
        {
            if (IsViewDerived(operation))
            {
                Report(
                    ViewEscape,
                    operation.Syntax,
                    ViewName(operation),
                    "the helper return");
            }
        }

        public override void VisitVariableDeclarator(
            IVariableDeclaratorOperation operation)
        {
            IOperation? value = operation.Initializer?.Value;
            if (TryRegisterAdapter(operation, value))
            {
                _adapters.Add(operation.Symbol);
            }
            else if (ContainsAdapter(value))
            {
                Report(
                    ViewEscape,
                    operation.Syntax,
                    AdapterName(value),
                    "a local adapter alias");
            }
            else if (IsViewDerived(value))
            {
                _views.Add(operation.Symbol);
            }

            base.VisitVariableDeclarator(operation);
        }

        public override void VisitSimpleAssignment(
            ISimpleAssignmentOperation operation)
        {
            if (ContainsAdapter(operation.Value))
            {
                Report(
                    ViewEscape,
                    operation.Syntax,
                    AdapterName(operation.Value),
                    "an adapter assignment");
            }
            else if (IsViewDerived(operation.Value))
            {
                if (operation.Target is ILocalReferenceOperation local)
                {
                    _views.Add(local.Local);
                }
                else
                {
                    Report(
                        ViewEscape,
                        operation.Syntax,
                        ViewName(operation.Value),
                        "a nonlocal assignment");
                }
            }

            base.VisitSimpleAssignment(operation);
        }

        public override void VisitReturn(IReturnOperation operation)
        {
            if (ContainsAdapter(operation.ReturnedValue))
            {
                Report(
                    ViewEscape,
                    operation.Syntax,
                    AdapterName(operation.ReturnedValue),
                    "the callback return");
            }
            else if (IsViewDerived(operation.ReturnedValue))
            {
                Report(
                    ViewEscape,
                    operation.Syntax,
                    ViewName(operation.ReturnedValue),
                    "the callback return");
            }

            base.VisitReturn(operation);
        }

        public override void VisitArgument(IArgumentOperation operation)
        {
            if (IsViewDerived(operation.Value)
                && !_adapterArguments.Contains(operation.Syntax.Span)
                && operation.Parameter?.ScopedKind
                    == ScopedKind.None)
            {
                string destination = operation.Parent
                    is IInvocationOperation invocation
                    ? invocation.TargetMethod.ToDisplayString(
                        SymbolDisplayFormat.MinimallyQualifiedFormat)
                    : "an unscoped call";
                Report(
                    ViewEscape,
                    operation.Syntax,
                    ViewName(operation.Value),
                    destination);
            }
            else if (ContainsAdapter(operation.Value)
                && !IsScopedAdapterForward(operation))
            {
                Report(
                    ViewEscape,
                    operation.Syntax,
                    AdapterName(operation.Value),
                    "an unscoped adapter call");
            }

            base.VisitArgument(operation);
        }

        public override void VisitLocalReference(
            ILocalReferenceOperation operation)
        {
            if (_adapters.Contains(operation.Local)
                && !IsPermittedAdapterUse(operation))
            {
                Report(
                    ViewEscape,
                    operation.Syntax,
                    operation.Local.Name,
                    "an adapter escape");
            }

            base.VisitLocalReference(operation);
        }

        public override void VisitParameterReference(
            IParameterReferenceOperation operation)
        {
            if (IsWriter(operation.Parameter)
                && !IsPermittedDirectUse(operation))
            {
                Report(
                    InvalidAuthority,
                    operation.Syntax,
                    operation.Parameter.Name,
                    "an alias or helper");
            }

            base.VisitParameterReference(operation);
        }

        public override void VisitAnonymousFunction(
            IAnonymousFunctionOperation operation)
        {
            IOperation? captured = operation.Body
                .DescendantsAndSelf()
                .FirstOrDefault(IsTrackedReference);
            if (captured is not null)
            {
                DiagnosticDescriptor descriptor =
                    IsViewDerived(captured)
                    ? ViewEscape
                    : InvalidAuthority;
                Report(
                    descriptor,
                    operation.Syntax,
                    ViewName(captured),
                    "a nested callback");
                return;
            }

            base.VisitAnonymousFunction(operation);
        }

        public override void VisitLocalFunction(
            ILocalFunctionOperation operation)
        {
            IOperation? captured = operation.Body
                .DescendantsAndSelf()
                .FirstOrDefault(IsTrackedReference);
            if (captured is not null)
            {
                DiagnosticDescriptor descriptor =
                    IsViewDerived(captured)
                    ? ViewEscape
                    : InvalidAuthority;
                Report(
                    descriptor,
                    operation.Syntax,
                    ViewName(captured),
                    "a nested local function");
                return;
            }

            base.VisitLocalFunction(operation);
        }

        private bool IsPermittedDirectUse(
            IParameterReferenceOperation reference)
        {
            IOperation? parent = reference.Parent;
            while (parent is IConversionOperation conversion
                && conversion.IsImplicit)
            {
                parent = parent.Parent;
            }

            if (parent is IInvocationOperation invocation
                && ReferenceEquals(invocation.Instance, reference)
                && _symbols.IsWriter(
                    invocation.TargetMethod.ContainingType))
            {
                return invocation.TargetMethod.Name
                    is "AsSpan" or "Commit";
            }

            if (parent is IArgumentOperation argument)
            {
                return _symbols.IsScopedRefForward(
                    argument.Parameter,
                    _symbols.IsWriter);
            }

            return parent is IPropertyReferenceOperation property
                && ReferenceEquals(property.Instance, reference)
                && property.Property.Name == "Length"
                && _symbols.IsWriter(
                    property.Property.ContainingType);
        }

        private bool IsTrackedReference(IOperation operation) =>
            operation is IParameterReferenceOperation parameter
                && IsWriter(parameter.Parameter)
            || operation is ILocalReferenceOperation local
                && (_views.Contains(local.Local)
                    || _adapters.Contains(local.Local));

        private bool IsViewDerived(IOperation? operation)
        {
            if (operation is null
                || !Symbols.IsViewLike(operation.Type))
            {
                return false;
            }

            return operation.DescendantsAndSelf().Any(item =>
                item is IParameterReferenceOperation parameter
                    && IsWriter(parameter.Parameter)
                || item is ILocalReferenceOperation local
                    && (_views.Contains(local.Local)
                        || _adapters.Contains(local.Local)));
        }

        private bool TryRegisterAdapter(
            IVariableDeclaratorOperation declaration,
            IOperation? value)
        {
            IOperation? current = value;
            while (current is IConversionOperation conversion
                && conversion.IsImplicit)
            {
                current = conversion.Operand;
            }

            if (current is not IObjectCreationOperation creation
                || creation.Type is not INamedTypeSymbol adapterType
                || !adapterType.IsRefLikeType
                || adapterType.DeclaringSyntaxReferences.Length != 1
                || creation.Constructor is not { } constructor
                || constructor.DeclaringSyntaxReferences.Length != 1
                || !SymbolEqualityComparer.Default.Equals(
                    declaration.Symbol.Type,
                    adapterType))
            {
                return false;
            }

            IArgumentOperation[] viewArguments = creation.Arguments
                .Where(argument => IsViewDerived(argument.Value))
                .ToArray();
            if (viewArguments.Length != 1
                || viewArguments[0].Parameter is not { } viewParameter
                || !ValidateAdapterConstructor(
                    adapterType,
                    constructor,
                    viewParameter))
            {
                return false;
            }

            _adapterArguments.Add(viewArguments[0].Syntax.Span);
            return true;
        }

        private bool ValidateAdapterConstructor(
            INamedTypeSymbol adapterType,
            IMethodSymbol constructor,
            IParameterSymbol viewParameter)
        {
            IFieldSymbol[] viewFields = adapterType.GetMembers()
                .OfType<IFieldSymbol>()
                .Where(field =>
                    !field.IsStatic
                    && Symbols.IsViewLike(field.Type))
                .ToArray();
            if (viewFields.Length != 1
                || !SymbolEqualityComparer.Default.Equals(
                    viewFields[0].Type,
                    viewParameter.Type))
            {
                return false;
            }

            SyntaxNode syntax = constructor
                .DeclaringSyntaxReferences[0]
                .GetSyntax();
            SyntaxNode? body = syntax is ConstructorDeclarationSyntax declaration
                ? (SyntaxNode?)declaration.Body
                    ?? declaration.ExpressionBody?.Expression
                : null;
            if (body is null)
            {
                return false;
            }

            SemanticModel model = _symbols.Compilation.GetSemanticModel(
                body.SyntaxTree);
            if (model.GetOperation(body) is not { } operation)
            {
                return false;
            }

            AdapterConstructorWalker walker = new(
                adapterType,
                viewParameter,
                viewFields[0]);
            walker.Visit(operation);
            return walker.IsValid
                && ValidateAdapterMembers(
                    adapterType,
                    viewFields[0]);
        }

        private bool ValidateAdapterMembers(
            INamedTypeSymbol adapterType,
            IFieldSymbol viewField)
        {
            foreach (IMethodSymbol method in adapterType.GetMembers()
                .OfType<IMethodSymbol>()
                .Where(method =>
                    !method.IsStatic
                    && !method.IsImplicitlyDeclared
                    && method.MethodKind
                        is not MethodKind.Constructor
                        and not MethodKind.StaticConstructor))
            {
                if (method.IsAsync
                    || method.ReturnsByRef
                    || method.ReturnsByRefReadonly
                    || Symbols.IsViewLike(method.ReturnType)
                    || SymbolEqualityComparer.Default.Equals(
                        method.ReturnType,
                        adapterType)
                    || method.DeclaringSyntaxReferences.Length != 1)
                {
                    return false;
                }

                SyntaxNode syntax = method.DeclaringSyntaxReferences[0]
                    .GetSyntax();
                if (HasDirectYield(syntax)
                    || syntax.DescendantNodes().Any(node =>
                        node is PointerTypeSyntax
                            or FixedStatementSyntax
                            or UnsafeStatementSyntax))
                {
                    return false;
                }

                SyntaxNode? body = syntax switch
                {
                    BaseMethodDeclarationSyntax declaration =>
                        (SyntaxNode?)declaration.Body
                            ?? declaration.ExpressionBody?.Expression,
                    AccessorDeclarationSyntax accessor =>
                        (SyntaxNode?)accessor.Body
                            ?? accessor.ExpressionBody?.Expression,
                    _ => null
                };
                if (body is null)
                {
                    return false;
                }

                SemanticModel model = _symbols.Compilation.GetSemanticModel(
                    body.SyntaxTree);
                if (model.GetOperation(body) is not { } operation)
                {
                    return false;
                }

                AdapterMemberWalker walker = new(
                    _symbols,
                    viewField);
                walker.Visit(operation);
                if (!walker.IsValid)
                {
                    return false;
                }
            }

            return true;
        }

        private bool IsScopedAdapterForward(
            IArgumentOperation argument)
        {
            ILocalReferenceOperation? adapter = argument.Value
                .DescendantsAndSelf()
                .OfType<ILocalReferenceOperation>()
                .FirstOrDefault(reference =>
                    _adapters.Contains(reference.Local));
            return adapter is not null
                && _symbols.IsScopedAdapterForward(
                    argument.Parameter,
                    adapter.Local.Type);
        }

        private bool IsPermittedAdapterUse(
            ILocalReferenceOperation reference)
        {
            IOperation? parent = reference.Parent;
            while (parent is IConversionOperation conversion
                && conversion.IsImplicit)
            {
                parent = parent.Parent;
            }

            if (parent is IArgumentOperation argument)
            {
                return IsScopedAdapterForward(argument);
            }

            if (parent is IFieldReferenceOperation field
                && ReferenceEquals(field.Instance, reference)
                || parent is IPropertyReferenceOperation property
                && ReferenceEquals(property.Instance, reference))
            {
                return true;
            }

            return parent is IInvocationOperation invocation
                && ReferenceEquals(invocation.Instance, reference)
                && invocation.TargetMethod.DeclaringSyntaxReferences.Length == 1
                && SymbolEqualityComparer.Default.Equals(
                    invocation.TargetMethod.ContainingType,
                    reference.Local.Type);
        }

        private bool ContainsAdapter(IOperation? operation) =>
            operation?.DescendantsAndSelf().Any(item =>
                item is ILocalReferenceOperation local
                && _adapters.Contains(local.Local)) == true;

        private string AdapterName(IOperation? operation) =>
            operation?.DescendantsAndSelf()
                .OfType<ILocalReferenceOperation>()
                .FirstOrDefault(reference =>
                    _adapters.Contains(reference.Local))?
                .Local.Name
            ?? "adapter";

        private bool IsWriter(IParameterSymbol parameter) =>
            _writers.Any(writer =>
                SymbolEqualityComparer.Default.Equals(
                    writer,
                    parameter));

        private string ViewName(IOperation? operation)
        {
            ILocalReferenceOperation? local = operation?
                .DescendantsAndSelf()
                .OfType<ILocalReferenceOperation>()
                .FirstOrDefault(reference =>
                    _views.Contains(reference.Local));
            if (local is not null)
            {
                return local.Local.Name;
            }

            IParameterReferenceOperation? parameter = operation?
                .DescendantsAndSelf()
                .OfType<IParameterReferenceOperation>()
                .FirstOrDefault(reference =>
                    IsWriter(reference.Parameter));
            return parameter?.Parameter.Name ?? "view";
        }

        private void Report(
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
            if (_reported.Add(key))
            {
                _report(Diagnostic.Create(
                    descriptor,
                    syntax.GetLocation(),
                    name,
                    destination));
            }
        }
    }

    private sealed class AdapterConstructorWalker : OperationWalker
    {
        private readonly INamedTypeSymbol _adapterType;
        private readonly IParameterSymbol _viewParameter;
        private readonly IFieldSymbol _viewField;
        private int _validAssignments;

        internal AdapterConstructorWalker(
            INamedTypeSymbol adapterType,
            IParameterSymbol viewParameter,
            IFieldSymbol viewField)
        {
            _adapterType = adapterType;
            _viewParameter = viewParameter;
            _viewField = viewField;
        }

        internal bool IsValid => _validAssignments == 1;

        public override void VisitParameterReference(
            IParameterReferenceOperation operation)
        {
            if (!SymbolEqualityComparer.Default.Equals(
                    operation.Parameter,
                    _viewParameter))
            {
                base.VisitParameterReference(operation);
                return;
            }

            IOperation? parent = operation.Parent;
            while (parent is IConversionOperation conversion
                && conversion.IsImplicit)
            {
                parent = parent.Parent;
            }

            if (parent is ISimpleAssignmentOperation assignment
                && ReferenceEquals(
                    UnwrapValue(assignment.Value),
                    operation)
                && assignment.Target is IFieldReferenceOperation field
                && SymbolEqualityComparer.Default.Equals(
                    field.Field,
                    _viewField)
                && field.Instance is IInstanceReferenceOperation instance
                && SymbolEqualityComparer.Default.Equals(
                    instance.Type,
                    _adapterType))
            {
                _validAssignments++;
                return;
            }

            _validAssignments = int.MinValue;
        }

        private static IOperation UnwrapValue(IOperation value)
        {
            IOperation current = value;
            while (current is IConversionOperation conversion
                && conversion.IsImplicit)
            {
                current = conversion.Operand;
            }

            return current;
        }
    }

    private sealed class AdapterMemberWalker : OperationWalker
    {
        private readonly Symbols _symbols;
        private readonly IFieldSymbol _viewField;
        private readonly HashSet<ILocalSymbol> _views =
            new(SymbolEqualityComparer.Default);

        internal AdapterMemberWalker(
            Symbols symbols,
            IFieldSymbol viewField)
        {
            _symbols = symbols;
            _viewField = viewField;
        }

        internal bool IsValid { get; private set; } = true;

        public override void VisitVariableDeclarator(
            IVariableDeclaratorOperation operation)
        {
            if (IsDerived(operation.Initializer?.Value))
            {
                _views.Add(operation.Symbol);
            }

            base.VisitVariableDeclarator(operation);
        }

        public override void VisitSimpleAssignment(
            ISimpleAssignmentOperation operation)
        {
            if (IsDerived(operation.Value))
            {
                if (operation.Target is ILocalReferenceOperation local)
                {
                    _views.Add(local.Local);
                }
                else
                {
                    IsValid = false;
                }
            }

            base.VisitSimpleAssignment(operation);
        }

        public override void VisitReturn(IReturnOperation operation)
        {
            if (IsDerived(operation.ReturnedValue))
            {
                IsValid = false;
            }

            base.VisitReturn(operation);
        }

        public override void VisitArgument(IArgumentOperation operation)
        {
            if (IsDerived(operation.Value)
                && operation.Parameter?.ScopedKind == ScopedKind.None
                && !_symbols.IsMemoryMarshalWriteDestination(operation))
            {
                IsValid = false;
            }

            base.VisitArgument(operation);
        }

        public override void VisitConversion(
            IConversionOperation operation)
        {
            if (IsDerived(operation.Operand)
                && !Symbols.IsViewLike(operation.Type))
            {
                IsValid = false;
            }

            base.VisitConversion(operation);
        }

        public override void VisitAnonymousFunction(
            IAnonymousFunctionOperation operation)
        {
            if (ContainsTrackedReference(operation.Body))
            {
                IsValid = false;
                return;
            }

            base.VisitAnonymousFunction(operation);
        }

        public override void VisitLocalFunction(
            ILocalFunctionOperation operation)
        {
            if (operation.Body is { } body
                && ContainsTrackedReference(body))
            {
                IsValid = false;
                return;
            }

            base.VisitLocalFunction(operation);
        }

        private bool IsDerived(IOperation? operation)
        {
            if (operation is null
                || !Symbols.IsViewLike(operation.Type))
            {
                return false;
            }

            return ContainsTrackedReference(operation);
        }

        private bool ContainsTrackedReference(IOperation operation) =>
            operation.DescendantsAndSelf().Any(item =>
                item is IFieldReferenceOperation field
                    && SymbolEqualityComparer.Default.Equals(
                        field.Field,
                        _viewField)
                || item is ILocalReferenceOperation local
                    && _views.Contains(local.Local));
    }

    private sealed class StateUsageWalker : OperationWalker
    {
        private readonly Symbols _symbols;
        private readonly IParameterSymbol[] _states;
        private readonly Action<Diagnostic> _report;
        private readonly HashSet<DiagnosticKey> _reported = [];

        internal StateUsageWalker(
            Symbols symbols,
            IParameterSymbol[] states,
            Action<Diagnostic> report)
        {
            _symbols = symbols;
            _states = states;
            _report = report;
        }

        internal void ReportExpressionReturn(IOperation operation)
        {
            if (ContainsState(operation))
            {
                Report(
                    StateEscape,
                    operation.Syntax,
                    StateName(operation),
                    "the helper return");
            }
        }

        public override void VisitParameterReference(
            IParameterReferenceOperation operation)
        {
            if (IsState(operation.Parameter)
                && !IsPermittedUse(operation))
            {
                Report(
                    InvalidStateAuthority,
                    operation.Syntax,
                    operation.Parameter.Name,
                    "an alias or unscoped operation");
            }

            base.VisitParameterReference(operation);
        }

        public override void VisitVariableDeclarator(
            IVariableDeclaratorOperation operation)
        {
            if (IsDirectStateValue(operation.Initializer?.Value))
            {
                Report(
                    StateEscape,
                    operation.Syntax,
                    StateName(operation.Initializer?.Value),
                    "a local alias");
            }

            base.VisitVariableDeclarator(operation);
        }

        public override void VisitSimpleAssignment(
            ISimpleAssignmentOperation operation)
        {
            if (IsDirectStateValue(operation.Value))
            {
                Report(
                    StateEscape,
                    operation.Syntax,
                    StateName(operation.Value),
                    "an assignment");
            }

            base.VisitSimpleAssignment(operation);
        }

        public override void VisitReturn(IReturnOperation operation)
        {
            if (ContainsState(operation.ReturnedValue))
            {
                Report(
                    StateEscape,
                    operation.Syntax,
                    StateName(operation.ReturnedValue),
                    "the callback return");
            }

            base.VisitReturn(operation);
        }

        public override void VisitArgument(IArgumentOperation operation)
        {
            if (IsDirectStateValue(operation.Value))
            {
                IParameterReferenceOperation reference = operation.Value
                    .DescendantsAndSelf()
                    .OfType<IParameterReferenceOperation>()
                    .First(item => IsState(item.Parameter));
                if (!_symbols.IsScopedInForward(
                        operation.Parameter,
                        reference.Parameter.Type)
                    && !_symbols.IsStateBoundaryForward(
                        operation,
                        reference.Parameter.Type))
                {
                    Report(
                        StateEscape,
                        operation.Syntax,
                        reference.Parameter.Name,
                        "an unscoped call");
                }
            }
            else if (ContainsState(operation.Value)
                && operation.Value.Type?.IsRefLikeType == true
                && operation.Parameter?.ScopedKind
                    == ScopedKind.None)
            {
                Report(
                    StateEscape,
                    operation.Syntax,
                    StateName(operation.Value),
                    "an unscoped derived view");
            }

            base.VisitArgument(operation);
        }

        public override void VisitAnonymousFunction(
            IAnonymousFunctionOperation operation)
        {
            IParameterReferenceOperation? captured = operation.Body
                .DescendantsAndSelf()
                .OfType<IParameterReferenceOperation>()
                .FirstOrDefault(reference =>
                    IsState(reference.Parameter));
            if (captured is not null)
            {
                Report(
                    StateEscape,
                    operation.Syntax,
                    captured.Parameter.Name,
                    "a nested callback");
                return;
            }

            base.VisitAnonymousFunction(operation);
        }

        public override void VisitLocalFunction(
            ILocalFunctionOperation operation)
        {
            IParameterReferenceOperation? captured = operation.Body
                .DescendantsAndSelf()
                .OfType<IParameterReferenceOperation>()
                .FirstOrDefault(reference =>
                    IsState(reference.Parameter));
            if (captured is not null)
            {
                Report(
                    StateEscape,
                    operation.Syntax,
                    captured.Parameter.Name,
                    "a nested local function");
                return;
            }

            base.VisitLocalFunction(operation);
        }

        private bool IsPermittedUse(
            IParameterReferenceOperation reference)
        {
            IOperation? parent = reference.Parent;
            while (parent is IConversionOperation conversion
                && conversion.IsImplicit)
            {
                parent = parent.Parent;
            }

            if (parent is IFieldReferenceOperation field
                && ReferenceEquals(field.Instance, reference)
                || parent is IPropertyReferenceOperation property
                && ReferenceEquals(property.Instance, reference)
                || parent is IArrayElementReferenceOperation array
                && ReferenceEquals(array.ArrayReference, reference))
            {
                return true;
            }

            return parent is IArgumentOperation argument
                && (_symbols.IsScopedInForward(
                        argument.Parameter,
                        reference.Parameter.Type)
                    || _symbols.IsStateBoundaryForward(
                        argument,
                        reference.Parameter.Type));
        }

        private bool IsDirectStateValue(IOperation? operation)
        {
            IOperation? current = operation;
            while (current is IConversionOperation conversion
                && conversion.OperatorMethod is null
                && conversion.Conversion.IsIdentity
                || current is IParenthesizedOperation)
            {
                current = current switch
                {
                    IConversionOperation item => item.Operand,
                    IParenthesizedOperation item => item.Operand,
                    _ => current
                };
            }

            return current is IParameterReferenceOperation reference
                && IsState(reference.Parameter);
        }

        private bool ContainsState(IOperation? operation) =>
            operation?.DescendantsAndSelf()
                .OfType<IParameterReferenceOperation>()
                .Any(reference => IsState(reference.Parameter))
            == true;

        private bool IsState(IParameterSymbol parameter) =>
            _states.Any(state =>
                SymbolEqualityComparer.Default.Equals(
                    state,
                    parameter));

        private string StateName(IOperation? operation) =>
            operation?.DescendantsAndSelf()
                .OfType<IParameterReferenceOperation>()
                .FirstOrDefault(reference =>
                    IsState(reference.Parameter))
                ?.Parameter.Name
            ?? "state";

        private void Report(
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
            if (_reported.Add(key))
            {
                _report(Diagnostic.Create(
                    descriptor,
                    syntax.GetLocation(),
                    name,
                    destination));
            }
        }
    }

    private sealed class BorrowUsageWalker : OperationWalker
    {
        private readonly Symbols _symbols;
        private readonly IParameterSymbol[] _borrows;
        private readonly Action<Diagnostic> _report;
        private readonly HashSet<DiagnosticKey> _reported = [];

        internal BorrowUsageWalker(
            Symbols symbols,
            IParameterSymbol[] borrows,
            Action<Diagnostic> report)
        {
            _symbols = symbols;
            _borrows = borrows;
            _report = report;
        }

        internal void ReportExpressionReturn(IOperation operation)
        {
            if (ContainsBorrow(operation))
            {
                Report(
                    BorrowEscape,
                    operation.Syntax,
                    BorrowName(operation),
                    "the helper return");
            }
        }

        public override void VisitParameterReference(
            IParameterReferenceOperation operation)
        {
            if (IsBorrow(operation.Parameter)
                && !IsPermittedUse(operation))
            {
                Report(
                    InvalidBorrowAuthority,
                    operation.Syntax,
                    operation.Parameter.Name,
                    "an alias or unscoped helper");
            }

            base.VisitParameterReference(operation);
        }

        public override void VisitReturn(IReturnOperation operation)
        {
            if (ContainsBorrow(operation.ReturnedValue))
            {
                Report(
                    BorrowEscape,
                    operation.Syntax,
                    BorrowName(operation.ReturnedValue),
                    "the callback return");
            }

            base.VisitReturn(operation);
        }

        public override void VisitSimpleAssignment(
            ISimpleAssignmentOperation operation)
        {
            if (ContainsBorrow(operation.Value))
            {
                Report(
                    BorrowEscape,
                    operation.Syntax,
                    BorrowName(operation.Value),
                    "an assignment");
            }

            base.VisitSimpleAssignment(operation);
        }

        public override void VisitInvocation(
            IInvocationOperation operation)
        {
            if (_symbols.IsBuilder(operation.Instance?.Type))
            {
                Report(
                    InvalidBorrowAuthority,
                    operation.Syntax,
                    _borrows[0].Name,
                    "owner use during an active borrow");
            }

            base.VisitInvocation(operation);
        }

        public override void VisitPropertyReference(
            IPropertyReferenceOperation operation)
        {
            if (_symbols.IsBuilder(operation.Instance?.Type))
            {
                Report(
                    InvalidBorrowAuthority,
                    operation.Syntax,
                    _borrows[0].Name,
                    "owner use during an active borrow");
            }

            base.VisitPropertyReference(operation);
        }

        public override void VisitAnonymousFunction(
            IAnonymousFunctionOperation operation)
        {
            IParameterReferenceOperation? captured = operation.Body
                .DescendantsAndSelf()
                .OfType<IParameterReferenceOperation>()
                .FirstOrDefault(reference =>
                    IsBorrow(reference.Parameter));
            if (captured is not null)
            {
                Report(
                    BorrowEscape,
                    operation.Syntax,
                    captured.Parameter.Name,
                    "a nested callback");
                return;
            }

            base.VisitAnonymousFunction(operation);
        }

        public override void VisitLocalFunction(
            ILocalFunctionOperation operation)
        {
            IParameterReferenceOperation? captured = operation.Body
                .DescendantsAndSelf()
                .OfType<IParameterReferenceOperation>()
                .FirstOrDefault(reference =>
                    IsBorrow(reference.Parameter));
            if (captured is not null)
            {
                Report(
                    BorrowEscape,
                    operation.Syntax,
                    captured.Parameter.Name,
                    "a nested local function");
                return;
            }

            base.VisitLocalFunction(operation);
        }

        private bool IsPermittedUse(
            IParameterReferenceOperation reference)
        {
            IOperation? parent = reference.Parent;
            while (parent is IConversionOperation conversion
                && conversion.IsImplicit)
            {
                parent = parent.Parent;
            }

            if (parent is IInvocationOperation invocation
                && ReferenceEquals(invocation.Instance, reference)
                && _symbols.IsBorrow(
                    invocation.TargetMethod.ContainingType))
            {
                return true;
            }

            if (parent is IPropertyReferenceOperation property
                && ReferenceEquals(property.Instance, reference)
                && _symbols.IsBorrow(
                    property.Property.ContainingType))
            {
                return true;
            }

            return parent is IArgumentOperation argument
                && _symbols.IsScopedRefForward(
                    argument.Parameter,
                    _symbols.IsBorrow);
        }

        private bool ContainsBorrow(IOperation? operation) =>
            operation?.DescendantsAndSelf()
                .OfType<IParameterReferenceOperation>()
                .Any(reference => IsBorrow(reference.Parameter))
            == true;

        private bool IsBorrow(IParameterSymbol parameter) =>
            _borrows.Any(borrow =>
                SymbolEqualityComparer.Default.Equals(
                    borrow,
                    parameter));

        private string BorrowName(IOperation? operation) =>
            operation?.DescendantsAndSelf()
                .OfType<IParameterReferenceOperation>()
                .FirstOrDefault(reference =>
                    IsBorrow(reference.Parameter))
                ?.Parameter.Name
            ?? "borrow";

        private void Report(
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
            if (_reported.Add(key))
            {
                _report(Diagnostic.Create(
                    descriptor,
                    syntax.GetLocation(),
                    name,
                    destination));
            }
        }
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
