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

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(
            ViewEscape,
            InvalidAuthority,
            BorrowEscape,
            InvalidBorrowAuthority);

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
        InvocationExpressionSyntax? invocation = context.Node
            .Ancestors()
            .OfType<InvocationExpressionSyntax>()
            .FirstOrDefault();
        if (invocation is null
            || !IsPotentialBuilderInvocation(invocation)
            || context.SemanticModel.GetOperation(
                context.Node,
                context.CancellationToken)
                is not IAnonymousFunctionOperation anonymous
            || anonymous.Symbol.Parameters.Length != 1)
        {
            return;
        }

        IParameterSymbol parameter = anonymous.Symbol.Parameters[0];
        if (symbols.IsWriter(parameter.Type))
        {
            AnalyzeAuthorityBody(
                anonymous.Body,
                [parameter],
                [],
                symbols,
                context.ReportDiagnostic,
                expressionReturn: false);
            return;
        }

        if (!symbols.IsBorrow(parameter.Type))
        {
            return;
        }

        ReportInvalidBorrowParameters(
            [parameter],
            context.ReportDiagnostic);
        AnalyzeAuthorityBody(
            anonymous.Body,
            [],
            [parameter],
            symbols,
            context.ReportDiagnostic,
            expressionReturn: false);
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
        if (writers.Length == 0 && borrows.Length == 0)
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

        if (symbols.IsBuilderWrite(invocation.TargetMethod))
        {
            ValidateCallbackArgument(
                context.ReportDiagnostic,
                invocation,
                symbols.IsWriteAction,
                symbols.IsWriter,
                RefKind.None,
                InvalidAuthority,
                "writer");
            return;
        }

        if (symbols.IsBuilderBorrow(invocation.TargetMethod))
        {
            ValidateCallbackArgument(
                context.ReportDiagnostic,
                invocation,
                symbols.IsBorrowAction,
                symbols.IsBorrow,
                RefKind.Ref,
                InvalidBorrowAuthority,
                "borrow");
        }
    }

    private static void AnalyzeAuthorityBody(
        IOperation body,
        IParameterSymbol[] writers,
        IParameterSymbol[] borrows,
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
    }

    private static void ValidateCallbackArgument(
        Action<Diagnostic> report,
        IInvocationOperation invocation,
        Func<ITypeSymbol?, bool> isAction,
        Func<ITypeSymbol?, bool> isAuthority,
        RefKind refKind,
        DiagnosticDescriptor descriptor,
        string name)
    {
        IArgumentOperation? callback = invocation.Arguments
            .FirstOrDefault(argument =>
                isAction(argument.Parameter?.Type));
        if (callback is null
            || IsDirectCallback(
                callback.Value,
                isAuthority,
                refKind))
        {
            return;
        }

        report(Diagnostic.Create(
            descriptor,
            callback.Syntax.GetLocation(),
            name,
            "an indirect callback"));
    }

    private static bool IsDirectCallback(
        IOperation value,
        Func<ITypeSymbol?, bool> isAuthority,
        RefKind refKind)
    {
        IAnonymousFunctionOperation? anonymous = value
            .DescendantsAndSelf()
            .OfType<IAnonymousFunctionOperation>()
            .FirstOrDefault();
        if (anonymous is not null)
        {
            return anonymous.Symbol.Parameters.Length == 1
                && anonymous.Symbol.Parameters[0].RefKind == refKind
                && isAuthority(
                    anonymous.Symbol.Parameters[0].Type);
        }

        IMethodReferenceOperation? reference = value
            .DescendantsAndSelf()
            .OfType<IMethodReferenceOperation>()
            .FirstOrDefault();
        if (reference is null)
        {
            return false;
        }

        IMethodSymbol declaration = reference.Method.OriginalDefinition;
        return declaration.ReturnsVoid
            && !declaration.IsAsync
            && declaration.Parameters.Length == 1
            && declaration.Parameters[0].RefKind == refKind
            && declaration.Parameters[0].ScopedKind
                != ScopedKind.None
            && isAuthority(
                declaration.Parameters[0].Type)
            && declaration.DeclaringSyntaxReferences.Length == 1;
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
            Borrow = runtimeAssembly.GetTypeByMetadataName(
                Namespace + "NativeBuilderBorrow`1");
            BorrowAction = runtimeAssembly.GetTypeByMetadataName(
                Namespace + "NativeBuilderBorrowAction`1");
        }

        internal INamedTypeSymbol? Builder { get; }

        internal INamedTypeSymbol? Writer { get; }

        internal INamedTypeSymbol? WriteAction { get; }

        internal INamedTypeSymbol? Borrow { get; }

        internal INamedTypeSymbol? BorrowAction { get; }

        internal bool IsAvailable =>
            Builder is not null
            && Writer is not null
            && WriteAction is not null
            && Borrow is not null
            && BorrowAction is not null;

        internal bool IsBuilderWrite(IMethodSymbol method) =>
            method.Name == "Write"
            && (Is(method.ContainingType, Builder)
                || Is(method.ContainingType, Borrow))
            && method.Parameters.Any(parameter =>
                IsWriteAction(parameter.Type));

        internal bool IsBuilderBorrow(IMethodSymbol method) =>
            method.Name == "Borrow"
            && Is(method.ContainingType, Builder)
            && method.Parameters.Any(parameter =>
                IsBorrowAction(parameter.Type));

        internal bool IsWriter(ITypeSymbol? type) =>
            Is(type, Writer);

        internal bool IsWriteAction(ITypeSymbol? type) =>
            Is(type, WriteAction);

        internal bool IsBorrow(ITypeSymbol? type) =>
            Is(type, Borrow);

        internal bool IsBorrowAction(ITypeSymbol? type) =>
            Is(type, BorrowAction);

        internal bool IsBuilder(ITypeSymbol? type) =>
            Is(type, Builder);

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
            if (IsViewDerived(value))
            {
                _views.Add(operation.Symbol);
            }

            base.VisitVariableDeclarator(operation);
        }

        public override void VisitSimpleAssignment(
            ISimpleAssignmentOperation operation)
        {
            if (IsViewDerived(operation.Value))
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
            if (IsViewDerived(operation.ReturnedValue))
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

            base.VisitArgument(operation);
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
                && _views.Contains(local.Local);

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
                    && _views.Contains(local.Local));
        }

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
