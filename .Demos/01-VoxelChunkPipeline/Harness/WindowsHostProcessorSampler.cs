using System.Runtime.InteropServices;
using Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.Harness;

internal sealed class WindowsHostProcessorSampler : IDisposable
{
    private const uint PdhFormatDouble = 0x00000200;
    private IntPtr _query;
    private readonly IntPtr _processorPerformance;
    private readonly IntPtr _totalCpu;
    private readonly IntPtr _processorQueue;

    internal WindowsHostProcessorSampler()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The processor-state diagnostic requires Windows.");
        }

        ThrowIfFailed(
            PdhOpenQueryW(
                null,
                IntPtr.Zero,
                out _query),
            "open the processor query");
        try
        {
            _processorPerformance = AddCounter(
                @"\Processor Information(_Total)\% Processor Performance");
            _totalCpu = AddCounter(
                @"\Processor(_Total)\% Processor Time");
            _processorQueue = AddCounter(
                @"\System\Processor Queue Length");
            ThrowIfFailed(
                PdhCollectQueryData(_query),
                "collect the processor baseline");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal async Task<PressureHostProcessorSample> SampleAsync(
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(
            TimeSpan.FromSeconds(1),
            cancellationToken);
        ThrowIfFailed(
            PdhCollectQueryData(_query),
            "collect processor data");
        return new PressureHostProcessorSample(
            DateTime.UtcNow,
            ReadValue(_processorPerformance),
            ReadValue(_totalCpu),
            ReadValue(_processorQueue));
    }

    private IntPtr AddCounter(string path)
    {
        ThrowIfFailed(
            PdhAddEnglishCounterW(
                _query,
                path,
                IntPtr.Zero,
                out IntPtr counter),
            $"add counter {path}");
        return counter;
    }

    private static double ReadValue(IntPtr counter)
    {
        ThrowIfFailed(
            PdhGetFormattedCounterValue(
                counter,
                PdhFormatDouble,
                out _,
                out PdhFormattedCounterValue value),
            "read a processor counter");
        return value.DoubleValue;
    }

    private static void ThrowIfFailed(
        uint status,
        string operation)
    {
        if (status != 0)
        {
            throw new InvalidOperationException(
                $"PDH could not {operation}. Status: 0x{status:X8}.");
        }
    }

    public void Dispose()
    {
        IntPtr query = Interlocked.Exchange(
            ref _query,
            IntPtr.Zero);
        if (query != IntPtr.Zero)
        {
            _ = PdhCloseQuery(query);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PdhFormattedCounterValue
    {
        internal readonly uint Status;
        internal readonly double DoubleValue;
    }

    [DllImport(
        "pdh.dll",
        EntryPoint = "PdhOpenQueryW",
        CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(
        string? dataSource,
        IntPtr userData,
        out IntPtr query);

    [DllImport(
        "pdh.dll",
        EntryPoint = "PdhAddEnglishCounterW",
        CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(
        IntPtr query,
        string fullCounterPath,
        IntPtr userData,
        out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(
        IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(
        IntPtr counter,
        uint format,
        out uint counterType,
        out PdhFormattedCounterValue value);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
