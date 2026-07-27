using System.Collections.Concurrent;

namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

public sealed class WorkerLocalPressureSession : IPressureProfileSession
{
    private const int DefaultMaximumWorkerCount = 4;
    private readonly WorkerSlot[] _workers;
    private PressureChunkPlanEntry[] _canonicalPlan = [];
    private long[] _workerBudgets = [];
    private PressureWorkerCapacity[] _workerCapacities = [];
    private int _activeWorkerCount;
    private int _planSeed;
    private int _disposed;

    public WorkerLocalPressureSession(
        string implementation,
        Func<IPressureProfileSession> workerFactory,
        int maximumWorkerCount = DefaultMaximumWorkerCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            implementation);
        ArgumentNullException.ThrowIfNull(workerFactory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            maximumWorkerCount);
        Implementation = implementation;
        int workerCount = Math.Min(
            maximumWorkerCount,
            Math.Max(1, Environment.ProcessorCount));
        _workers = new WorkerSlot[workerCount];
        try
        {
            for (int index = 0; index < workerCount; index++)
            {
                _workers[index] = new WorkerSlot(
                    workerFactory(),
                    implementation,
                    index);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public string Implementation { get; }

    public PressureProfileResult Run(
        PressureProfileRequest request,
        Action<PressureProgress> reportProgress)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        request.Validate();
        ArgumentNullException.ThrowIfNull(reportProgress);
        PressureChunkPlanEntry[] plan = GetPlan(request);
        EnsureWorkerCapacityPlan(
            request,
            plan);
        WorkerPartition[] partitions = Partition(
            plan,
            _activeWorkerCount);
        using ManualResetEventSlim startGate = new();
        WorkerExecution[] executions = new WorkerExecution[
            partitions.Length];
        try
        {
            for (int index = 0;
                index < partitions.Length;
                index++)
            {
                WorkerPartition partition = partitions[index];
                PressureProfileRequest workerRequest =
                    CreateWorkerRequest(
                        request,
                        partition,
                        partitions.Length,
                        _workerBudgets[index]);
                WorkerExecution execution = new(
                    workerRequest,
                    startGate);
                executions[index] = execution;
                _workers[index].Submit(execution);
            }

            foreach (WorkerExecution execution in executions)
            {
                execution.Ready.Wait();
            }

            PressureRuntimeSnapshot before =
                PressureRuntimeSnapshot.Capture();
            reportProgress(new PressureProgress(
                Implementation,
                request.ProfilePercent,
                PressureProgressKind.ProcessingStarted,
                0,
                0,
                VoxelPipelineStage.None,
                -1));
            startGate.Set();
            foreach (WorkerExecution execution in executions)
            {
                execution.ProcessingCompleted.Wait();
            }

            int completedChunks = executions.Sum(
                static execution =>
                    execution.Progress.CompletedChunks);
            long completedLogicalBytes = executions.Sum(
                static execution =>
                    execution.Progress.CompletedLogicalBytes);
            int lastCompletedChunkId = executions.Max(
                static execution =>
                    execution.Progress.LastCompletedChunkId);
            VoxelPipelineStage lastCompletedStage =
                executions.All(
                    static execution =>
                        execution.Progress.LastCompletedStage
                            == VoxelPipelineStage.Completed)
                ? VoxelPipelineStage.Completed
                : executions
                    .Select(
                        static execution =>
                            execution.Progress.LastCompletedStage)
                    .First(
                        static stage =>
                            stage != VoxelPipelineStage.Completed);
            reportProgress(new PressureProgress(
                Implementation,
                request.ProfilePercent,
                PressureProgressKind.ProcessingCompleted,
                completedChunks,
                completedLogicalBytes,
                lastCompletedStage,
                lastCompletedChunkId));
            foreach (WorkerExecution execution in executions)
            {
                execution.Complete();
            }

            PressureRuntimeSnapshot after =
                PressureRuntimeSnapshot.Capture();
            return Aggregate(
                request,
                plan,
                executions,
                before,
                after);
        }
        finally
        {
            startGate.Set();
            foreach (WorkerExecution? execution in executions)
            {
                execution?.Dispose();
            }
        }
    }

    private PressureChunkPlanEntry[] GetPlan(
        PressureProfileRequest request)
    {
        if (request.HasPlannedChunks)
        {
            throw new ArgumentException(
                "The outer pressure request cannot contain a worker chunk plan.",
                nameof(request));
        }

        int minimumChunks = request.Warmup
            ? request.RetentionDepth
            : 0;
        bool create = _canonicalPlan.Length == 0
            || _planSeed != request.Seed
            || _canonicalPlan.Sum(
                static entry =>
                    entry.LogicalDemandBytes)
                < request.RequestedCumulativeDemandBytes
            || _canonicalPlan.Length < minimumChunks;
        if (create)
        {
            _canonicalPlan =
                PressureWorkContract.CreateCanonicalChunkPlan(
                    request.Seed,
                    request.RequestedCumulativeDemandBytes,
                    minimumChunks);
            _planSeed = request.Seed;
        }

        long realizedDemand = 0;
        int count = 0;
        while (realizedDemand
                    < request.RequestedCumulativeDemandBytes
            || count < minimumChunks
            || count
                % PressureWorkContract.CanonicalPressureCycleLength
                != 0)
        {
            realizedDemand = checked(
                realizedDemand
                + _canonicalPlan[count].LogicalDemandBytes);
            count++;
        }

        return _canonicalPlan[..count];
    }

    private void EnsureWorkerCapacityPlan(
        PressureProfileRequest request,
        IReadOnlyList<PressureChunkPlanEntry> plan)
    {
        if (_activeWorkerCount != 0)
        {
            return;
        }

        if (!request.Warmup)
        {
            throw new InvalidOperationException(
                "The worker set requires a warmup capacity plan.");
        }

        long processReserve = Math.Max(
            16L * 1024 * 1024,
            request.CgroupCapBytes / 8);
        long retainedLimit = checked(
            request.CgroupCapBytes - processReserve);
        if (retainedLimit <= 0)
        {
            throw new OutOfMemoryException(
                "The process memory reserve consumes the complete pressure cap.");
        }

        int maximumCount = Math.Min(
            _workers.Length,
            plan.Count);
        for (int candidate = maximumCount;
            candidate >= 1;
            candidate--)
        {
            WorkerPartition[] partitions = Partition(
                plan,
                candidate);
            PressureWorkerCapacity[] capacities =
                new PressureWorkerCapacity[candidate];
            long selectionBytes = 0;
            bool fits = true;
            for (int index = 0; index < candidate; index++)
            {
                PressureProfileRequest workerRequest =
                    CreateWorkerRequest(
                        request,
                        partitions[index],
                        candidate,
                        retainedLimit);
                try
                {
                    capacities[index] =
                        _workers[index].PlanCapacity(
                            workerRequest);
                    PressureWorkerCapacity capacity =
                        capacities[index];
                    if (capacity.MinimumRetainedBytes <= 0
                        || capacity.SafetyReserveBytes < 0
                        || capacity.PreferredRetainedBytes
                            < capacity.MinimumRetainedBytes)
                    {
                        throw new InvalidOperationException(
                            "A worker capacity plan contains invalid byte counts.");
                    }

                    selectionBytes = checked(
                        selectionBytes
                        + capacity.MinimumRetainedBytes
                        + capacity.SafetyReserveBytes);
                    if (selectionBytes > retainedLimit)
                    {
                        fits = false;
                        break;
                    }
                }
                catch (OutOfMemoryException)
                {
                    fits = false;
                    break;
                }
            }

            if (!fits)
            {
                continue;
            }

            _activeWorkerCount = candidate;
            _workerCapacities = capacities;
            _workerBudgets = AllocateWorkerBudgets(
                capacities,
                retainedLimit,
                selectionBytes);
            return;
        }

        throw new OutOfMemoryException(
            "One worker cannot fit the canonical pressure plan and its safety reserve.");
    }

    private static long[] AllocateWorkerBudgets(
        IReadOnlyList<PressureWorkerCapacity> capacities,
        long retainedLimit,
        long selectionBytes)
    {
        long[] budgets = capacities
            .Select(
                static capacity =>
                    capacity.MinimumRetainedBytes)
            .ToArray();
        long distributable = checked(
            retainedLimit - selectionBytes);
        long totalPreferredGrowth = capacities.Sum(
            static capacity =>
                capacity.PreferredRetainedBytes
                - capacity.MinimumRetainedBytes);
        if (distributable == 0
            || totalPreferredGrowth == 0)
        {
            return budgets;
        }

        long distributed = 0;
        for (int index = 0; index < budgets.Length; index++)
        {
            long need = capacities[index]
                .PreferredRetainedBytes
                - capacities[index]
                    .MinimumRetainedBytes;
            long extra = checked(
                distributable * need
                / totalPreferredGrowth);
            budgets[index] = checked(
                budgets[index] + extra);
            distributed = checked(
                distributed + extra);
        }

        long remainder = distributable - distributed;
        for (int index = 0;
            remainder != 0 && index < budgets.Length;
            index++)
        {
            long need = capacities[index]
                .PreferredRetainedBytes
                - budgets[index];
            if (need <= 0)
            {
                continue;
            }

            long extra = Math.Min(need, remainder);
            budgets[index] = checked(
                budgets[index] + extra);
            remainder -= extra;
        }

        return budgets;
    }

    private static PressureProfileRequest CreateWorkerRequest(
        PressureProfileRequest request,
        WorkerPartition partition,
        int workerCount,
        long workerBudgetBytes)
    {
        int workerRetentionDepth = Math.Max(
            1,
            (request.RetentionDepth
                + workerCount - 1)
            / workerCount);
        return request with
        {
            CgroupCapBytes = workerBudgetBytes,
            RequestedCumulativeDemandBytes =
                partition.LogicalDemandBytes,
            RetentionDepth = workerRetentionDepth,
            ProgressEveryChunks = int.MaxValue,
            PlannedChunks =
                partition.Chunks.ToArray()
        };
    }

    private WorkerPartition[] Partition(
        IReadOnlyList<PressureChunkPlanEntry> plan,
        int maximumWorkerCount)
    {
        int workerCount = Math.Min(
            maximumWorkerCount,
            plan.Count);
        WorkerPartition[] partitions = new WorkerPartition[
            workerCount];
        for (int index = 0; index < workerCount; index++)
        {
            partitions[index] = new WorkerPartition();
        }

        foreach (PressureChunkPlanEntry entry in plan)
        {
            int selected = 0;
            for (int index = 1;
                index < partitions.Length;
                index++)
            {
                if (partitions[index].EstimatedWorkUnits
                        < partitions[selected].EstimatedWorkUnits
                    || (partitions[index].EstimatedWorkUnits
                            == partitions[selected].EstimatedWorkUnits
                        && partitions[index].LogicalDemandBytes
                            < partitions[selected]
                                .LogicalDemandBytes))
                {
                    selected = index;
                }
            }

            partitions[selected].Add(entry);
        }

        return partitions;
    }

    private PressureProfileResult Aggregate(
        PressureProfileRequest request,
        IReadOnlyList<PressureChunkPlanEntry> plan,
        IReadOnlyList<WorkerExecution> executions,
        PressureRuntimeSnapshot before,
        PressureRuntimeSnapshot after)
    {
        WorkerExecution? failedExecution =
            executions.FirstOrDefault(
                static execution =>
                    execution.Failure is not null);
        PressureProfileResult[] results = executions
            .Where(
                static execution =>
                    execution.Result is not null)
            .Select(
                static execution =>
                    execution.Result!.Value)
            .ToArray();
        PressureProfileResult? failedResult = null;
        foreach (PressureProfileResult result in results)
        {
            if (result.Outcome
                != PressureProfileOutcome.Completed)
            {
                failedResult = result;
                break;
            }
        }

        PressureProfileOutcome outcome =
            failedExecution?.Failure is not null
                ? MapFailureOutcome(failedExecution.Failure)
                : failedResult?.Outcome
                    ?? PressureProfileOutcome.Completed;
        PressureChunkEvidence[] evidence = results
            .SelectMany(
                static result =>
                    result.ChunkEvidence)
            .OrderBy(
                static chunk =>
                    chunk.ChunkId)
            .ToArray();
        bool exactPlan = evidence.Length == plan.Count;
        if (exactPlan)
        {
            for (int index = 0;
                index < evidence.Length;
                index++)
            {
                if (evidence[index].ChunkId
                    != plan[index].ChunkId)
                {
                    exactPlan = false;
                    break;
                }
            }
        }

        long realizedDemand = results.Sum(
            static result =>
                result.RealizedCumulativeDemandBytes);
        long peakLiveLogicalBytes = results.Sum(
            static result =>
                result.PeakLiveLogicalBytes);
        bool correctness = outcome
                == PressureProfileOutcome.Completed
            && exactPlan
            && results.Length == executions.Count
            && results.All(
                static result =>
                    result.CorrectnessPassed)
            && realizedDemand
                >= request.RequestedCumulativeDemandBytes;
        Exception? failure = failedExecution?.Failure;
        string? exceptionType =
            failure?.GetType().FullName
            ?? failedResult?.ExceptionType;
        string? exceptionMessage =
            failure?.Message
            ?? failedResult?.ExceptionMessage;
        IReadOnlyList<NativeOwnerProfile>? owners = results.Any(
            static result =>
                result.NativeOwners is not null)
            ? results.SelectMany(
                    static result =>
                        result.NativeOwners
                        ?? [])
                .ToArray()
            : null;
        int lastCompletedChunkId = evidence.Length == 0
            ? -1
            : evidence[^1].ChunkId;
        VoxelPipelineStage lastStage =
            outcome == PressureProfileOutcome.Completed
                ? VoxelPipelineStage.Completed
                : failedResult?.LastCompletedStage
                    ?? VoxelPipelineStage.None;
        return new PressureProfileResult(
            Implementation,
            outcome,
            request.ProfilePercent,
            request.CgroupCapBytes,
            request.RequestedCumulativeDemandBytes,
            realizedDemand,
            Math.Max(
                0,
                realizedDemand
                    - request.RequestedCumulativeDemandBytes),
            request.DeadlineMilliseconds,
            evidence.Length,
            results.Sum(
                static result =>
                    result.CompletedLogicalBytes),
            results.Sum(
                static result =>
                    result.SourceInputBytes),
            peakLiveLogicalBytes,
            peakLiveLogicalBytes == 0
                ? 0
                : realizedDemand
                    / (double)peakLiveLogicalBytes,
            request.RetentionDepth,
            results.Sum(
                static result =>
                    result.PeakRetentionDepth),
            results.Sum(
                static result =>
                    result.AdmissionBudgetBytes),
            results.Sum(
                static result =>
                    result.AdmissionThrottleCount),
            lastStage,
            lastCompletedChunkId,
            correctness,
            PressureWorkContract.ComputeProfileEvidenceHash(
                evidence),
            evidence,
            before,
            after,
            Math.Max(
                0,
                after.TotalAllocatedBytes
                    - before.TotalAllocatedBytes),
            Math.Max(
                0,
                after.Gen0Collections
                    - before.Gen0Collections),
            Math.Max(
                0,
                after.Gen1Collections
                    - before.Gen1Collections),
            Math.Max(
                0,
                after.Gen2Collections
                    - before.Gen2Collections),
            Math.Max(
                0,
                after.TotalPauseMilliseconds
                    - before.TotalPauseMilliseconds),
            results.Sum(
                static result =>
                    result.NativePeakBytes),
            results.Sum(
                static result =>
                    result.NativeRetainedBytes),
            results.Sum(
                static result =>
                    result.NativeFinalBytes),
            results.Sum(
                static result =>
                    result.TypedPhysicalReuseCount),
            results.Sum(
                static result =>
                    result.ScopedPhysicalReuseCount),
            results.Sum(
                static result =>
                    result.ScopedPhysicalReuseBytes),
            owners,
            exceptionType,
            exceptionMessage,
            ActiveWorkerCount: _activeWorkerCount,
            WorkerBudgetBytes: _workerBudgets,
            WorkerCapacities: _workerCapacities);
    }

    private static PressureProfileOutcome MapFailureOutcome(
        Exception failure) =>
        failure is OutOfMemoryException
            ? PressureProfileOutcome.OutOfMemory
            : PressureProfileOutcome.HarnessFailure;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_workers is null)
        {
            return;
        }

