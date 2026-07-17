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
            bool compilerRejected = compilation.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            if (testCase.CompilerRejected)
            {
                Assert.True(compilerRejected, $"Corpus case '{testCase.Name}' was marked compilerRejected but compiled successfully.");
                compilerRejectedCases++;
                continue;
            }

            Assert.False(compilerRejected, $"Corpus case '{testCase.Name}' unexpectedly failed C# compilation.");
            CompilationWithAnalyzers analyzed = compilation.WithAnalyzers(
                ImmutableArray.Create<DiagnosticAnalyzer>(new NativeAllocationAnalyzer()));
            ImmutableArray<Diagnostic> actualDiagnostics = await analyzed.GetAnalyzerDiagnosticsAsync();
            string[] actual = AnalyzerContractTests.NativeDiagnostics(actualDiagnostics);
            foreach (string expected in testCase.ExpectedDiagnostics)
            {
                Assert.Contains(expected, actual);
            }

            if (testCase.ExpectedDiagnostics.Length == 0)
            {
                Assert.True(
                    actual.Length == 0,
                    $"Corpus case '{testCase.Name}' produced unexpected diagnostics: {string.Join(", ", actualDiagnostics.Select(diagnostic => $"{diagnostic.Id}@{diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1}:{diagnostic.GetMessage()}"))}.");
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
        string[] ExpectedDiagnostics,
        string Source);
}
