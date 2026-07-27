using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Supprocom.NativeAllocationManagement.Analyzers;
using Xunit.Abstractions;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class AnalyzerConformanceCorpusTests
{
    private readonly ITestOutputHelper _output;

    public AnalyzerConformanceCorpusTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task SharedOwnershipCorpusMatchesRoslynDiagnosticsAndCompilerBoundary()
    {
        string path = FindRepositoryRoot();
        path = Path.Combine(path, "conformance", "native-allocation-ownership.json");
        CorpusCase[] cases = JsonSerializer.Deserialize<CorpusCase[]>(
            await File.ReadAllTextAsync(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("The ownership conformance corpus was empty.");
        int analyzerCases = 0;
        int compilerRejectedCases = 0;

        foreach (CorpusCase testCase in cases)
        {
            SyntaxTree tree = CSharpSyntaxTree.ParseText(testCase.Source, new CSharpParseOptions(LanguageVersion.Preview));
            CSharpCompilation compilation = CSharpCompilation.Create(
                "NativeAllocationCorpus_" + testCase.Name,
                [tree],
                [.. GetTrustedPlatformReferences(), MetadataReference.CreateFromFile(typeof(NativePool<int>).Assembly.Location)],
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
            Diagnostic[] compilerErrors = compilation.GetDiagnostics()
                .Where(
                    static diagnostic =>
                        diagnostic.Severity == DiagnosticSeverity.Error)
                .ToArray();
            bool compilerRejected = compilerErrors.Length != 0;
            if (testCase.CompilerRejected)
            {
                Assert.True(compilerRejected, $"Corpus case '{testCase.Name}' was marked compilerRejected but compiled successfully.");
                compilerRejectedCases++;
                continue;
            }

            Assert.False(
                compilerRejected,
                $"Corpus case '{testCase.Name}' unexpectedly failed C# compilation."
                    + Environment.NewLine
                    + string.Join(
                        Environment.NewLine,
                        compilerErrors.Select(
                            static diagnostic =>
                                diagnostic.ToString())));
            CompilationWithAnalyzers analyzed = compilation.WithAnalyzers(
                ImmutableArray.Create<DiagnosticAnalyzer>(new NativeAllocationAnalyzer()));
            ImmutableArray<Diagnostic> actualDiagnostics = await analyzed.GetAnalyzerDiagnosticsAsync();
            ExpectedDiagnostic[] expectedDiagnostics = testCase.ExpectedDiagnostics;
            ActualDiagnostic[] actual = actualDiagnostics
                .Where(diagnostic => diagnostic.Id.StartsWith("NAM", StringComparison.Ordinal))
                .Select(diagnostic => new ActualDiagnostic(
                    diagnostic.Id,
                    diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1,
                    diagnostic.Location.GetLineSpan().StartLinePosition.Character + 1,
                    diagnostic.Severity.ToString(),
                    diagnostic.Properties.TryGetValue("NAM.Provenance", out string? provenance)
                        ? provenance
                        ?? string.Empty
                        : string.Empty))
                .ToArray();

            string[] expectedIds = expectedDiagnostics
                .Select(static expected => expected.Id)
                .OrderBy(
                    static id => id,
                    StringComparer.Ordinal)
                .ToArray();
            string[] actualIds = actual
                .Select(static diagnostic => diagnostic.Id)
                .Distinct()
                .OrderBy(
                    static id => id,
                    StringComparer.Ordinal)
                .ToArray();
            Assert.True(
                expectedIds.SequenceEqual(actualIds),
                $"Corpus case '{testCase.Name}' diagnostic identifiers differ."
                    + Environment.NewLine
                    + $"Expected: {string.Join(", ", expectedIds)}"
                    + Environment.NewLine
                    + $"Actual: {string.Join(", ", actualIds)}");

            foreach (ExpectedDiagnostic expected in expectedDiagnostics)
            {
                ActualDiagnostic[] actualForId = actual.Where(diagnostic => diagnostic.Id == expected.Id).ToArray();
                Assert.True(
                    expected.Count == actualForId.Length,
                    $"Corpus case '{testCase.Name}' expected {expected.Count} "
                        + $"{expected.Id} diagnostics but received "
                        + $"{actualForId.Length}.");
                if (expected.Severity is not null)
                {
                    Assert.All(actualForId, diagnostic => Assert.Equal(expected.Severity, diagnostic.Severity));
                }

                DiagnosticFact[] expectedFacts = expected.Facts
                    .OrderBy(static fact => fact.Line)
                    .ThenBy(static fact => fact.Column)
                    .ThenBy(
                        static fact => fact.Provenance,
                        StringComparer.Ordinal)
                    .ToArray();
                DiagnosticFact[] actualFacts = actualForId
                    .Select(
                        static diagnostic =>
                            new DiagnosticFact(
                                diagnostic.Line,
                                diagnostic.Column,
                                diagnostic.Provenance))
                    .OrderBy(static fact => fact.Line)
                    .ThenBy(static fact => fact.Column)
                    .ThenBy(
                        static fact => fact.Provenance,
                        StringComparer.Ordinal)
                    .ToArray();
                Assert.True(
                    expectedFacts.SequenceEqual(actualFacts),
                    $"Corpus case '{testCase.Name}' facts differ for "
                        + $"{expected.Id}."
                        + Environment.NewLine
                        + $"Expected: {string.Join(", ", expectedFacts)}"
                        + Environment.NewLine
                        + $"Actual: {string.Join(", ", actualFacts)}");
            }

            analyzerCases++;
        }

        _output.WriteLine($"corpusCases={cases.Length}; analyzerCases={analyzerCases}; compilerRejectedCases={compilerRejectedCases}");
        Assert.True(analyzerCases > 0);
        Assert.True(compilerRejectedCases > 0);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Supprocom.NativeAllocationManagement.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("The repository root was not found from the test output directory.");
    }

    private static IEnumerable<MetadataReference> GetTrustedPlatformReferences()
    {
        string trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string
            ?? throw new InvalidOperationException("The test host did not expose trusted platform assemblies.");
        return trustedAssemblies.Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path));
    }

    private sealed record CorpusCase(
        string Name,
        bool CompilerRejected,
        ExpectedDiagnostic[] ExpectedDiagnostics,
        string Source);

    private sealed record ExpectedDiagnostic(
        string Id,
        int Count,
        DiagnosticFact[] Facts,
        string? Severity = null);

    private sealed record DiagnosticFact(int Line, int Column, string Provenance);

    private sealed record ActualDiagnostic(
        string Id,
        int Line,
        int Column,
        string Severity,
        string Provenance);

}
