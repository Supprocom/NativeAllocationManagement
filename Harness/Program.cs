namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.Harness;

internal static class Program
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "The process entry point must report arbitrary harness failures and return a nonzero exit code.")]
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Contains("--compile-gate", StringComparer.Ordinal))
            {
                return await CompilationGateHarness.RunAsync(args).ConfigureAwait(false);
            }

            if (args.Contains("--pressure-matrix", StringComparer.Ordinal))
            {
                return await PressureMatrixHarness.RunAsync(args).ConfigureAwait(false);
            }

            if (args.Contains(
                    "--sustained-diagnostic",
                    StringComparer.Ordinal))
            {
                return await PressureMatrixHarness
                    .RunSustainedDiagnosticAsync(args).ConfigureAwait(false);
            }

            await Console.Error.WriteLineAsync(
                "Specify --compile-gate, --pressure-matrix, or --sustained-diagnostic.").ConfigureAwait(false);
            return 2;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(exception.ToString()).ConfigureAwait(false);
            return 2;
        }
    }
}