        foreach (WorkerSlot? worker in _workers)
        {
            worker?.Dispose();
        }
    }

    private sealed class WorkerPartition
    {
        internal List<PressureChunkPlanEntry> Chunks
        {
            get;
        } = [];

        internal long LogicalDemandBytes { get; private set; }

        internal long EstimatedWorkUnits { get; private set; }

        internal void Add(PressureChunkPlanEntry entry)
        {
            Chunks.Add(entry);
            LogicalDemandBytes = checked(
                LogicalDemandBytes
                + entry.LogicalDemandBytes);
            EstimatedWorkUnits = checked(
                EstimatedWorkUnits
                + entry.EstimatedWorkUnits);
        }
    }

    private sealed class WorkerExecution : IDisposable
    {
        internal WorkerExecution(
            PressureProfileRequest request,
            ManualResetEventSlim startGate)
        {
            Request = request;
            StartGate = startGate;
        }

        internal PressureProfileRequest Request { get; }

        internal ManualResetEventSlim StartGate { get; }

        internal ManualResetEventSlim Ready { get; } = new();

        internal ManualResetEventSlim ProcessingCompleted { get; } =
            new();

        internal ManualResetEventSlim Finished { get; } = new();

        internal PressureProgress Progress { get; set; }

        internal PressureProfileResult? Result { get; set; }

        internal Exception? Failure { get; set; }

        internal Task<PressureProfileResult>? QueuedResult
        {
            get;
            set;
        }

        internal void Complete()
        {
            if (QueuedResult is null)
            {
                Finished.Wait();
                return;
            }

            try
            {
                Result = QueuedResult.GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                Failure = exception;
            }
            finally
            {
                Finished.Set();
            }
        }

        public void Dispose()
        {
            Ready.Dispose();
            ProcessingCompleted.Dispose();
            Finished.Dispose();
        }
    }

    private sealed class WorkerSlot : IDisposable
    {
        private readonly BlockingCollection<WorkerExecution>
            _requests = new();
        private readonly IPressureProfileSession _session;
        private readonly Thread? _thread;
        private int _disposed;

        internal WorkerSlot(
            IPressureProfileSession session,
            string implementation,
            int workerIndex)
        {
            _session = session;
            if (session is IQueuedPressureProfileSession)
            {
                return;
            }

            _thread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"{implementation} allocator worker {workerIndex}"
            };
            _thread.Start();
        }

        internal void Submit(WorkerExecution execution)
        {
            if (_session is IQueuedPressureProfileSession queued)
            {
                try
                {
                    execution.QueuedResult = queued.QueueAsync(
                        execution.Request,
                        progress => ReportProgress(
                            execution,
                            progress));
                }
                catch (Exception exception)
                {
                    execution.Failure = exception;
                    execution.Ready.Set();
                    execution.ProcessingCompleted.Set();
                    execution.Finished.Set();
                }

                return;
            }

            _requests.Add(execution);
        }

        internal PressureWorkerCapacity PlanCapacity(
            PressureProfileRequest request) =>
            _session is IPressureWorkerCapacityPlanner planner
                ? planner.PlanWorkerCapacity(request)
                : new PressureWorkerCapacity(
                    1,
                    0,
                    1);

        private void WorkerLoop()
        {
            foreach (WorkerExecution execution
                in _requests.GetConsumingEnumerable())
            {
                try
                {
                    execution.Result = _session.Run(
                        execution.Request,
                        progress => ReportProgress(
                            execution,
                            progress));
                }
                catch (Exception exception)
                {
                    execution.Failure = exception;
                }
                finally
                {
                    execution.Ready.Set();
                    execution.ProcessingCompleted.Set();
                    execution.Finished.Set();
                }
            }
        }

        private static void ReportProgress(
            WorkerExecution execution,
            PressureProgress progress)
        {
            if (progress.Kind
                == PressureProgressKind.ProcessingStarted)
            {
                execution.Ready.Set();
                execution.StartGate.Wait();
                return;
            }

            if (progress.Kind
                == PressureProgressKind.ProcessingCompleted)
            {
                execution.Progress = progress;
                execution.ProcessingCompleted.Set();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (_thread is not null)
            {
                _requests.CompleteAdding();
                if (!_thread.Join(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException(
                        "A worker-local pressure thread did not stop within ten seconds.");
                }
            }

            _session.Dispose();
            _requests.Dispose();
        }
    }
}
