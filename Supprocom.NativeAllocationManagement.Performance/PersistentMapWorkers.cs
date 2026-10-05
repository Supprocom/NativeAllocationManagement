using System.Runtime.ExceptionServices;

namespace Supprocom.NativeAllocationManagement.Performance;

internal sealed class PersistentMapWorkers : IDisposable
{
    private static readonly TimeSpan WorkerTimeout =
        TimeSpan.FromSeconds(10);

    private readonly int _mapCount;
    private readonly int _workerCount;
    private readonly Thread[] _threads;
    private readonly ExceptionDispatchInfo?[] _failures;
    private readonly SemaphoreSlim[] _starts;
    private readonly ManualResetEventSlim _complete;
    private readonly CountdownEvent _ready;
    private readonly TimeSpan _startupTimeout;
    private readonly TimeSpan _phaseTimeout;
    private readonly TimeSpan _joinTimeout;
    private readonly Action<int>? _onWorkerStop;
    private readonly Action? _onWorkersStopped;
    private readonly Action<int>? _beforeReady;
    private Action<int, int>? _action;
    private int _remaining;
    private int _stopping;
    private int _disposed;
    private int _createdStarts;
    private int _startedWorkers;
    private int _liveWorkers;
    private int _wakeComplete;
    private int _resourceState;
    private int _cleanupFailures;
    private ExceptionDispatchInfo? _terminalFailure;

