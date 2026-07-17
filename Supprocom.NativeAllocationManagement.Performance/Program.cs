using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
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
    private static readonly string[] WorkloadNames = ["managed-array", "array-pool", "native-pool", "native-region"];
    private static int _sink;

    private static async Task<int> Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

        if (args is ["--workload", string workloadArgument])
        {
            Console.WriteLine(JsonSerializer.Serialize(MeasureSingleWorkload(workloadArgument)));
            return 0;
        }

        Console.WriteLine("# NativeAllocationManagement V1 performance evidence");
        Console.WriteLine();
        Console.WriteLine("Analyzer measurements retain a representative eight-project graph. Workload measurements run one workload per child process so GC, LOH, resident-memory, and native baselines are isolated.");
        Console.WriteLine();

        AnalyzerEvidence analyzer = await MeasureAnalyzerAsync();
        Console.WriteLine("## Analyzer measurements");
        Console.WriteLine();
        Console.WriteLine("| case | elapsedMs | allocatedBytes | diagnostics | projects analyzed | operation blocks | cancellation |");
        Console.WriteLine("| --- | ---: | ---: | ---: | ---: | ---: | --- |");
        Console.WriteLine($"| cold | {analyzer.ColdElapsedMs:F2} | {analyzer.ColdAllocatedBytes} | {analyzer.ColdDiagnostics} | {analyzer.ColdProjectsAnalyzed} | {analyzer.ColdOperationBlocks} | no |");
        Console.WriteLine($"| changed-project incremental | {analyzer.IncrementalElapsedMs:F2} | {analyzer.IncrementalAllocatedBytes} | {analyzer.IncrementalDiagnostics} | {analyzer.IncrementalProjectsAnalyzed} of {analyzer.TotalProjects} | {analyzer.IncrementalOperationBlocks} of {analyzer.FullOperationBlocks} | no |");
        Console.WriteLine($"| cancellation | {analyzer.CancellationElapsedMs:F2} | n/a | n/a | n/a | n/a | {analyzer.CancellationObserved} |");
        Console.WriteLine();
        Console.WriteLine($"Representative graph: {analyzer.TotalProjects} retained project models in a reference chain, {analyzer.FullOperationBlocks} cold method blocks, and project {analyzer.ChangedProjectId} edited at the leaf. The incremental scheduler reused {analyzer.RetainedProjects} unchanged project analyses and scheduled exactly {analyzer.IncrementalProjectsAnalyzed} project.");
        Console.WriteLine();

        Console.WriteLine("## Isolated workload measurements");
        Console.WriteLine();
        Console.WriteLine("| workload | elapsedMs | throughputPerSecond | managedAllocatedBytes | gen0 | gen1 | gen2 | lohBytesBeforeForcedGc | lohBytesAfterForcedGc | nativeBytesBefore | peakNativeBytes | finalNativeBytes | directBoundaryNs | accessBoundaryNs | readBoundaryNs | callbackBodyNs | boundaryMinusBodyNs | peakResidentBytes |");
        Console.WriteLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (string workloadName in WorkloadNames)
        {
            WorkloadEvidence workload = await MeasureIsolatedWorkloadAsync(workloadName);
            Console.WriteLine($"| {workload.Name} | {workload.ElapsedMs:F2} | {workload.ThroughputPerSecond:F2} | {workload.ManagedAllocatedBytes} | {workload.Gen0Collections} | {workload.Gen1Collections} | {workload.Gen2Collections} | {workload.LohBytesBeforeForcedGc} | {workload.LohBytesAfterForcedGc} | {workload.NativeBytesBefore} | {workload.PeakNativeBytes} | {workload.FinalNativeBytes} | {workload.DirectBoundaryNanoseconds:F2} | {workload.AccessBoundaryNanoseconds:F2} | {workload.ReadBoundaryNanoseconds:F2} | {workload.CallbackBodyNanoseconds:F2} | {workload.BoundaryMinusBodyNanoseconds:F2} | {workload.PeakResidentBytes} |");
        }

        GC.KeepAlive(_sink);
        return 0;
    }

    private static async Task<AnalyzerEvidence> MeasureAnalyzerAsync()
    {
        RepresentativeSolution solution = CreateRepresentativeSolution();
        int fullOperationBlocks = CorpusProjects * MethodsPerProject;

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        long coldAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch coldClock = Stopwatch.StartNew();
        OperationBlockCounterAnalyzer coldCounter = new();
        ProjectAnalysis[] coldResults = await Task.WhenAll(
            solution.Projects.Select(project => AnalyzeProjectAsync(project, CancellationToken.None, coldCounter)));
        coldClock.Stop();
        long coldAllocated = GC.GetTotalAllocatedBytes(precise: true) - coldAllocatedBefore;

        ProjectModel changedProject = solution.Projects[^1];
        SyntaxTree editedTree = CSharpSyntaxTree.ParseText(
            CreateCorpusSource(changedProject.Id).Replace("value[0] = 0;", "value[0] = 1;", StringComparison.Ordinal),
            new CSharpParseOptions(LanguageVersion.Preview),
            $"Project{changedProject.Id}.cs");
        ProjectModel editedModel = changedProject with
        {
            Tree = editedTree,
            Compilation = changedProject.Compilation.ReplaceSyntaxTree(changedProject.Tree, editedTree)
        };

        Dictionary<int, ProjectAnalysis> retainedAnalyses = coldResults.ToDictionary(result => result.ProjectId);
        retainedAnalyses.Remove(changedProject.Id);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        long incrementalAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch incrementalClock = Stopwatch.StartNew();
        OperationBlockCounterAnalyzer incrementalCounter = new();
        ProjectAnalysis incrementalResult = await AnalyzeProjectAsync(editedModel, CancellationToken.None, incrementalCounter);
        incrementalClock.Stop();
        long incrementalAllocated = GC.GetTotalAllocatedBytes(precise: true) - incrementalAllocatedBefore;
        _ = retainedAnalyses;

        CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Stopwatch cancellationClock = Stopwatch.StartNew();
        bool cancellationObserved;
        try
        {
            _ = await AnalyzeAsync(changedProject.Compilation, cancellation.Token);
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
            coldResults.Sum(result => result.Diagnostics.Length),
            incrementalClock.Elapsed.TotalMilliseconds,
            incrementalAllocated,
            incrementalResult.Diagnostics.Length,
            cancellationClock.Elapsed.TotalMilliseconds,
            cancellationObserved,
            solution.Projects.Count,
            solution.Projects.Count - 1,
            changedProject.Id,
            fullOperationBlocks,
            coldCounter.Count,
            incrementalCounter.Count);
    }

    private static async Task<ProjectAnalysis> AnalyzeProjectAsync(
        ProjectModel project,
        CancellationToken cancellationToken,
        OperationBlockCounterAnalyzer counter)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(project.Compilation, cancellationToken, counter);
        return new ProjectAnalysis(project.Id, diagnostics);
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(
        CSharpCompilation compilation,
        CancellationToken cancellationToken,
        params DiagnosticAnalyzer[] additionalAnalyzers)
    {
        DiagnosticAnalyzer[] analyzers = [new NativeAllocationAnalyzer(), .. additionalAnalyzers];
        CompilationWithAnalyzers analyzed = compilation.WithAnalyzers(ImmutableArray.CreateRange(analyzers));
        return await analyzed.GetAnalyzerDiagnosticsAsync(cancellationToken);
    }

    private static RepresentativeSolution CreateRepresentativeSolution()
    {
        List<ProjectModel> projects = [];
        MetadataReference[] commonReferences =
        [
            .. GetTrustedPlatformReferences(),
            MetadataReference.CreateFromFile(typeof(NativePool<int>).Assembly.Location)
        ];

        for (int project = 0; project < CorpusProjects; project++)
        {
            SyntaxTree tree = CSharpSyntaxTree.ParseText(
                CreateCorpusSource(project),
                new CSharpParseOptions(LanguageVersion.Preview),
                $"Project{project}.cs");
            List<MetadataReference> references = [.. commonReferences];
            if (projects.Count > 0)
            {
                references.Add(projects[^1].Compilation.ToMetadataReference());
            }

            CSharpCompilation compilation = CSharpCompilation.Create(
                $"NativeAllocationPerformanceProject{project}",
                [tree],
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
            projects.Add(new ProjectModel(project, compilation, tree));
        }

        return new RepresentativeSolution(projects);
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

    private static async Task<WorkloadEvidence> MeasureIsolatedWorkloadAsync(string workloadName)
    {
        string processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The performance process path was unavailable.");
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = processPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            process.StartInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
        }

        process.StartInfo.ArgumentList.Add("--workload");
        process.StartInfo.ArgumentList.Add(workloadName);
        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start isolated workload '{workloadName}'.");
        }

        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        Task exitTask = process.WaitForExitAsync();
        if (await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(90))) != exitTask)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"Isolated workload '{workloadName}' exceeded the 90 second limit.");
        }

        string output = await outputTask;
        string error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Isolated workload '{workloadName}' failed with exit code {process.ExitCode}: {error}");
        }

        string json = output
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault()
            ?? throw new InvalidDataException($"Isolated workload '{workloadName}' produced no evidence. stderr: {error}");
        return JsonSerializer.Deserialize<WorkloadEvidence>(json)
            ?? throw new InvalidDataException($"Isolated workload '{workloadName}' produced invalid JSON: {json}");
    }

    private static WorkloadEvidence MeasureSingleWorkload(string name)
    {
        Action<CallbackMeasurement> workload = name switch
        {
            "managed-array" => RunManagedArray,
            "array-pool" => RunArrayPool,
            "native-pool" => RunNativePool,
            "native-region" => RunNativeRegion,
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown workload.")
        };

        NativeMemoryTestHooks.Reset();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        long managedBefore = GC.GetTotalAllocatedBytes(precise: true);
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        long lohBefore = GetLohBytesAfterForcedGc();
        NativeMemoryTestMetrics nativeBefore = NativeMemoryTestHooks.Snapshot();
        long peakResident = Process.GetCurrentProcess().WorkingSet64;
        CallbackMeasurement callback = new(peakResident, nativeBefore.OutstandingNativeBytes);

        Stopwatch clock = Stopwatch.StartNew();
        workload(callback);
        clock.Stop();

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        long managedAllocated = GC.GetTotalAllocatedBytes(precise: true) - managedBefore;
        NativeMemoryTestMetrics nativeAfter = NativeMemoryTestHooks.Snapshot();
        long lohAfter = GetLohBytesAfterForcedGc();
        return new WorkloadEvidence(
            name,
            clock.Elapsed.TotalMilliseconds,
            WorkloadIterations / Math.Max(clock.Elapsed.TotalSeconds, double.Epsilon),
            managedAllocated,
            GC.CollectionCount(0) - gen0Before,
            GC.CollectionCount(1) - gen1Before,
            GC.CollectionCount(2) - gen2Before,
            lohBefore,
            lohAfter,
            nativeBefore.OutstandingNativeBytes,
            Math.Max(nativeAfter.OutstandingNativeBytes, callback.PeakNativeBytes),
            nativeAfter.OutstandingNativeBytes,
            callback.DirectBoundaryNanoseconds,
            callback.AccessBoundaryNanoseconds,
            callback.ReadBoundaryNanoseconds,
            callback.BodyNanoseconds,
            callback.BoundaryMinusBodyNanoseconds,
            callback.PeakResidentBytes);
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

    private static long GetLohBytesAfterForcedGc()
    {
        ReadOnlySpan<GCGenerationInfo> generations = GC.GetGCMemoryInfo().GenerationInfo;
        return generations.Length > 3 ? generations[3].SizeAfterBytes : -1;
    }

    private sealed class CallbackMeasurement
    {
        internal CallbackMeasurement(long initialResidentBytes, long initialNativeBytes)
        {
            PeakResidentBytes = initialResidentBytes;
            PeakNativeBytes = initialNativeBytes;
        }

        internal long Count { get; private set; }
        internal long BodyElapsedTicks { get; private set; }
        internal long BoundaryElapsedTicks { get; private set; }
        internal long AccessBoundaryTicks { get; private set; }
        internal long ReadBoundaryTicks { get; private set; }
        internal long DirectBoundaryTicks { get; private set; }
        internal long DirectCount { get; private set; }
        internal long AccessCount { get; private set; }
        internal long ReadCount { get; private set; }
        internal long PeakNativeBytes { get; private set; }
        internal long PeakResidentBytes { get; private set; }

        internal double DirectBoundaryNanoseconds => Nanoseconds(DirectBoundaryTicks, DirectCount);
        internal double AccessBoundaryNanoseconds => Nanoseconds(AccessBoundaryTicks, AccessCount);
        internal double ReadBoundaryNanoseconds => Nanoseconds(ReadBoundaryTicks, ReadCount);
        internal double BodyNanoseconds => Nanoseconds(BodyElapsedTicks, Count);
        internal double BoundaryMinusBodyNanoseconds => Nanoseconds(BoundaryElapsedTicks, Count) - BodyNanoseconds;

        internal void Run(Span<int> values)
        {
            long start = Stopwatch.GetTimestamp();
            RunBody(values);
            CompleteBoundary(start, operation: 0);
        }

        internal void Run(Pooled<int> values)
        {
            long start = Stopwatch.GetTimestamp();
            if ((Count & 1) == 0)
            {
                values.Access(RunBody);
                CompleteBoundary(start, operation: 1);
            }
            else
            {
                _ = values.Read<int>(RunReadBody);
                CompleteBoundary(start, operation: 2);
            }
        }

        internal void Run(Local<int> values)
        {
            long start = Stopwatch.GetTimestamp();
            if ((Count & 1) == 0)
            {
                values.Access(RunBody);
                CompleteBoundary(start, operation: 1);
            }
            else
            {
                _ = values.Read<int>(RunReadBody);
                CompleteBoundary(start, operation: 2);
            }
        }

        private void RunBody(Span<int> values)
        {
            long start = Stopwatch.GetTimestamp();
            if (values.Length > 0)
            {
                values[0] = values.Length;
                Volatile.Write(ref _sink, values[0]);
            }

            BodyElapsedTicks += Stopwatch.GetTimestamp() - start;
        }

        private int RunReadBody(ReadOnlySpan<int> values)
        {
            long start = Stopwatch.GetTimestamp();
            int value = values.Length == 0 ? 0 : values[0];
            Volatile.Write(ref _sink, value);
            BodyElapsedTicks += Stopwatch.GetTimestamp() - start;
            return value;
        }

        private void CompleteBoundary(long start, int operation)
        {
            long elapsed = Stopwatch.GetTimestamp() - start;
            BoundaryElapsedTicks += elapsed;
            if (operation == 1)
            {
                AccessBoundaryTicks += elapsed;
                AccessCount++;
            }
            else if (operation == 2)
            {
                ReadBoundaryTicks += elapsed;
                ReadCount++;
            }
            else
            {
                DirectBoundaryTicks += elapsed;
                DirectCount++;
            }

            Count++;
            NoteCurrentResources();
        }

        private void NoteCurrentResources()
        {
            NativeMemoryTestMetrics metrics = NativeMemoryTestHooks.Snapshot();
            PeakNativeBytes = Math.Max(PeakNativeBytes, metrics.OutstandingNativeBytes);
            if (Count == 1 || Count % 128 == 0)
            {
                PeakResidentBytes = Math.Max(PeakResidentBytes, Process.GetCurrentProcess().WorkingSet64);
            }
        }

        private static double Nanoseconds(long ticks, long count)
        {
            return count == 0
                ? 0
                : ticks * 1_000_000_000d / Stopwatch.Frequency / count;
        }
    }

    private sealed class RepresentativeSolution
    {
        internal RepresentativeSolution(IReadOnlyList<ProjectModel> projects)
        {
            Projects = projects;
        }

        internal IReadOnlyList<ProjectModel> Projects { get; }
    }

    private sealed record ProjectModel(int Id, CSharpCompilation Compilation, SyntaxTree Tree);

    private sealed record ProjectAnalysis(int ProjectId, ImmutableArray<Diagnostic> Diagnostics);

    private sealed record AnalyzerEvidence(
        double ColdElapsedMs,
        long ColdAllocatedBytes,
        int ColdDiagnostics,
        double IncrementalElapsedMs,
        long IncrementalAllocatedBytes,
        int IncrementalDiagnostics,
        double CancellationElapsedMs,
        bool CancellationObserved,
        int TotalProjects,
        int RetainedProjects,
        int ChangedProjectId,
        int FullOperationBlocks,
        int ColdOperationBlocks,
        int IncrementalOperationBlocks)
    {
        internal int ColdProjectsAnalyzed => TotalProjects;
        internal int IncrementalProjectsAnalyzed => 1;
    }

    private sealed record WorkloadEvidence(
        string Name,
        double ElapsedMs,
        double ThroughputPerSecond,
        long ManagedAllocatedBytes,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections,
        long LohBytesBeforeForcedGc,
        long LohBytesAfterForcedGc,
        long NativeBytesBefore,
        long PeakNativeBytes,
        long FinalNativeBytes,
        double DirectBoundaryNanoseconds,
        double AccessBoundaryNanoseconds,
        double ReadBoundaryNanoseconds,
        double CallbackBodyNanoseconds,
        double BoundaryMinusBodyNanoseconds,
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

        private int _count;

        internal int Count => Volatile.Read(ref _count);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(CounterDescriptor);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterOperationBlockAction(_ => Interlocked.Increment(ref _count));
        }
    }
}
