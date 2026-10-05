using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Supprocom.NativeAllocationManagement.Analyzers;

namespace Supprocom.NativeAllocationManagement.Performance;

internal static class NativeAnalyzerCancellationWorker
{
    internal static async Task<int> RunAsync(string responsePath, string projectDirectory)
    {
        string response = Path.GetFullPath(responsePath);
        string directory = Path.GetFullPath(projectDirectory);
        CSharpCommandLineArguments arguments = CSharpCommandLineParser.Default.Parse(
            ["@" + response], directory, sdkDirectory: null, additionalReferenceDirectories: null);
        if (arguments.Errors.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            throw new InvalidDataException(string.Join(Environment.NewLine, arguments.Errors));
        SyntaxTree[] sources = arguments.SourceFiles.Select(source =>
        {
            if (source.IsInputRedirected || source.IsScript)
                throw new InvalidDataException("Cancellation evidence requires real regular consumer source files.");
            string path = Path.GetFullPath(source.Path, directory);
            return CSharpSyntaxTree.ParseText(File.ReadAllText(path), arguments.ParseOptions, path, Encoding.UTF8);
        }).ToArray();
        MetadataReference[] references = arguments.MetadataReferences.Select(reference =>
            (MetadataReference)MetadataReference.CreateFromFile(
                Path.GetFullPath(reference.Reference, directory), reference.Properties)).ToArray();
        CSharpCompilation compilation = CSharpCompilation.Create("NAM real consumer cancellation",
            sources, references, arguments.CompilationOptions);
        ImmutableArray<Diagnostic> compilerErrors = compilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToImmutableArray();
        if (!compilerErrors.IsEmpty)
            throw new InvalidDataException(string.Join(Environment.NewLine, compilerErrors));
        NativeAnalyzerCancellationEvidence evidence = await MeasureAsync(compilation).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(evidence));
        return evidence.Cancelled && evidence.AnalysisWasInFlight && evidence.FollowUpDiagnosticIds.Length == 0 ? 0 : 3;
    }

    internal static async Task<NativeAnalyzerCancellationEvidence> MeasureAsync(CSharpCompilation compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        IAssemblySymbol runtime = compilation.GetTypeByMetadataName("Supprocom.NativeAllocationManagement.NativePool`1")?.ContainingAssembly
            ?? throw new InvalidDataException("The actual NAM runtime metadata reference is required.");
        using CancellationTokenSource cancellation = new();
        NativeOperationProbe probe = new(runtime, cancellation);
        ImmutableArray<DiagnosticAnalyzer> analyzers =
            [new NativeAllocationAnalyzer(), new NativeBuilderWriteAnalyzer(), probe];
        CompilationWithAnalyzers driver = compilation.WithAnalyzers(analyzers);
        Task<ImmutableArray<Diagnostic>> analysis = driver.GetAnalyzerDiagnosticsAsync(cancellation.Token);
        try { _ = await Task.WhenAny(analysis, probe.Entered).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            try { _ = await analysis.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            throw;
        }
        if (!probe.Entered.IsCompletedSuccessfully)
        {
            _ = await analysis.ConfigureAwait(false);
            throw new InvalidOperationException("The real consumer analysis completed before an in-flight cancellation could be triggered.");
        }
        string operation = await probe.Entered.ConfigureAwait(false);
        bool cancelled = false;
        try { _ = await analysis.ConfigureAwait(false); }
        catch (OperationCanceledException) { cancelled = true; }
        double latency = Stopwatch.GetElapsedTime(probe.CancellationStarted).TotalMilliseconds;

        // A new driver prevents a cancelled partial result from posing as a
        // valid cached result. This runs the same complete real consumer again.
        ImmutableArray<Diagnostic> followUp = await compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new NativeAllocationAnalyzer(), new NativeBuilderWriteAnalyzer()))
            .GetAnalyzerDiagnosticsAsync(CancellationToken.None).ConfigureAwait(false);
        return new(typeof(CSharpCompilation).Assembly.GetName().Version!.ToString(),
            compilation.SyntaxTrees.Length, probe.NativeOperationBlocks,
            operation, true, cancelled, latency,
            followUp.Select(static diagnostic => diagnostic.Id).Order(StringComparer.Ordinal).ToArray(),
            "Real MSBuild-derived consumer input in the Roslyn analysis driver, not SDK console cancellation. The probe requests cancellation synchronously from entry to an operation block containing authenticated runtime metadata; it is not an observation inside NAM's own callback. Latency includes the waiting observer's scheduling delay. No artificial blocking or synthetic result cache is used.");
    }

    // A private measurement probe, never included in the shipped analyzer or
    // exposed as a consumer diagnostic. It does not need public rule releases.
#pragma warning disable RS1001, RS1036, RS1041, RS2008
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class NativeOperationProbe(IAssemblySymbol runtime, CancellationTokenSource cancellation) : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Marker = new("NAMPERF0002", "Native operation entry",
            "Native operation entry", "Performance evidence", DiagnosticSeverity.Hidden, isEnabledByDefault: true);
        private readonly TaskCompletionSource<string> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _nativeOperationBlocks;
        private int _triggerClaimed;
        private long _cancellationStarted;
        internal Task<string> Entered => _entered.Task;
        internal int NativeOperationBlocks => Volatile.Read(ref _nativeOperationBlocks);
        internal long CancellationStarted => Volatile.Read(ref _cancellationStarted);
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Marker];

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
#pragma warning disable RS1012 // The private entry observer requests cancellation; it intentionally registers no diagnostic/end action and performs no artificial no-op work.
            context.RegisterOperationBlockStartAction(observation =>
            {
                if (!observation.OperationBlocks.Any(ContainsNativeType)) return;
                Interlocked.Increment(ref _nativeOperationBlocks);
                if (Interlocked.CompareExchange(ref _triggerClaimed, 1, 0) != 0) return;
                string operation = observation.OwningSymbol.ToDisplayString();
                Volatile.Write(ref _cancellationStarted, Stopwatch.GetTimestamp());
                // The caller owns this CTS. Request cancellation before leaving
                // the callback, rather than racing an asynchronously scheduled
                // observer against complete analysis. This never blocks on a
                // test latch or delays NAM work to manufacture an in-flight task.
                cancellation.Cancel();
                _entered.TrySetResult(operation);
            });
#pragma warning restore RS1012
        }

        private bool ContainsNativeType(IOperation operation)
        {
            if (operation.Type is { } type && SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, runtime)) return true;
            foreach (IOperation child in operation.ChildOperations)
            {
                if (ContainsNativeType(child)) return true;
            }
            return false;
        }
    }
#pragma warning restore RS1001, RS1036, RS1041, RS2008
}

internal sealed record NativeAnalyzerCancellationEvidence(
    string RoslynVersion,
    int SourceFileCount,
    int NativeOperationBlocks,
    string TriggerOperation,
    bool AnalysisWasInFlight,
    bool Cancelled,
    double CancellationMilliseconds,
    string[] FollowUpDiagnosticIds,
    string Scope);