    internal PersistentMapWorkers(
        int workerCount,
        int mapCount,
        Action<int>? onWorkerStop = null,
        Action? onWorkersStopped = null)
        : this(workerCount, mapCount, WorkerTimeout, WorkerTimeout,
            WorkerTimeout, onWorkerStop, null, null, onWorkersStopped)
    {
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "Failed startup must stop every started worker and preserve the original exception together with any cleanup failure.")]
    internal PersistentMapWorkers(
        int workerCount,
        int mapCount,
        TimeSpan startupTimeout,
        TimeSpan phaseTimeout,
        TimeSpan joinTimeout,
        Action<int>? onWorkerStop,
        Action<PersistentMapWorkers, int>? beforeStart,
        Action<int>? beforeReady,
        Action? onWorkersStopped = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            workerCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            mapCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            workerCount,
            mapCount);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(startupTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(phaseTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(joinTimeout, TimeSpan.Zero);

        _mapCount = mapCount;
        _workerCount = workerCount;
        _threads = new Thread[workerCount];
        _failures = new ExceptionDispatchInfo?[workerCount];
        _starts = new SemaphoreSlim[workerCount];
        _complete = new ManualResetEventSlim(false);
        _ready = new CountdownEvent(workerCount);
        _startupTimeout = startupTimeout;
        _phaseTimeout = phaseTimeout;
        _joinTimeout = joinTimeout;
        _onWorkerStop = onWorkerStop;
        _onWorkersStopped = onWorkersStopped;
        _beforeReady = beforeReady;
        try
        {
            for (int workerIndex = 0;
                workerIndex < workerCount;
                workerIndex++)
            {
                _starts[workerIndex] = new SemaphoreSlim(0, 1);
                _createdStarts++;
                beforeStart?.Invoke(this, workerIndex);
                int capturedIndex = workerIndex;
                Thread thread = new(() => RunWorker(capturedIndex))
                {
                    IsBackground = true,
                    Name = $"NAM performance worker {workerIndex}"
                };
                _threads[workerIndex] = thread;
                Interlocked.Increment(ref _liveWorkers);
                try { thread.Start(); }
                catch
                {
                    Interlocked.Decrement(ref _liveWorkers);
                    throw;
                }
                _startedWorkers++;
            }

            if (!_ready.Wait(_startupTimeout))
            {
                throw new TimeoutException(
                    "The persistent performance workers did not start within the declared deadline.");
            }
            foreach (ExceptionDispatchInfo? failure in _failures) failure?.Throw();
        }
        catch (Exception exception)
        {
            RequestStop();
            Exception? cleanup = JoinWorkers();
            if (cleanup is not null)
                throw new AggregateException("Worker startup failed and cleanup remains unsuccessful.", exception, cleanup);
            throw;
        }
    }

    internal int LiveWorkerCount => Volatile.Read(ref _liveWorkers);
    internal bool ResourcesReleased => Volatile.Read(ref _resourceState) == 2;

    internal void Run(Action<int, int> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        Array.Clear(_failures);
        _complete.Reset();
        Volatile.Write(ref _action, action);
        Volatile.Write(ref _remaining, _workerCount);
        foreach (SemaphoreSlim start in _starts)
        {
            start.Release();
        }

        if (!_complete.Wait(_phaseTimeout))
        {
            RequestStop();
            throw new TimeoutException(
                "The persistent performance worker phase exceeded the declared deadline.");
        }

        Volatile.Write(ref _action, null);
        foreach (ExceptionDispatchInfo? failure in _failures)
        {
            failure?.Throw();
        }
    }

    public void Dispose()
    {
        RequestStop();
        Exception? cleanup = JoinWorkers();
        if (cleanup is not null) ExceptionDispatchInfo.Capture(cleanup).Throw();
    }

    private void RequestStop()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Volatile.Write(ref _stopping, 1);
        foreach (ref readonly SemaphoreSlim start in _starts.AsSpan(0, _createdStarts))
        {
            // A queued phase wake already suffices: the next wake observes stop.
            try { start.Release(); }
            catch (SemaphoreFullException) { }
        }
        Volatile.Write(ref _wakeComplete, 1);
        TryReleaseResources();
    }

    private Exception? JoinWorkers()
    {
        List<Exception>? failures = null;
        for (int index = 0; index < _startedWorkers; index++)
        {
            if (!_threads[index].Join(_joinTimeout))
                (failures ??= []).Add(new TimeoutException(
                    $"Persistent worker {index} did not stop within the declared deadline."));
        }
        TryReleaseResources();
        if (Volatile.Read(ref _cleanupFailures) != 0)
        {
            foreach (ExceptionDispatchInfo? failure in _failures)
                if (failure is not null) (failures ??= []).Add(failure.SourceException);
        }
        if (_terminalFailure is not null) (failures ??= []).Add(_terminalFailure.SourceException);
        return failures switch
        {
            null => null,
            { Count: 1 } => failures[0],
            _ => new AggregateException("Persistent worker cleanup failures.", failures)
        };
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "Terminal callback errors must be retained while every synchronization primitive is still released; no exception may escape a worker finally.")]
    private void TryReleaseResources()
    {
        if (Volatile.Read(ref _wakeComplete) == 0 || Volatile.Read(ref _liveWorkers) != 0 ||
            Interlocked.CompareExchange(ref _resourceState, 1, 0) != 0) return;
        try { _onWorkersStopped?.Invoke(); }
        catch (Exception exception) { _terminalFailure = ExceptionDispatchInfo.Capture(exception); }
        foreach (ref readonly SemaphoreSlim start in _starts.AsSpan(0, _createdStarts)) start.Dispose();
        _ready.Dispose();
        _complete.Dispose();
        Volatile.Write(ref _resourceState, 2);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "This boundary captures any failure to preserve cleanup and report the original error.")]
    private void RunWorker(int workerIndex)
    {
        try
        {
            try { _beforeReady?.Invoke(workerIndex); }
            catch (Exception exception) { _failures[workerIndex] = ExceptionDispatchInfo.Capture(exception); }
            _ready.Signal();
            while (true)
            {
                _starts[workerIndex].Wait();
                if (Volatile.Read(ref _stopping) != 0) return;

                try
                {
                    Action<int, int> action = Volatile.Read(ref _action)
                        ?? throw new InvalidOperationException("The persistent worker has no operation.");
                    for (int mapIndex = workerIndex; mapIndex < _mapCount; mapIndex += _workerCount)
                        action(workerIndex, mapIndex);
                }
                catch (Exception exception)
                {
                    _failures[workerIndex] = ExceptionDispatchInfo.Capture(exception);
                }
                finally
                {
                    if (Interlocked.Decrement(ref _remaining) == 0) _complete.Set();
                }
            }
        }
        finally
        {
            try { _onWorkerStop?.Invoke(workerIndex); }
            catch (Exception exception)
            {
                Exception? original = _failures[workerIndex]?.SourceException;
                _failures[workerIndex] = ExceptionDispatchInfo.Capture(original is null ? exception :
                    new AggregateException("Worker operation and cleanup both failed.", original, exception));
                Interlocked.Increment(ref _cleanupFailures);
            }
            Interlocked.Decrement(ref _liveWorkers);
            TryReleaseResources();
        }
    }
}
