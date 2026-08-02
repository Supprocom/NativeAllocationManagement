using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
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
        "Builder writer '{0}' cannot transfer commit authority through '{1}'. Commit directly in the NativeBuilder.Write callback.");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(ViewEscape, InvalidAuthority);

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

            startContext.RegisterOperationBlockAction(blockContext =>
            {
                if (blockContext.OwningSymbol is not IMethodSymbol method
                    || method.MethodKind == MethodKind.AnonymousFunction)
                {
                    return;
                }

                IParameterSymbol[] writers = method.Parameters
                    .Where(parameter => symbols.IsWriter(parameter.Type))
                    .ToArray();
                if (writers.Length == 0)
                {
                    return;
                }

                WriterUsageWalker walker = new(
                    symbols,
                    writers,
                    blockContext.ReportDiagnostic);
                foreach (IOperation block in blockContext.OperationBlocks)
                {
                    walker.Visit(block);
                }
            });
            startContext.RegisterOperationAction(operationContext =>
            {
                if (operationContext.Operation
                    is not IAnonymousFunctionOperation callback)
                {
                    return;
                }

                IParameterSymbol[] writers = callback.Symbol.Parameters
                    .Where(parameter => symbols.IsWriter(parameter.Type))
                    .ToArray();
                if (writers.Length == 0)
                {
                    return;
                }

                WriterUsageWalker walker = new(
                    symbols,
                    writers,
                    operationContext.ReportDiagnostic);
                walker.Visit(callback.Body);
            }, OperationKind.AnonymousFunction);
            startContext.RegisterOperationAction(operationContext =>
            {
                if (operationContext.Operation
                    is not IInvocationOperation invocation
                    || !symbols.IsBuilderWrite(invocation.TargetMethod))
                {
                    return;
                }

                IArgumentOperation? callback = invocation.Arguments
                    .FirstOrDefault(argument =>
                        symbols.IsWriteAction(argument.Parameter?.Type));
                if (callback is null
                    || IsDirectCallback(callback.Value, symbols))
                {
                    return;
                }

                operationContext.ReportDiagnostic(Diagnostic.Create(
                    InvalidAuthority,
                    callback.Syntax.GetLocation(),
                    "writer",
                    "an indirect callback"));
            }, OperationKind.Invocation);
        });
    }

    private static bool IsDirectCallback(
        IOperation value,
        Symbols symbols)
    {
        IAnonymousFunctionOperation? anonymous = value
            .DescendantsAndSelf()
            .OfType<IAnonymousFunctionOperation>()
            .FirstOrDefault();
        if (anonymous is not null)
        {
            return anonymous.Symbol.Parameters.Length == 1
                && symbols.IsWriter(
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
            && declaration.Parameters[0].RefKind == RefKind.None
            && declaration.Parameters[0].ScopedKind
                != ScopedKind.None
            && symbols.IsWriter(
                declaration.Parameters[0].Type)
            && declaration.DeclaringSyntaxReferences.Length == 1;
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
        }

        internal INamedTypeSymbol? Builder { get; }

        internal INamedTypeSymbol? Writer { get; }

        internal INamedTypeSymbol? WriteAction { get; }

        internal bool IsAvailable =>
            Builder is not null
            && Writer is not null
            && WriteAction is not null;

        internal bool IsBuilderWrite(IMethodSymbol method) =>
            method.Name == "Write"
            && Is(method.ContainingType, Builder)
            && method.Parameters.Any(parameter =>
                IsWriteAction(parameter.Type));

        internal bool IsWriter(ITypeSymbol? type) =>
            Is(type, Writer);

        internal bool IsWriteAction(ITypeSymbol? type) =>
            Is(type, WriteAction);

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
