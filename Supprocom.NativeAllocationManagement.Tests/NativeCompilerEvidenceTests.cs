using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Supprocom.NativeAllocationManagement.Analyzers;
using Supprocom.NativeAllocationManagement.Performance;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeCompilerEvidenceTests
{
    [Fact]
    public void DedicatedCompilerHostHasNoRoslynAssemblyReference()
    {
        Assert.DoesNotContain(typeof(NativeCompilerCostWorker).Assembly.GetReferencedAssemblies(),
            static assembly => assembly.Name!.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal));
        Assert.Equal("Supprocom.NativeAllocationManagement.CompilerHost",
            typeof(NativeCompilerCostWorker).Assembly.GetName().Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualSdkHostLoadsTheBundledAnalyzerAndReportsTheRealCompilerOutcome(bool abandonLease)
    {
        DirectoryInfo runtime = new(Path.GetDirectoryName(typeof(object).Assembly.Location)!);
        string sdkRoot = runtime.Parent!.Parent!.Parent!.FullName;
        string dotnet = Path.Combine(sdkRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        string compiler = Path.Combine(sdkRoot, "sdk", "10.0.302", "Roslyn", "bincore", "csc.dll");
        Assert.True(File.Exists(dotnet), dotnet);
        Assert.True(File.Exists(compiler), compiler);
        string directory = Path.Combine(Path.GetTempPath(), "nam-compiler-host-" + Guid.NewGuid().ToString("N"), "input with spaces");
        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "Consumer source.cs");
        string response = Path.Combine(directory, "actual-arguments.rsp");
        string output = Path.Combine(directory, "Consumer.dll");
        try
        {
            string binding = abandonLease ? "Pooled<int>" : "using Pooled<int>";
            await File.WriteAllTextAsync(source, "using Supprocom.NativeAllocationManagement; public static class Consumer { public static int Work() { "
                + "using NativePool<int> pool = new(0, 0, NativeMemoryReturn.ToNativeMemory); "
                + binding + " lease = pool.Rent(1, static writer => writer.Write(17)); "
                + "return lease.Read(static view => view[0]); } }").ConfigureAwait(true);
            string[] framework = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
                .Split(Path.PathSeparator);
            string[] references = framework.Append(typeof(NativePool<int>).Assembly.Location)
                .Distinct(StringComparer.Ordinal).Select(static path => "/reference:\"" + path + "\"").ToArray();
            await File.WriteAllLinesAsync(response, new[] { "/noconfig", "/nostdlib+", "/target:library", "/langversion:13.0", "/out:\"" + output + "\"",
                "/analyzer:\"" + typeof(NativeAllocationAnalyzer).Assembly.Location + "\"" }
                .Concat(references).Append("\"" + source + "\"")).ConfigureAwait(true);
            ProcessStartInfo start = new(dotnet)
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in new[] { typeof(NativeCompilerCostWorker).Assembly.Location,
                "--compiler-cost-worker", "--compiler", compiler, "--response", response })
            {
                start.ArgumentList.Add(argument);
            }
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("Compiler host did not start.");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(120));
            try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(true); }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(true);
                throw;
            }
            string observations = await stdout.ConfigureAwait(true);
            string errors = await stderr.ConfigureAwait(true);
            await File.WriteAllTextAsync(Path.Combine(directory, "compiler.stdout.log"), observations).ConfigureAwait(true);
            await File.WriteAllTextAsync(Path.Combine(directory, "compiler.stderr.log"), errors).ConfigureAwait(true);
            Assert.Empty(errors);
            Assert.DoesNotContain("CS8033", observations, StringComparison.Ordinal);
            Assert.DoesNotContain("CS2023", observations, StringComparison.Ordinal);
#pragma warning disable HLQ005 // Xunit.Assert.Single enforces observation cardinality; First would conceal duplicate reports.
            string json = Assert.Single(observations.Split('\n'), static line => line.StartsWith('{'));
#pragma warning restore HLQ005
            NativeCompilerCostEvidence evidence = JsonSerializer.Deserialize<NativeCompilerCostEvidence>(json)!;
            Assert.Equal(abandonLease ? 1 : 0, process.ExitCode);
            Assert.Equal(process.ExitCode, evidence.ExitCode);
            Assert.Equal(!abandonLease, File.Exists(output));
            if (abandonLease) Assert.Contains("NAM1003", observations, StringComparison.Ordinal);
            Assert.Equal(response, evidence.ResponseFile.Path);
            Assert.Equal(new FileInfo(response).Length, evidence.ResponseFile.Length);
            Assert.Equal(64, evidence.ResponseFile.SHA256.Length);
            Assert.Contains("csc, Version=5.6.0.0", evidence.CompilerAssembly, StringComparison.Ordinal);
            Assert.True(evidence.AllocatedBytes > 0);
            Assert.True(double.IsFinite(evidence.CpuMilliseconds) && evidence.CpuMilliseconds > 0);
            Assert.True(double.IsFinite(evidence.WallMilliseconds) && evidence.WallMilliseconds > 0);
            Assert.True(evidence.Gen0Collections >= 0 && evidence.Gen1Collections >= 0 && evidence.Gen2Collections >= 0);
            Assert.NotEmpty(evidence.LatencyMode);
        }
        finally
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("NAM_RETAIN_PACKAGE_EVIDENCE"), "1", StringComparison.Ordinal))
                Directory.Delete(Directory.GetParent(directory)!.FullName, recursive: true);
        }
    }

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
    public async Task AReferencedRuntimeWithoutAnyNativeOperationCannotClaimNativeCancellation()
    {
        string[] framework = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(Path.PathSeparator);
        MetadataReference[] references = framework.Select(static path =>
            (MetadataReference)MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(NativePool<int>).Assembly.Location)).ToArray();
        CSharpCompilation plain = CSharpCompilation.Create("No native operation",
            [CSharpSyntaxTree.ParseText("public static class Plain { public static int Work() => 17; }")],
            references, new(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(plain.GetDiagnostics(), static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        await Assert.ThrowsAsync<InvalidOperationException>(() => NativeAnalyzerCancellationWorker.MeasureAsync(plain));
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
