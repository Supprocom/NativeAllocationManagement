using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

namespace Supprocom.NativeAllocationManagement.Performance;

// Hosts the actual SDK compiler, not a substitute compilation or result cache.
// Run in an isolated child with its original project working directory and the
// response file produced by MSBuild's ProvideCommandLineArgs output.
internal static class NativeCompilerCostWorker
{
    internal static int Run(string compilerPath, string responsePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(compilerPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(responsePath);
        string compiler = Path.GetFullPath(compilerPath);
        string response = Path.GetFullPath(responsePath);
        CompilerInputIdentity compilerIdentity = Identify(compiler);
        CompilerInputIdentity responseIdentity = Identify(response);
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        TimeSpan cpuBefore = process.TotalProcessorTime;
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        long started = Stopwatch.GetTimestamp();
        CompilerLoadContext context = new(compiler);
        Assembly assembly = context.LoadFromAssemblyPath(compiler);
        MethodInfo entry = assembly.EntryPoint
            ?? throw new InvalidOperationException("The SDK compiler has no entry point.");
        if (entry.ReturnType != typeof(int)
            || entry.GetParameters() is not [{ ParameterType: var argumentType }]
            || argumentType != typeof(string[]))
        {
            throw new InvalidOperationException("The SDK compiler entry point must be Int32 Main(String[]).");
        }

        object? result = entry.Invoke(null, [new[] { "@" + response }]);
        long elapsed = Stopwatch.GetTimestamp() - started;
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        process.Refresh();
        double cpuMilliseconds = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        int exitCode = (int)result!;
        NativeCompilerCostEvidence evidence = new(compilerIdentity, responseIdentity,
            assembly.FullName!, entry.ToString()!, Environment.Version.ToString(),
            Environment.CurrentDirectory,
            Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "unset",
            Environment.GetEnvironmentVariable("DOTNET_TieredPGO") ?? "unset",
            exitCode, allocated, cpuMilliseconds,
            elapsed * 1000d / Stopwatch.Frequency,
            "Actual SDK compiler entry point including load-context construction, assembly loading and invocation; complete process allocations/CPU, not NAM-only allocation. Input hashing and report serialization are outside measurement. CPU includes the final process refresh. Wall time excludes that final refresh.");
        Console.WriteLine(JsonSerializer.Serialize(evidence));
        return exitCode;
    }

    private static CompilerInputIdentity Identify(string path)
    {
        using FileStream input = File.OpenRead(path);
        return new(path, input.Length, Convert.ToHexString(SHA256.HashData(input)));
    }

    private sealed class CompilerLoadContext(string compilerPath)
        : AssemblyLoadContext("NAM isolated SDK compiler", isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(compilerPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            string? path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }
}

internal readonly record struct CompilerInputIdentity(string Path, long Length, string SHA256);

internal sealed record NativeCompilerCostEvidence(
    CompilerInputIdentity Compiler,
    CompilerInputIdentity ResponseFile,
    string CompilerAssembly,
    string EntryPoint,
    string RuntimeVersion,
    string WorkingDirectory,
    string TieredCompilation,
    string TieredPGO,
    int ExitCode,
    long AllocatedBytes,
    double CpuMilliseconds,
    double WallMilliseconds,
    string Scope);
