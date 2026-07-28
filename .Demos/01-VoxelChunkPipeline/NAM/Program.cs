using Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;
using System.Globalization;
using System.Text;

namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.NAM;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            Console.InputEncoding = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false);
            Console.OutputEncoding = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false);
            if (args is ["--server"])
            {
                return PressureProtocolServer.Run(
                    new WorkerLocalPressureSession(
                        "NAM",
                        static () =>
                            new NativePressureSession(),
                        maximumWorkerCount:
                            GetMaximumWorkerCount()));
            }

            Console.Error.WriteLine("Usage: VoxelChunkPipeline.NAM --server");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 2;
        }
    }

    private static int GetMaximumWorkerCount()
    {
        string? diagnosticValue =
            Environment.GetEnvironmentVariable(
                "NAM_DIAGNOSTIC_WORKER_COUNT");
        if (diagnosticValue is null)
        {
            return Math.Max(
                1,
                Environment.ProcessorCount * 5 / 6);
        }

        if (!int.TryParse(
                diagnosticValue,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int workerCount)
            || workerCount <= 0
            || workerCount > Environment.ProcessorCount)
        {
            throw new InvalidOperationException(
                "NAM_DIAGNOSTIC_WORKER_COUNT must select an available positive worker count.");
        }

        return workerCount;
    }
}
