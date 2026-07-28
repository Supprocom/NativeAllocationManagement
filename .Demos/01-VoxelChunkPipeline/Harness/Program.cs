namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.Harness;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--compile-gate", StringComparer.Ordinal))
            {
                return CompilationGateHarness.RunAsync(args)
                    .GetAwaiter()
                    .GetResult();
            }

            if (args.Contains("--pressure-matrix", StringComparer.Ordinal))
            {
                return PressureMatrixHarness.RunAsync(args)
                    .GetAwaiter()
                    .GetResult();
            }

            if (args.Contains(
                    "--sustained-diagnostic",
                    StringComparer.Ordinal))
            {
                return PressureMatrixHarness
                    .RunSustainedDiagnosticAsync(args)
                    .GetAwaiter()
                    .GetResult();
            }

            Console.Error.WriteLine(
                "Specify --compile-gate, --pressure-matrix, or --sustained-diagnostic.");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 2;
        }
    }
}
