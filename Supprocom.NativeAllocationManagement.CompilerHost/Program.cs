using Supprocom.NativeAllocationManagement.Performance;

namespace Supprocom.NativeAllocationManagement.CompilerHost;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args is ["--compiler-cost-worker", "--compiler", string compiler, "--response", string response])
        {
            return NativeCompilerCostWorker.Run(compiler, response);
        }

        throw new ArgumentException("Expected --compiler-cost-worker --compiler <actual SDK csc.dll> --response <actual MSBuild response>.", nameof(args));
    }
}
