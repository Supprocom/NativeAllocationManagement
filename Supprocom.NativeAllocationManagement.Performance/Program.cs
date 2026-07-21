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
    private const int WorkloadIterations = 500;
    private const int CoordinateElements = 8_192;
    private const int VoxelElements = 4_096;
    private const int UploadElements = 16_384;
    private const int WorkloadOperationsPerIteration = 3;
    private static readonly string[] WorkloadNames = ["managed-array", "array-pool", "native-pool", "native-region", "native-arena"];
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
        Console.WriteLine($"Representative graph: {analyzer.TotalProjects} versioned project models in a reference chain, {analyzer.FullOperationBlocks} cold method blocks with 2 NAM-heavy and 10 ordinary methods per project, and heavy method 0 in project {analyzer.ChangedProjectId} edited. The retained cache hit projects [{analyzer.CacheHitProjectIds}], invalidated and reanalyzed projects [{analyzer.ReanalyzedProjectIds}], and the aggregate consumed {analyzer.AggregateOperationBlocks} total blocks ({analyzer.AggregateOperationBlocks - analyzer.IncrementalOperationBlocks} cached, {analyzer.IncrementalOperationBlocks} reanalyzed).");
        Console.WriteLine();

        Console.WriteLine("## Isolated workload measurements");
        Console.WriteLine();
        Console.WriteLine("| workload | elapsedMs | throughputPerSecond | managedAllocatedBytes | gen0 | gen1 | gen2 | lohSizeBeforeForcedGc | lohSizeAfterForcedGc | lohSizeDeltaAfterForcedGc | nativeBytesBefore | peakNativeBytes | peakRetainedNativeBytes | peakRetiredNativeBytes | trimmedNativeBytes | finalRetainedNativeBytes | finalRetiredNativeBytes | finalNativeBytes | directBoundaryNs | accessBoundaryNs | readBoundaryNs | callbackBodyNs | boundaryMinusBodyNs | peakResidentBytes |");
        Console.WriteLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (string workloadName in WorkloadNames)
        {
            WorkloadEvidence workload = await MeasureIsolatedWorkloadAsync(workloadName);
            Console.WriteLine($"| {workload.Name} | {workload.ElapsedMs:F2} | {workload.ThroughputPerSecond:F2} | {workload.ManagedAllocatedBytes} | {workload.Gen0Collections} | {workload.Gen1Collections} | {workload.Gen2Collections} | {workload.LohSizeBeforeForcedGc} | {workload.LohSizeAfterForcedGc} | {workload.LohSizeDeltaAfterForcedGc} | {workload.NativeBytesBefore} | {workload.PeakNativeBytes} | {workload.PeakRetainedNativeBytes} | {workload.PeakRetiredNativeBytes} | {workload.TrimmedNativeBytes} | {workload.FinalRetainedNativeBytes} | {workload.FinalRetiredNativeBytes} | {workload.FinalNativeBytes} | {workload.DirectBoundaryNanoseconds:F2} | {workload.AccessBoundaryNanoseconds:F2} | {workload.ReadBoundaryNanoseconds:F2} | {workload.CallbackBodyNanoseconds:F2} | {workload.BoundaryMinusBodyNanoseconds:F2} | {workload.PeakResidentBytes} |");
        }

        GC.KeepAlive(_sink);
        return 0;
    }

    private static async Task<AnalyzerEvidence> MeasureAnalyzerAsync()
    {
        RepresentativeSolution solution = CreateRepresentativeSolution();

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        long coldAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch coldClock = Stopwatch.StartNew();
        ProjectAnalysis[] coldResults = await Task.WhenAll(
            solution.Projects.Select(project => AnalyzeProjectAsync(project, CancellationToken.None)));
        coldClock.Stop();
        long coldAllocated = GC.GetTotalAllocatedBytes(precise: true) - coldAllocatedBefore;
        int fullOperationBlocks = coldResults.Sum(result => result.OperationBlocks);

        int changedProjectId = CorpusProjects / 2;
        ProjectModel changedProject = solution.Projects[changedProjectId];
        RepresentativeSolution editedSolution = CreateEditedSolution(solution, changedProjectId);
        AnalysisCache cache = new(coldResults);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        long incrementalAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch incrementalClock = Stopwatch.StartNew();
        List<ProjectAnalysis> aggregateResults = [];
        List<int> cacheHitProjects = [];
        List<int> reanalyzedProjects = [];
        foreach (ProjectModel project in editedSolution.Projects)
        {
            if (cache.TryGet(project, out ProjectAnalysis? cached) && cached is not null)
            {
                aggregateResults.Add(cached);
                cacheHitProjects.Add(project.Id);
                continue;
            }

            ProjectAnalysis analyzed = await AnalyzeProjectAsync(project, CancellationToken.None);
            cache.Store(analyzed);
            aggregateResults.Add(analyzed);
            reanalyzedProjects.Add(project.Id);
        }

        int aggregateDiagnostics = aggregateResults.Sum(result => result.Diagnostics.Length);
        int aggregateOperationBlocks = aggregateResults.Sum(result => result.OperationBlocks);
        incrementalClock.Stop();
        long incrementalAllocated = GC.GetTotalAllocatedBytes(precise: true) - incrementalAllocatedBefore;

        int expectedCacheHits = editedSolution.Projects.Count(project =>
            ReferenceEquals(project.Compilation, solution.Projects[project.Id].Compilation));
        int expectedReanalyzed = editedSolution.Projects.Count - expectedCacheHits;
        if (cacheHitProjects.Count != expectedCacheHits
            || reanalyzedProjects.Count != expectedReanalyzed
            || aggregateOperationBlocks != fullOperationBlocks
            || !cacheHitProjects.SequenceEqual(editedSolution.Projects
                .Where(project => ReferenceEquals(project.Compilation, solution.Projects[project.Id].Compilation))
                .Select(project => project.Id))
            || !reanalyzedProjects.SequenceEqual(editedSolution.Projects
                .Where(project => !ReferenceEquals(project.Compilation, solution.Projects[project.Id].Compilation))
                .Select(project => project.Id)))
        {
            throw new InvalidOperationException("The retained incremental cache did not produce the expected identity and aggregate counts.");
        }

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
            aggregateDiagnostics,
            cancellationClock.Elapsed.TotalMilliseconds,
            cancellationObserved,
            solution.Projects.Count,
            changedProjectId,
            fullOperationBlocks,
            coldResults.Sum(result => result.OperationBlocks),
            reanalyzedProjects.Sum(projectId => aggregateResults.First(result => result.ProjectId == projectId).OperationBlocks),
            cacheHitProjects.Count,
            reanalyzedProjects.Count,
            string.Join(",", cacheHitProjects),
            string.Join(",", reanalyzedProjects),
            aggregateOperationBlocks);
    }

    private static async Task<ProjectAnalysis> AnalyzeProjectAsync(
        ProjectModel project,
        CancellationToken cancellationToken)
    {
        OperationBlockCounterAnalyzer counter = new();
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(project.Compilation, cancellationToken, counter);
        return new ProjectAnalysis(project.Id, project.Compilation, diagnostics, counter.Count);
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

    private static RepresentativeSolution CreateEditedSolution(
        RepresentativeSolution original,
        int changedProjectId)
    {
        List<ProjectModel> projects = [];
        MetadataReference[] commonReferences =
        [
            .. GetTrustedPlatformReferences(),
            MetadataReference.CreateFromFile(typeof(NativePool<int>).Assembly.Location)
        ];

        for (int project = 0; project < original.Projects.Count; project++)
        {
            if (project < changedProjectId)
            {
                projects.Add(original.Projects[project]);
                continue;
            }

            SyntaxTree tree = CSharpSyntaxTree.ParseText(
                CreateCorpusSource(project, editHeavyMethod: project == changedProjectId),
                new CSharpParseOptions(LanguageVersion.Preview),
                $"Project{project}.edited.cs");
            List<MetadataReference> references = [.. commonReferences];
            if (projects.Count > 0)
            {
                references.Add(projects[^1].Compilation.ToMetadataReference());
            }

            CSharpCompilation compilation = CSharpCompilation.Create(
                $"NativeAllocationPerformanceProject{project}.Edited",
                [tree],
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
            projects.Add(new ProjectModel(project, compilation, tree));
        }

        return new RepresentativeSolution(projects);
    }

    private static string CreateCorpusSource(int project, bool editHeavyMethod = false)
    {
        StringBuilder source = new();
        source.AppendLine("using Supprocom.NativeAllocationManagement;");
        source.AppendLine($"public static class CorpusProject{project} {{");
        for (int method = 0; method < MethodsPerProject; method++)
        {
            if (method < 2)
            {
                int value = editHeavyMethod && method == 0 ? 1 : 0;
                source.AppendLine($"public static void Method{method}() {{ NativePool<int> pool = new(); Pooled<int> value = pool.Rent(1); value[0] = {value}; value.Dispose(); pool.Dispose(); }}");
            }
            else
            {
                source.AppendLine($"public static int Method{method}() => {project} + {method};");
            }
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
            "native-arena" => RunNativeArena,
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown workload.")
        };

        NativeMemoryTestHooks.Reset();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        long peakResident = Process.GetCurrentProcess().WorkingSet64;
        NativeMemoryTestMetrics initialNative = NativeMemoryTestHooks.Snapshot();
        CallbackMeasurement callback = new(peakResident, initialNative.OutstandingNativeBytes);
        if (name == "native-arena")
        {
            RunNativeArenaSpikeEvidence(callback);
        }

        long managedBefore = GC.GetTotalAllocatedBytes(precise: true);
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        long lohBefore = GetLohSizeAfterForcedGc();
        NativeMemoryTestMetrics nativeBefore = NativeMemoryTestHooks.Snapshot();

        Stopwatch clock = Stopwatch.StartNew();
        workload(callback);
        clock.Stop();

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        long managedAllocated = GC.GetTotalAllocatedBytes(precise: true) - managedBefore;
        NativeMemoryTestMetrics nativeAfter = NativeMemoryTestHooks.Snapshot();
        long lohAfter = GetLohSizeAfterForcedGc();
        return new WorkloadEvidence(
            name,
            clock.Elapsed.TotalMilliseconds,
            WorkloadIterations * WorkloadOperationsPerIteration / Math.Max(clock.Elapsed.TotalSeconds, double.Epsilon),
            managedAllocated,
            GC.CollectionCount(0) - gen0Before,
            GC.CollectionCount(1) - gen1Before,
            GC.CollectionCount(2) - gen2Before,
            lohBefore,
            lohAfter,
            lohAfter - lohBefore,
            nativeBefore.OutstandingNativeBytes,
            Math.Max(nativeAfter.OutstandingNativeBytes, callback.PeakNativeBytes),
            callback.PeakRetainedNativeBytes,
            callback.PeakRetiredNativeBytes,
            callback.TrimmedNativeBytes,
            nativeAfter.RetainedNativeBytes,
            nativeAfter.RetiredNativeBytes,
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
            Coordinate[] coordinates = new Coordinate[CoordinateElements];
            Voxel[] voxels = new Voxel[VoxelElements];
            byte[] upload = new byte[UploadElements];
            callback.Run(coordinates.AsSpan());
            callback.Run(voxels.AsSpan());
            callback.Run(upload.AsSpan());
        }
    }

    private static void RunArrayPool(CallbackMeasurement callback)
    {
        ArrayPool<Coordinate> coordinatePool = ArrayPool<Coordinate>.Shared;
        ArrayPool<Voxel> voxelPool = ArrayPool<Voxel>.Shared;
        ArrayPool<byte> uploadPool = ArrayPool<byte>.Shared;
        for (int iteration = 0; iteration < WorkloadIterations; iteration++)
        {
            Coordinate[] coordinates = coordinatePool.Rent(CoordinateElements);
            Voxel[] voxels = voxelPool.Rent(VoxelElements);
            byte[] upload = uploadPool.Rent(UploadElements);
            try
            {
                callback.Run(coordinates.AsSpan(0, CoordinateElements));
                callback.Run(voxels.AsSpan(0, VoxelElements));
                callback.Run(upload.AsSpan(0, UploadElements));
            }
            finally
            {
                coordinatePool.Return(coordinates, clearArray: true);
                voxelPool.Return(voxels, clearArray: true);
                uploadPool.Return(upload, clearArray: true);
            }
        }
    }

    private static void RunNativePool(CallbackMeasurement callback)
    {
        NativePool<Coordinate> coordinatePool = new(CoordinateElements, NativeMemoryReturn.ToNativeMemory);
        NativePool<Voxel> voxelPool = new(VoxelElements, NativeMemoryReturn.ToNativeMemory);
        NativePool<byte> uploadPool = new(UploadElements, NativeMemoryReturn.ToNativeMemory);
        try
        {
            for (int iteration = 0; iteration < WorkloadIterations; iteration++)
            {
                Pooled<Coordinate> coordinates = coordinatePool.Rent(CoordinateElements);
                Pooled<Voxel> voxels = voxelPool.Rent(VoxelElements);
                Pooled<byte> upload = uploadPool.Rent(UploadElements);
                callback.Run(coordinates);
                callback.Run(voxels);
                callback.Run(upload);
                coordinates.Dispose();
                voxels.Dispose();
                upload.Dispose();
            }
        }
        finally
        {
            coordinatePool.Dispose();
            voxelPool.Dispose();
            uploadPool.Dispose();
        }
    }

    private static void RunNativeRegion(CallbackMeasurement callback)
    {
        for (int iteration = 0; iteration < WorkloadIterations; iteration++)
        {
            nuint reservation = checked(
                (nuint)(CoordinateElements * NativeTypeLayout.StorageSize<Coordinate>()
                    + VoxelElements * NativeTypeLayout.StorageSize<Voxel>()
                    + UploadElements * NativeTypeLayout.StorageSize<byte>()));
            using (NativeRegion region = new(reservation, NativeMemoryReturn.ToNativeMemory))
            {
                Local<Coordinate> coordinates = region.Lease<Coordinate>(CoordinateElements);
                Local<Voxel> voxels = region.Lease<Voxel>(VoxelElements);
                Local<byte> upload = region.Lease<byte>(UploadElements);
                callback.Run(coordinates);
                callback.Run(voxels);
                callback.Run(upload);
            }
        }
    }

    private static void RunNativeArena(CallbackMeasurement callback)
    {
        NativeArena arena = new(
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        try
        {
            for (int iteration = 0; iteration < WorkloadIterations; iteration++)
            {
                ArenaLease<Coordinate> coordinates = arena.Scratch<Coordinate>(CoordinateElements);
                ArenaLease<Voxel> voxels = arena.Scratch<Voxel>(VoxelElements);
                ArenaLease<byte> upload = arena.Scratch<byte>(UploadElements);
                callback.Run(coordinates);
                callback.Run(voxels);
                callback.Run(upload);
                arena.ReleaseLeasesToNativeMemory();
            }
        }
        finally
        {
            arena.Dispose();
        }
    }

    private static void RunNativeArenaSpikeEvidence(CallbackMeasurement callback)
    {
        NativeArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        try
        {
            ArenaLease<byte> firstSpike = arena.Scratch<byte>(3 * 1024 * 1024);
            _ = firstSpike.Length;
            callback.SampleResources();
            arena.ReleaseLeasesToNativeMemory();
            nuint trimmedByBytes = arena.TrimRetainedMemoryByBytes(1);

            ArenaLease<byte> secondSpike = arena.Scratch<byte>(5 * 1024 * 1024);
            _ = secondSpike.Length;
            callback.SampleResources();
            arena.ReleaseLeasesToNativeMemory();
            nuint trimmedByLeaseShape = arena.TrimRetainedMemoryByLeaseSize<byte>(1);
            callback.RecordTrimmedBytes(checked(trimmedByBytes + trimmedByLeaseShape));
        }
        finally
        {
            arena.Dispose();
        }
    }

    private static long GetLohSizeAfterForcedGc()
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
        internal long PeakRetainedNativeBytes { get; private set; }
        internal long PeakRetiredNativeBytes { get; private set; }
        internal long TrimmedNativeBytes { get; private set; }
        internal long PeakResidentBytes { get; private set; }

        internal double DirectBoundaryNanoseconds => Nanoseconds(DirectBoundaryTicks, DirectCount);
        internal double AccessBoundaryNanoseconds => Nanoseconds(AccessBoundaryTicks, AccessCount);
        internal double ReadBoundaryNanoseconds => Nanoseconds(ReadBoundaryTicks, ReadCount);
        internal double BodyNanoseconds => Nanoseconds(BodyElapsedTicks, Count);
        internal double BoundaryMinusBodyNanoseconds => Nanoseconds(BoundaryElapsedTicks, Count) - BodyNanoseconds;

        internal void RecordTrimmedBytes(nuint bytes)
        {
            TrimmedNativeBytes = checked(TrimmedNativeBytes + (long)bytes);
        }

        internal void SampleResources() => NoteCurrentResources();

        internal void Run<T>(Span<T> values)
        {
            long start = Stopwatch.GetTimestamp();
            if ((Count & 1) == 0)
            {
                RunBody(values);
                CompleteBoundary(start, operation: 0);
            }
            else
            {
                _ = RunReadBody((ReadOnlySpan<T>)values);
                CompleteBoundary(start, operation: 0);
            }
        }

        internal void Run<T>(Pooled<T> values)
        {
            long start = Stopwatch.GetTimestamp();
            if ((Count & 1) == 0)
            {
                values.Access(view => RunNativeBody(view));
                CompleteBoundary(start, operation: 1);
            }
            else
            {
                _ = values.Read<int>(view => RunNativeReadBody(view));
                CompleteBoundary(start, operation: 2);
            }
        }

        internal void Run<T>(Local<T> values)
        {
            long start = Stopwatch.GetTimestamp();
            if ((Count & 1) == 0)
            {
                values.Access(view => RunNativeBody(view));
                CompleteBoundary(start, operation: 1);
            }
            else
            {
                _ = values.Read<int>(view => RunNativeReadBody(view));
                CompleteBoundary(start, operation: 2);
            }
        }

        internal void Run<T>(ArenaLease<T> values)
        {
            long start = Stopwatch.GetTimestamp();
            if ((Count & 1) == 0)
            {
                values.Access(view => RunNativeBody(view));
                CompleteBoundary(start, operation: 1);
            }
            else
            {
                _ = values.Read<int>(view => RunNativeReadBody(view));
                CompleteBoundary(start, operation: 2);
            }
        }

        private void RunBody<T>(Span<T> values)
        {
            long start = Stopwatch.GetTimestamp();
            if (values.Length > 0)
            {
                Volatile.Write(ref _sink, values.Length);
            }

            BodyElapsedTicks += Stopwatch.GetTimestamp() - start;
        }

        private int RunReadBody<T>(ReadOnlySpan<T> values)
        {
            long start = Stopwatch.GetTimestamp();
            int value = values.Length;
            Volatile.Write(ref _sink, value);
            BodyElapsedTicks += Stopwatch.GetTimestamp() - start;
            return value;
        }

        private void RunNativeBody<T>(NativeLeaseView<T> values)
        {
            long start = Stopwatch.GetTimestamp();
            Volatile.Write(ref _sink, values.Length);
            BodyElapsedTicks += Stopwatch.GetTimestamp() - start;
        }

        private int RunNativeReadBody<T>(NativeLeaseView<T> values)
        {
            long start = Stopwatch.GetTimestamp();
            int value = values.Length;
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
            PeakRetainedNativeBytes = Math.Max(PeakRetainedNativeBytes, metrics.RetainedNativeBytes);
            PeakRetiredNativeBytes = Math.Max(PeakRetiredNativeBytes, metrics.RetiredNativeBytes);
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

    private readonly struct Coordinate
    {
        internal int X { get; init; }

        internal int Y { get; init; }
    }

    private readonly struct Voxel
    {
        internal long Value { get; init; }

        internal int Material { get; init; }
    }

    private sealed class AnalysisCache
    {
        private readonly Dictionary<int, ProjectAnalysis> _entries = [];

        internal AnalysisCache(IEnumerable<ProjectAnalysis> analyses)
        {
            foreach (ProjectAnalysis analysis in analyses)
            {
                Store(analysis);
            }
        }

        internal void Store(ProjectAnalysis analysis)
        {
            _entries[analysis.ProjectId] = analysis;
        }

        internal bool TryGet(ProjectModel project, out ProjectAnalysis? analysis)
        {
            if (_entries.TryGetValue(project.Id, out ProjectAnalysis? cached)
                && ReferenceEquals(cached.Compilation, project.Compilation))
            {
                analysis = cached;
                return true;
            }

            analysis = null;
            return false;
        }
    }

    private sealed record ProjectModel(int Id, CSharpCompilation Compilation, SyntaxTree Tree);

    private sealed record ProjectAnalysis(
        int ProjectId,
        CSharpCompilation Compilation,
        ImmutableArray<Diagnostic> Diagnostics,
        int OperationBlocks);

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
        int ChangedProjectId,
        int FullOperationBlocks,
        int ColdOperationBlocks,
        int IncrementalOperationBlocks,
        int CacheHitProjects,
        int ReanalyzedProjects,
        string CacheHitProjectIds,
        string ReanalyzedProjectIds,
        int AggregateOperationBlocks)
    {
        internal int ColdProjectsAnalyzed => TotalProjects;
        internal int IncrementalProjectsAnalyzed => ReanalyzedProjects;
    }

    private sealed record WorkloadEvidence(
        string Name,
        double ElapsedMs,
        double ThroughputPerSecond,
        long ManagedAllocatedBytes,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections,
        long LohSizeBeforeForcedGc,
        long LohSizeAfterForcedGc,
        long LohSizeDeltaAfterForcedGc,
        long NativeBytesBefore,
        long PeakNativeBytes,
        long PeakRetainedNativeBytes,
        long PeakRetiredNativeBytes,
        long TrimmedNativeBytes,
        long FinalRetainedNativeBytes,
        long FinalRetiredNativeBytes,
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
