using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Supprocom.NativeAllocationManagement.Analyzers;
using Supprocom.NativeAllocationManagement.CodeFixes;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeOwnershipCodeFixTests
{
    private static readonly ImmutableArray<MetadataReference> References = CreateReferences();

    [Theory]
    [InlineData("NativeRegion region = new(); Local<int> value = region.Lease<int>(1, static writer => writer.Fill(17)); _ = value.Length;", "NAM1006", "using NativeRegion")]
    [InlineData("NativeBuilder<int> builder = new(4); builder.Append(17);", "NAM1031", "using NativeBuilder<int>")]
    [InlineData("NativeWorkspace<int> workspace = new(4); workspace.Initialize(1, static writer => writer.Fill(17));", "NAM1038", "using NativeWorkspace<int>")]
    [InlineData("using NativeBuilder<int> builder = new(4); builder.Append(17); NativeTransfer<int> owner = builder.Complete(); _ = owner.Read(static view => view[0]);", "NAM1025", "using NativeTransfer<int>")]
    public async Task LexicalCorrectionActuallyAppliesCompilesAndRemovesTheDefect(string body, string diagnosticId, string expectedText)
    {
        using AdhocWorkspace workspace = new();
        Document document = CreateDocument(workspace, Wrap(body));
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(document);
        Diagnostic[] targets = diagnostics.Where(diagnostic => string.Equals(diagnostic.Id, diagnosticId, StringComparison.Ordinal)).ToArray();
        // xUnit's exact-one assertion is not LINQ Single and has no Hyperlinq alternative.
#pragma warning disable HLQ005
        Diagnostic target = Assert.Single(targets);
#pragma warning restore HLQ005
        List<CodeAction> actions = [];
        NativeOwnershipCodeFixProvider provider = new();
        await provider.RegisterCodeFixesAsync(new(document, target, (action, _) => actions.Add(action), CancellationToken.None));
#pragma warning disable HLQ005
        CodeAction action = Assert.Single(actions);
#pragma warning restore HLQ005
        ImmutableArray<CodeActionOperation> operations = await action.GetOperationsAsync(CancellationToken.None);
        ApplyChangesOperation[] changes = operations.OfType<ApplyChangesOperation>().ToArray();
#pragma warning disable HLQ005
        ApplyChangesOperation change = Assert.Single(changes);
#pragma warning restore HLQ005
        Document corrected = change.ChangedSolution.GetDocument(document.Id)!;
        Assert.Contains(expectedText, (await corrected.GetTextAsync(CancellationToken.None)).ToString(), StringComparison.Ordinal);
        Compilation compilation = (await corrected.Project.GetCompilationAsync(CancellationToken.None))!;
        Assert.DoesNotContain(compilation.GetDiagnostics(CancellationToken.None), static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(await AnalyzeAsync(corrected)));
        Assert.Null(provider.GetFixAllProvider());
        List<CodeAction> repeated = [];
        Diagnostic stale = Diagnostic.Create(target.Descriptor,
            Location.Create((await corrected.GetSyntaxTreeAsync(CancellationToken.None))!, target.Location.SourceSpan));
        await provider.RegisterCodeFixesAsync(new(corrected, stale, (item, _) => repeated.Add(item), CancellationToken.None));
        Assert.Empty(repeated);
    }

    [Theory]
    [InlineData("NativeRegion region = new(); region.Dispose();", "NAM1006")]
    [InlineData("NativeRegion region = new(), other = new();", "NAM1006")]
    [InlineData("NativeRegion region = new(); System.Func<int> allocating = () => region.Id.GetHashCode(); _ = allocating();", "NAM1006")]
    [InlineData("NativeBuilder<int> builder = new(4); _ = builder.Complete();", "NAM1031")]
    [InlineData("NativeRegion region = new(); region = new();", "NAM1006")]
    [InlineData("NativeRegion region = new(); static void Take(ref NativeRegion value) { } Take(ref region);", "NAM1006")]
    [InlineData("NativeRegion region = new(); goto done; done: _ = region.Id;", "NAM1006")]
    [InlineData("NativeRegion region = new(); int ReadId() => region.Id.GetHashCode(); _ = ReadId();", "NAM1006")]
    public async Task AmbiguousOrNewlyInvalidCleanupIsNotOffered(string body, string diagnosticId)
    {
        using AdhocWorkspace workspace = new();
        Document document = CreateDocument(workspace, Wrap(body));
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(document);
        Diagnostic? target = diagnostics.FirstOrDefault(diagnostic => string.Equals(diagnostic.Id, diagnosticId, StringComparison.Ordinal));
        // A competing completed-owner shape can already lack this lifetime
        // diagnostic. A fabricated request still must not offer unsafe cleanup.
        target ??= Diagnostic.Create(new DiagnosticDescriptor(diagnosticId, "request", "request", "test", DiagnosticSeverity.Error, true),
            Location.Create((await document.GetSyntaxTreeAsync(CancellationToken.None))!, new TextSpan(Wrap(body).IndexOf("new(4)", StringComparison.Ordinal), 6)));
        List<CodeAction> actions = [];
        await new NativeOwnershipCodeFixProvider().RegisterCodeFixesAsync(new(document, target,
            (action, _) => actions.Add(action), CancellationToken.None));
        Assert.Empty(actions);
    }

    [Fact]
    public async Task CancelledCorrectionDoesNotRegisterAnAction()
    {
        using AdhocWorkspace workspace = new();
        Document document = CreateDocument(workspace, Wrap("NativeRegion region = new();"));
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(document);
        Diagnostic target = diagnostics.First(static diagnostic => string.Equals(diagnostic.Id, "NAM1006", StringComparison.Ordinal));
        List<CodeAction> actions = [];
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new NativeOwnershipCodeFixProvider()
            .RegisterCodeFixesAsync(new(document, target, (action, _) => actions.Add(action), cancellation.Token)));
        Assert.Empty(actions);
    }

    [Fact]
    public async Task SourceLookalikeDoesNotReceiveACompiledRuntimeCorrection()
    {
        using AdhocWorkspace workspace = new();
        Document document = CreateDocument(workspace, """
            using System;
            public struct NativeRegion : IDisposable { public void Dispose() { } }
            public static class Sample { public static void Run() { NativeRegion region = new(); } }
            """);
        SyntaxTree tree = (await document.GetSyntaxTreeAsync(CancellationToken.None))!;
        Diagnostic target = Diagnostic.Create(new NativeAllocationAnalyzer().SupportedDiagnostics
            .First(static descriptor => string.Equals(descriptor.Id, "NAM1006", StringComparison.Ordinal)),
            Location.Create(tree, new TextSpan((await document.GetTextAsync(CancellationToken.None)).ToString()
                .IndexOf("region =", StringComparison.Ordinal), 6)));
        List<CodeAction> actions = [];
        await new NativeOwnershipCodeFixProvider().RegisterCodeFixesAsync(new(document, target,
            (action, _) => actions.Add(action), CancellationToken.None));
        Assert.Empty(actions);
    }

    [Fact]
    public void EveryPublishedDiagnosticHasOneActualDocumentationPageAndHelpUri()
    {
        NativeAllocationAnalyzer analyzer = new();
        string root = RepositoryTestPaths.Root;
        foreach (DiagnosticDescriptor descriptor in analyzer.SupportedDiagnostics)
        {
            string file = Path.Combine(root, "docs", "diagnostics", descriptor.Id + ".md");
            Assert.True(File.Exists(file), file);
            Assert.Equal("https://github.com/Supprocom/NativeAllocationManagement/blob/main/docs/diagnostics/" + descriptor.Id + ".md", descriptor.HelpLinkUri);
            string content = File.ReadAllText(file);
            Assert.Contains(descriptor.Id, content, StringComparison.Ordinal);
            string severity = descriptor.DefaultSeverity switch
            {
                DiagnosticSeverity.Error => "error",
                DiagnosticSeverity.Warning => "warning",
                _ => throw new InvalidOperationException("The shipped NAM policy contains an unrecognized documentation severity.")
            };
            Assert.Contains("Severity: " + severity, content, StringComparison.Ordinal);
        }
        Assert.Equal(analyzer.SupportedDiagnostics.Length, Directory.EnumerateFiles(Path.Combine(root, "docs", "diagnostics"), "NAM*.md").Count());
    }

    private static Document CreateDocument(AdhocWorkspace workspace, string source)
    {
        Project project = workspace.AddProject("Consumer", LanguageNames.CSharp)
            .WithParseOptions(new CSharpParseOptions(LanguageVersion.CSharp13))
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true))
            .WithMetadataReferences(References);
        return project.AddDocument("Consumer.cs", SourceText.From(source));
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(Document document)
    {
        Compilation compilation = (await document.Project.GetCompilationAsync(CancellationToken.None).ConfigureAwait(false))!;
        return await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new NativeAllocationAnalyzer()))
            .GetAnalyzerDiagnosticsAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static ImmutableArray<MetadataReference> CreateReferences()
    {
        string platform = Assert.IsType<string>(AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"));
        return platform.Split(Path.PathSeparator).Append(typeof(NativePool<int>).Assembly.Location)
            .Distinct(StringComparer.Ordinal).Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path)).ToImmutableArray();
    }

    private static string Wrap(string body) => $$"""
        using Supprocom.NativeAllocationManagement;
        public static class Sample
        {
            public static void Run()
            {
                {{body}}
            }
        }
        """;
}
