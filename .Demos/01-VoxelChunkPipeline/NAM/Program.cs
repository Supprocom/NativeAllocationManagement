using Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;
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
                            Math.Max(
                                1,
                                Environment.ProcessorCount
                                    * 5
                                    / 6)));
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
}
