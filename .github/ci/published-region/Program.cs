using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Supprocom.NativeAllocationManagement;
using Supprocom.NativeAllocationManagement.Performance;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
if (args is ["--identity"])
{
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        Framework = RuntimeInformation.FrameworkDescription,
        Rid = RuntimeInformation.RuntimeIdentifier,
        Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
        RuntimePath = typeof(NativeRegion).Assembly.Location
    }));
    return 0;
}

if (args.Length != 0) throw new ArgumentException("The fixed Region workload accepts no timing arguments.", nameof(args));
RegionRegressionReport report = AllocatorPerformanceRegression.RunRegion();
string? cpuEvidence = Environment.GetEnvironmentVariable("NAM_REGION_CPU_EVIDENCE");
if (cpuEvidence is not null)
{
    using Process process = Process.GetCurrentProcess();
    File.WriteAllText(cpuEvidence, JsonSerializer.Serialize(new
    {
        ProcessorMilliseconds = process.TotalProcessorTime.TotalMilliseconds,
        ObservedAt = DateTimeOffset.UtcNow,
        Scope = "Actual worker process CPU through complete workload and allocator cleanup, before final evidence emission and process shutdown."
    }));
}
Console.WriteLine(JsonSerializer.Serialize(report));
return report.Passed ? 0 : 3;
