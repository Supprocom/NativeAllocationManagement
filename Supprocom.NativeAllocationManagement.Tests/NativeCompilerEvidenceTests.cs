using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Supprocom.NativeAllocationManagement.Performance;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeCompilerEvidenceTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void MissingCompilerOrResponseArgumentsCannotBecomeMeasurements(string missing)
    {
        Assert.Throws<ArgumentException>(() => NativeCompilerCostWorker.Run(missing, "response.rsp"));
        Assert.Throws<ArgumentException>(() => NativeCompilerCostWorker.Run("csc.dll", missing));
    }

    [Fact]
    public void MissingInputFailsBeforeAnyCompilerExecutionOrMeasurementReport()
    {
        string absent = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "csc.dll");
        Assert.Throws<DirectoryNotFoundException>(() => NativeCompilerCostWorker.Run(absent, "response.rsp"));
    }

    [Fact]
    public async Task ACompilationWithoutTheAuthenticRuntimeCannotClaimNativeCancellation()
    {
        CSharpCompilation plain = CSharpCompilation.Create("No runtime",
            [CSharpSyntaxTree.ParseText("public sealed class Plain { }")],
            options: new(OutputKind.DynamicallyLinkedLibrary));
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeAnalyzerCancellationWorker.MeasureAsync(plain));
    }

    [Fact]
    public async Task InFlightCancellationIsFollowedByACompleteUncancelledAnalysis()
    {
        // This generated fixture checks the measurement mechanism only. Real
        // compiler evidence separately uses the full MSBuild-derived consumer.
        StringBuilder source = new("using Supprocom.NativeAllocationManagement; public static class Consumer {");
        for (int method = 0; method < 256; method++)
        {
            source.Append("public static int Work").Append(method).Append("() {")
                .Append("using NativePool<int> pool = new(0, 0, NativeMemoryReturn.ToNativeMemory);")
                .Append("using Pooled<int> lease = pool.Rent(1, static writer => writer.Write(17));")
                .Append("return lease.Read(static view => view[0]); }");
        }
        source.Append('}');
        string[] framework = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(Path.PathSeparator);
        MetadataReference[] references = framework.Select(static path =>
            (MetadataReference)MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(NativePool<int>).Assembly.Location)).ToArray();
        CSharpCompilation compilation = CSharpCompilation.Create("Cancellation mechanism fixture",
            [CSharpSyntaxTree.ParseText(source.ToString())], references,
            new(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(compilation.GetDiagnostics(), static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        NativeAnalyzerCancellationEvidence evidence = await NativeAnalyzerCancellationWorker.MeasureAsync(compilation);
        Assert.True(evidence.AnalysisWasInFlight);
        Assert.True(evidence.Cancelled);
        Assert.True(evidence.NativeOperationBlocks > 0);
        Assert.Equal(1, evidence.SourceFileCount);
        Assert.NotEmpty(evidence.TriggerOperation);
        Assert.Empty(evidence.FollowUpDiagnosticIds);
        Assert.True(double.IsFinite(evidence.CancellationMilliseconds));
        Assert.True(evidence.CancellationMilliseconds >= 0);
        Assert.Equal(typeof(CSharpCompilation).Assembly.GetName().Version!.ToString(), evidence.RoslynVersion);
        Assert.Contains("not SDK console cancellation", evidence.Scope, StringComparison.Ordinal);
    }
}
