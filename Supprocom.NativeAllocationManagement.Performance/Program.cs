using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Supprocom.NativeAllocationManagement;
using Supprocom.NativeAllocationManagement.Analyzers;

namespace Supprocom.NativeAllocationManagement.Performance;

internal static class Program
{
    private const int CorpusProjects = 8;
    private const int MethodsPerProject = 12;
    private const int WorkloadIterations = 2_000;
    private const int WorkloadElements = 32_000;
    private static int _sink;

    private static async Task<int> Main()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
        Console.WriteLine("# NativeAllocationManagement V1 performance evidence");
        Console.WriteLine();
        Console.WriteLine("The harness uses fixed workloads and reports measurements without timing assertions.");
        Console.WriteLine();

        AnalyzerEvidence analyzer = await MeasureAnalyzerAsync();
        Console.WriteLine("## Analyzer measurements");
        Console.WriteLine();
        Console.WriteLine("| case | elapsedMs | allocatedBytes | diagnostics | operationBlocks | cancellation |");
        Console.WriteLine("| --- | ---: | ---: | ---: | ---: | --- |");
        Console.WriteLine($"| cold | {analyzer.ColdElapsedMs:F2} | {analyzer.ColdAllocatedBytes} | {analyzer.ColdDiagnostics} | {analyzer.ColdOperationBlocks} | no |");
        Console.WriteLine($"| changed-project incremental | {analyzer.IncrementalElapsedMs:F2} | {analyzer.IncrementalAllocatedBytes} | {analyzer.IncrementalDiagnostics} | {analyzer.IncrementalOperationBlocks} of {analyzer.FullOperationBlocks} | no |");
        Console.WriteLine($"| cancellation | {analyzer.CancellationElapsedMs:F2} | n/a | n/a | n/a | {analyzer.CancellationObserved} |");
        Console.WriteLine();
        Console.WriteLine($"Representative corpus: {CorpusProjects} independent syntax trees, {analyzer.FullOperationBlocks} method operation blocks. The incremental edit replaces one project tree and schedules {analyzer.IncrementalOperationBlocks} blocks; the unchanged trees are retained and are not scheduled by the harness.");
        Console.WriteLine();

        Console.WriteLine("## Workload measurements");
        Console.WriteLine();
        Console.WriteLine("| workload | elapsedMs | throughputPerSecond | managedAllocatedBytes | gen0 | gen1 | gen2 | lohBytesAfter | retainedNativeBytes | callbackOverheadNs | peakResidentBytes |");
        Console.WriteLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (WorkloadEvidence workload in MeasureWorkloads())
        {
            Console.WriteLine($"| {workload.Name} | {workload.ElapsedMs:F2} | {workload.ThroughputPerSecond:F2} | {workload.ManagedAllocatedBytes} | {workload.Gen0Collections} | {workload.Gen1Collections} | {workload.Gen2Collections} | {workload.LohBytesDelta} | {workload.RetainedNativeBytes} | {workload.CallbackOverheadNanoseconds:F2} | {workload.PeakResidentBytes} |");
        }

