using System.Collections.Immutable;
using System.Composition;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Formatting;
using Supprocom.NativeAllocationManagement.Analyzers;

namespace Supprocom.NativeAllocationManagement.CodeFixes;

/// <summary>Offers verified lexical cleanup without changing ownership, limits or runtime behavior.</summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(NativeOwnershipCodeFixProvider)), Shared]
public sealed class NativeOwnershipCodeFixProvider : CodeFixProvider
{
    private static readonly ImmutableArray<string> DiagnosticIds = ImmutableArray.Create("NAM1006", "NAM1025", "NAM1031", "NAM1038");
    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds => DiagnosticIds;
    /// <summary>Batch fixing is deliberately unsupported: each ownership transformation needs independent semantic proof.</summary>
    public override FixAllProvider? GetFixAllProvider() => null;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        CancellationToken cancellationToken = context.CancellationToken;
        SyntaxNode? root = await context.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        SemanticModel? model = await context.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || model is null) return;
        foreach (Diagnostic diagnostic in context.Diagnostics)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SyntaxNode node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            LocalDeclarationStatementSyntax? declaration = node.FirstAncestorOrSelf<LocalDeclarationStatementSyntax>();
            if (declaration is null || !CanAddUsing(declaration, model, cancellationToken)) continue;
            LocalDeclarationStatementSyntax replacement = declaration
                .WithUsingKeyword(SyntaxFactory.Token(SyntaxKind.UsingKeyword).WithTrailingTrivia(SyntaxFactory.Space))
                .WithAdditionalAnnotations(Formatter.Annotation);
            Document candidate = context.Document.WithSyntaxRoot(root.ReplaceNode(declaration, replacement));
            if (!await ProvesCorrectionAsync(context.Document, candidate, diagnostic.Id, cancellationToken).ConfigureAwait(false)) continue;
            context.RegisterCodeFix(CodeAction.Create("Use verified lexical cleanup", _ => Task.FromResult(candidate),
                equivalenceKey: "NAM.VerifiedLexicalCleanup"), diagnostic);
        }
    }

    private static bool CanAddUsing(LocalDeclarationStatementSyntax declaration, SemanticModel model, CancellationToken cancellationToken)
    {
        if (declaration.Parent is not BlockSyntax scope || declaration.Declaration.Variables.Count != 1
            || !declaration.UsingKeyword.IsKind(SyntaxKind.None) || !declaration.AwaitKeyword.IsKind(SyntaxKind.None)
            || declaration.Modifiers.Count != 0 || declaration.Declaration.Variables[0].Initializer is null) return false;
        VariableDeclaratorSyntax variable = declaration.Declaration.Variables[0];
        if (model.GetDeclaredSymbol(variable, cancellationToken) is not ILocalSymbol local || !IsRuntimeOwner(local.Type, model.Compilation)) return false;
        // Decline asynchronous/iterator scopes and ambiguous control transfers.
        if (scope.DescendantNodes().Any(static syntax => syntax is AwaitExpressionSyntax or YieldStatementSyntax or GotoStatementSyntax)) return false;
        foreach (IdentifierNameSyntax reference in scope.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(reference, cancellationToken).Symbol, local)) continue;
            if (reference.Ancestors().TakeWhile(ancestor => ancestor != scope)
                .Any(static ancestor => ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)) return false;
            if (reference.Parent is ArgumentSyntax argument && !argument.RefKindKeyword.IsKind(SyntaxKind.None)) return false;
            if (reference.Parent is AssignmentExpressionSyntax assignment && assignment.Left == reference) return false;
            if (reference.Parent is PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax) return false;
            if (reference.Parent is MemberAccessExpressionSyntax member && member.Expression == reference
                && member.Parent is InvocationExpressionSyntax invocation
                && model.GetSymbolInfo(invocation, cancellationToken).Symbol is IMethodSymbol method
                && method.Name is "Dispose" or "Complete" or "Move" or "ReturnToNativeMemory" or "ReturnToGC"
                    or "ReturnMemoryToNativeMemory" or "ReturnMemoryToGC" or "LeaseFromMemory") return false;
        }
        return true;
    }

    private static bool IsRuntimeOwner(ITypeSymbol type, Compilation compilation)
    {
        if (type is not INamedTypeSymbol named) return false;
        if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T) named = (INamedTypeSymbol)named.TypeArguments[0];
        IAssemblySymbol? runtime = compilation.GetTypeByMetadataName("Supprocom.NativeAllocationManagement.NativePool`1")?.ContainingAssembly;
        if (runtime is null || !SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, runtime)) return false;
        string metadataName = named.OriginalDefinition.MetadataName;
        return metadataName is "NativeRegion" or "NativeBuilder`1" or "NativeWorkspace`1" or "NativeTransfer`1"
            or "NativeMemoryReservation`1" or "NativeLayoutReservation" or "NativeLayoutOwner" or "NativeShared`1" or "NativeWeak`1";
    }

    private static async Task<bool> ProvesCorrectionAsync(Document original, Document candidate, string targetId, CancellationToken cancellationToken)
    {
        Compilation? before = await original.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        Compilation? after = await candidate.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        if (before is null || after is null || after.GetDiagnostics(cancellationToken).Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)) return false;
        ImmutableArray<Diagnostic> originalDiagnostics = await AnalyzeAsync(before, cancellationToken).ConfigureAwait(false);
        ImmutableArray<Diagnostic> correctedDiagnostics = await AnalyzeAsync(after, cancellationToken).ConfigureAwait(false);
        if (correctedDiagnostics.Count(diagnostic => string.Equals(diagnostic.Id, targetId, StringComparison.Ordinal))
            >= originalDiagnostics.Count(diagnostic => string.Equals(diagnostic.Id, targetId, StringComparison.Ordinal))) return false;
        Dictionary<string, int> remaining = new(StringComparer.Ordinal);
        foreach (Diagnostic diagnostic in originalDiagnostics)
        {
            string key = Key(diagnostic);
            remaining.TryGetValue(key, out int count);
            remaining[key] = count + 1;
        }
        foreach (Diagnostic diagnostic in correctedDiagnostics)
        {
            string key = Key(diagnostic);
            if (!remaining.TryGetValue(key, out int count) || count == 0) return false;
            remaining[key] = count - 1;
        }
        return true;
    }

    private static string Key(Diagnostic diagnostic) => diagnostic.Id + ":" + diagnostic.GetMessage(CultureInfo.InvariantCulture);

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(Compilation compilation, CancellationToken cancellationToken) =>
        await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new NativeAllocationAnalyzer()))
            .GetAnalyzerDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
}
