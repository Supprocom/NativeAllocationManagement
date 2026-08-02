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
    private Action<int, int>? _action;
    private int _remaining;
    private int _stopping;
    private int _disposed;

    internal PersistentMapWorkers(
        int workerCount,
        int mapCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            workerCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            mapCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            workerCount,
            mapCount);

        _mapCount = mapCount;
        _workerCount = workerCount;
        _threads = new Thread[workerCount];
        _failures = new ExceptionDispatchInfo?[workerCount];
        _starts = new SemaphoreSlim[workerCount];
        _complete = new ManualResetEventSlim(false);
        using CountdownEvent ready = new(workerCount);
        for (int workerIndex = 0;
            workerIndex < workerCount;
            workerIndex++)
        {
            _starts[workerIndex] = new SemaphoreSlim(0, 1);
            int capturedIndex = workerIndex;
            Thread thread = new(
                () => RunWorker(capturedIndex, ready))
            {
                IsBackground = true,
                Name = $"NAM performance worker {workerIndex}"
            };
            _threads[workerIndex] = thread;
            thread.Start();
        }

        if (!ready.Wait(WorkerTimeout))
        {
            throw new TimeoutException(
                "The persistent performance workers did not start in ten seconds.");
        }

    }

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

        if (!_complete.Wait(WorkerTimeout))
        {
            throw new TimeoutException(
                "The persistent performance worker phase exceeded ten seconds.");
        }

        Volatile.Write(ref _action, null);
        foreach (ExceptionDispatchInfo? failure in _failures)
        {
            failure?.Throw();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _stopping, 1);
        foreach (SemaphoreSlim start in _starts)
        {
            start.Release();
        }

        foreach (Thread thread in _threads)
        {
            if (!thread.Join(WorkerTimeout))
            {
                throw new TimeoutException(
                    "A persistent performance worker did not stop in ten seconds.");
            }
        }

        foreach (SemaphoreSlim start in _starts)
        {
            start.Dispose();
        }

        _complete.Dispose();
    }

    private void RunWorker(
        int workerIndex,
        CountdownEvent ready)
    {
        ready.Signal();
        while (true)
        {
            _starts[workerIndex].Wait();

            if (Volatile.Read(ref _stopping) != 0)
            {
                return;
            }

            try
            {
                Action<int, int> action = Volatile.Read(
                    ref _action)
                    ?? throw new InvalidOperationException(
                        "The persistent worker has no operation.");
                for (int mapIndex = workerIndex;
                    mapIndex < _mapCount;
                    mapIndex += _workerCount)
                {
                    action(workerIndex, mapIndex);
                }
            }
            catch (Exception exception)
            {
                _failures[workerIndex] =
                    ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                if (Interlocked.Decrement(
                        ref _remaining) == 0)
                {
                    _complete.Set();
                }
            }
        }
    }
}
