using Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;
using System.Text;

namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SafeCSharp;

internal static class Program
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "The process entry point must report any worker startup failure and exit with a protocol error code.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "PressureProtocolServer.Run takes ownership of the session and disposes it in its finally block.")]
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
                        "SafeCSharp",
                        static () =>
                            new SafePressureSession(),
                        maximumWorkerCount:
                            Environment.ProcessorCount));
            }

            Console.Error.WriteLine("Usage: VoxelChunkPipeline.SafeCSharp --server");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 2;
        }
    }
}