        GC.KeepAlive(_sink);
        return 0;
    }

    private static async Task<AnalyzerEvidence> MeasureAnalyzerAsync()
    {
        List<SyntaxTree> trees = [];
        for (int project = 0; project < CorpusProjects; project++)
        {
            trees.Add(CSharpSyntaxTree.ParseText(CreateCorpusSource(project), new CSharpParseOptions(LanguageVersion.Preview), $"Project{project}.cs"));
        }

        CSharpCompilation compilation = CreateCompilation(trees);
        int fullOperationBlocks = CorpusProjects * MethodsPerProject;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        long coldAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch coldClock = Stopwatch.StartNew();
        OperationBlockCounterAnalyzer coldCounter = new();
        ImmutableArray<Diagnostic> coldDiagnostics = await AnalyzeAsync(compilation, CancellationToken.None, coldCounter);
        coldClock.Stop();
        long coldAllocated = GC.GetTotalAllocatedBytes(precise: true) - coldAllocatedBefore;

        SyntaxTree editedTree = CSharpSyntaxTree.ParseText(
            CreateCorpusSource(0).Replace("value[0] = 0;", "value[0] = 1;", StringComparison.Ordinal),
            new CSharpParseOptions(LanguageVersion.Preview),
            "Project0.cs");
        CSharpCompilation changedProject = CreateCompilation([editedTree]);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        long incrementalAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch incrementalClock = Stopwatch.StartNew();
        OperationBlockCounterAnalyzer incrementalCounter = new();
        ImmutableArray<Diagnostic> incrementalDiagnostics = await AnalyzeAsync(changedProject, CancellationToken.None, incrementalCounter);
        incrementalClock.Stop();
        long incrementalAllocated = GC.GetTotalAllocatedBytes(precise: true) - incrementalAllocatedBefore;

        CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Stopwatch cancellationClock = Stopwatch.StartNew();
        bool cancellationObserved;
        try
        {
            _ = await AnalyzeAsync(compilation, cancellation.Token);
            cancellationObserved = false;
        }
        catch (OperationCanceledException)
        {
            cancellationObserved = true;
        }

        cancellationClock.Stop();
        return new AnalyzerEvidence(
            coldClock.Elapsed.TotalMilliseconds,
            coldAllocated,
            coldDiagnostics.Length,
            incrementalClock.Elapsed.TotalMilliseconds,
            incrementalAllocated,
            incrementalDiagnostics.Length,
            cancellationClock.Elapsed.TotalMilliseconds,
            cancellationObserved,
            fullOperationBlocks,
            coldCounter.Count,
            incrementalCounter.Count);
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(
        CSharpCompilation compilation,
        CancellationToken cancellationToken,
        params DiagnosticAnalyzer[] additionalAnalyzers)
    {
        DiagnosticAnalyzer[] analyzers = [new NativeAllocationAnalyzer(), .. additionalAnalyzers];
        CompilationWithAnalyzers analyzed = compilation.WithAnalyzers(
            ImmutableArray.CreateRange(analyzers));
        return await analyzed.GetAnalyzerDiagnosticsAsync(cancellationToken);
    }

    private static CSharpCompilation CreateCompilation(IEnumerable<SyntaxTree> trees)
    {
        return CSharpCompilation.Create(
            "NativeAllocationPerformanceCorpus",
            trees,
            [.. GetTrustedPlatformReferences(), MetadataReference.CreateFromFile(typeof(NativePool<int>).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
    }

    private static string CreateCorpusSource(int project)
    {
        StringBuilder source = new();
        source.AppendLine("using Supprocom.NativeAllocationManagement;");
        source.AppendLine($"public static class CorpusProject{project} {{");
        for (int method = 0; method < MethodsPerProject; method++)
        {
            source.AppendLine($"public static void Method{method}() {{ NativePool<int> pool = new(); Pooled<int> value = pool.Rent(1); value[0] = 0; value.Dispose(); pool.Dispose(); }}");
        }

        source.AppendLine("}");
        return source.ToString();
    }

    private static IEnumerable<MetadataReference> GetTrustedPlatformReferences()
    {
        string trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string
            ?? throw new InvalidOperationException("The runtime did not expose trusted platform assemblies.");
        return trustedAssemblies.Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path));
    }

    private static IEnumerable<WorkloadEvidence> MeasureWorkloads()
    {
        yield return Measure("managed-array", RunManagedArray);
        yield return Measure("array-pool", RunArrayPool);
        yield return Measure("native-pool", RunNativePool);
        yield return Measure("native-region", RunNativeRegion);
    }

    private static WorkloadEvidence Measure(string name, Action<CallbackMeasurement> workload)
    {
        NativeMemoryTestHooks.Reset();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        long managedBefore = GC.GetTotalAllocatedBytes(precise: true);
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        long peakResident = Process.GetCurrentProcess().WorkingSet64;
        CallbackMeasurement callback = new(peakResident);
        Stopwatch clock = Stopwatch.StartNew();
        workload(callback);
        clock.Stop();
        peakResident = Math.Max(Math.Max(peakResident, Process.GetCurrentProcess().WorkingSet64), callback.PeakResidentBytes);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        long managedAllocated = GC.GetTotalAllocatedBytes(precise: true) - managedBefore;
        long lohAfter = GetLohBytes();
        NativeMemoryTestMetrics native = NativeMemoryTestHooks.Snapshot();
        return new WorkloadEvidence(
            name,
            clock.Elapsed.TotalMilliseconds,
            WorkloadIterations / Math.Max(clock.Elapsed.TotalSeconds, double.Epsilon),
            managedAllocated,
            GC.CollectionCount(0) - gen0Before,
            GC.CollectionCount(1) - gen1Before,
            GC.CollectionCount(2) - gen2Before,
            lohAfter,
            native.OutstandingNativeBytes,
            callback.Count == 0 ? 0 : callback.ElapsedTicks * 1_000_000_000d / Stopwatch.Frequency / callback.Count,
            peakResident);
    }

    private static void RunManagedArray(CallbackMeasurement callback)
    {
        for (int iteration = 0; iteration < WorkloadIterations; iteration++)
        {
            int[] values = new int[WorkloadElements];
            callback.Run(values.AsSpan());
        }
    }

    private static void RunArrayPool(CallbackMeasurement callback)
    {
        ArrayPool<int> pool = ArrayPool<int>.Shared;
        for (int iteration = 0; iteration < WorkloadIterations; iteration++)
        {
            int[] values = pool.Rent(WorkloadElements);
            try
            {
                callback.Run(values.AsSpan(0, WorkloadElements));
            }
            finally
            {
                pool.Return(values, clearArray: true);
            }
        }
    }

    private static void RunNativePool(CallbackMeasurement callback)
    {
        NativePool<int> pool = new(WorkloadElements, NativeReturn.ToNativeMemory);
        try
        {
            for (int iteration = 0; iteration < WorkloadIterations; iteration++)
            {
                Pooled<int> values = pool.Rent(WorkloadElements);
                callback.Run(values);
                values.Dispose();
            }
        }
        finally
        {
            pool.Dispose();
        }
    }

    private static void RunNativeRegion(CallbackMeasurement callback)
    {
        for (int iteration = 0; iteration < WorkloadIterations; iteration++)
        {
            NativeRegion region = new((nuint)(WorkloadElements * sizeof(int)), NativeReturn.ToNativeMemory);
            Local<int> values = region.Allocate<int>(WorkloadElements);
            callback.Run(values);
            region.Dispose();
        }
    }

    private static long GetLohBytes()
    {
        ReadOnlySpan<GCGenerationInfo> generations = GC.GetGCMemoryInfo().GenerationInfo;
        return generations.Length > 3 ? generations[3].SizeAfterBytes : -1;
    }

    private sealed class CallbackMeasurement
    {
        internal CallbackMeasurement(long initialResidentBytes)
        {
            PeakResidentBytes = initialResidentBytes;
        }

        internal long Count { get; private set; }
        internal long ElapsedTicks { get; private set; }
        internal long PeakResidentBytes { get; private set; }

        internal void Run(Span<int> values)
        {
            long start = Stopwatch.GetTimestamp();
            values[0] = values.Length;
            Volatile.Write(ref _sink, values[0]);
            Count++;
            ElapsedTicks += Stopwatch.GetTimestamp() - start;
            if (Count % 128 == 0)
            {
                PeakResidentBytes = Math.Max(PeakResidentBytes, Process.GetCurrentProcess().WorkingSet64);
            }
        }

        internal void Run(Pooled<int> values)
        {
            values.Access(Run);
        }

        internal void Run(Local<int> values)
        {
            values.Access(Run);
        }
    }

    private sealed record AnalyzerEvidence(
        double ColdElapsedMs,
        long ColdAllocatedBytes,
        int ColdDiagnostics,
        double IncrementalElapsedMs,
        long IncrementalAllocatedBytes,
        int IncrementalDiagnostics,
        double CancellationElapsedMs,
        bool CancellationObserved,
        int FullOperationBlocks,
        int ColdOperationBlocks,
        int IncrementalOperationBlocks);

    private sealed record WorkloadEvidence(
        string Name,
        double ElapsedMs,
        double ThroughputPerSecond,
        long ManagedAllocatedBytes,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections,
        long LohBytesDelta,
        long RetainedNativeBytes,
        double CallbackOverheadNanoseconds,
        long PeakResidentBytes);

    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class OperationBlockCounterAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor CounterDescriptor = new(
            "NAMPERF0001",
            "Operation-block counter",
            "Operation-block counter",
            "Performance",
            DiagnosticSeverity.Hidden,
            isEnabledByDefault: true);

        internal int Count => Volatile.Read(ref _count);

        private int _count;

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(CounterDescriptor);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterOperationBlockAction(_ => Interlocked.Increment(ref _count));
        }
    }
}
