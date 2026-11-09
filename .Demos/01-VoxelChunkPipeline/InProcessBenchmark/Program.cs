using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;
using NativeSession = Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.NAM.NativePressureSession;
using SafeSession = Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SafeCSharp.SafePressureSession;

// Usage: compare [--rounds 5] [--requests 9] [--demand-mib 256]
//        time <nam|safe> <demandMiB> <timedRequests>
//        verify <nam|safe> <demandMiB> <hexSeed>...
const string Usage = "Usage: compare [--rounds N] [--requests N] [--demand-mib N]"
    + " | time <nam|safe> <demandMiB> <timedRequests>"
    + " | verify <nam|safe> <demandMiB> <hexSeed>...";
const long CapBytes = 256L * 1024 * 1024;
const int WarmupRequests = 4;

try
{
    switch (args.Length > 0 ? args[0] : string.Empty)
    {
        case "compare":
            return Compare(args);
        case "time" when args.Length == 4:
            RunTime(args[1], ParseDemand(args[2]), int.Parse(args[3], CultureInfo.InvariantCulture));
            return 0;
        case "verify" when args.Length >= 4:
            RunVerify(args[1], ParseDemand(args[2]), args[3..]);
            return 0;
        default:
            Console.Error.WriteLine(Usage);
            return 2;
    }
}
catch (Exception exception) when (exception is FormatException or ArgumentException or InvalidOperationException or IOException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static long ParseDemand(string mib) => long.Parse(mib, CultureInfo.InvariantCulture) * 1024 * 1024;

// The caller owns and disposes the returned session.
#pragma warning disable CA2000
static WorkerLocalPressureSession CreateSession(string mode) => mode switch
{
    "nam" => new("NAM", static () => new NativeSession(), maximumWorkerCount: 1),
    "safe" => new("SafeCSharp", static () => new SafeSession(), maximumWorkerCount: 1),
    _ => throw new ArgumentException($"Unknown mode '{mode}'; expected nam or safe.", nameof(mode)),
};
#pragma warning restore CA2000

static PressureProfileRequest CreateRequest(long demand) => new(
    (int)(demand * 100 / CapBytes), CapBytes, demand, 1e9, 0x4E414D, 1, int.MaxValue,
    ExecutionMode: PressureExecutionMode.Measurement);

static PressureProfileRequest WarmupRequest(PressureProfileRequest request, long ordinal) =>
    request with { Warmup = true, RequestedCumulativeDemandBytes = CapBytes * 10, ProfilePercent = 1000, RequestOrdinal = ordinal };

static void Check(PressureProfileResult result)
{
    if (result.Outcome != PressureProfileOutcome.Completed)
    {
        throw new InvalidOperationException($"Outcome {result.Outcome}");
    }
}

static void RunVerify(string mode, long demand, string[] seeds)
{
    foreach (string seedText in seeds)
    {
        // A session's plan is fixed by the seed of its first request, so each seed gets its own session.
        int seed = Convert.ToInt32(seedText, 16);
        using WorkerLocalPressureSession session = CreateSession(mode);
        PressureProfileRequest request = CreateRequest(demand) with { Seed = seed };
        long ordinal = 1;
        Check(session.Run(WarmupRequest(request, ordinal++), static _ => { }));
        PressureProfileResult result = session.Run(
            request with { ExecutionMode = PressureExecutionMode.Verification, RequestOrdinal = ordinal++ },
            static _ => { });
        Check(result);
        StringBuilder text = new();
        foreach (PressureChunkEvidence evidence in result.ChunkEvidence)
        {
            text.AppendLine(evidence.ToString());
        }

        string chunks = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
        Console.WriteLine(FormattableString.Invariant(
            $"{mode} seed {seedText} correct {result.CorrectnessPassed} chunks {result.CompletedChunks} canonical {result.CanonicalEvidenceHash} chunk-evidence {chunks}"));
    }
}

static void RunTime(string mode, long demand, int timed)
{
    using WorkerLocalPressureSession session = CreateSession(mode);
    PressureProfileRequest request = CreateRequest(demand);
    long ordinal = 1;
    for (int i = 0; i < WarmupRequests; i++)
    {
        Check(session.Run(WarmupRequest(request, ordinal++), static _ => { }));
    }

    List<double> times = [];
    List<double> cpu = [];
    for (int i = 0; i < timed; i++)
    {
        long start = Stopwatch.GetTimestamp();
        TimeSpan cpuStart = Environment.CpuUsage.TotalTime;
        PressureProfileResult result = session.Run(request with { RequestOrdinal = ordinal++ }, static _ => { });
        cpu.Add((Environment.CpuUsage.TotalTime - cpuStart).TotalMilliseconds);
        times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        Check(result);
    }

    times.Sort();
    cpu.Sort();
    // CPU time is process-wide and is unaffected by waiting for a core on a loaded host.
    Console.WriteLine(FormattableString.Invariant(
        $"{mode} cpu median {cpu[cpu.Count / 2]:F1} ms min {cpu[0]:F1} ms | wall median {times[times.Count / 2]:F1} ms min {times[0]:F1} ms"));
}

static int Compare(string[] args)
{
    int rounds = 5;
    int requests = 9;
    long demandMib = 256;
    for (int i = 1; i < args.Length; i += 2)
    {
        if (i + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {args[i]}.", nameof(args));
        }

        long value = long.Parse(args[i + 1], CultureInfo.InvariantCulture);
        switch (args[i])
        {
            case "--rounds":
                rounds = (int)value;
                break;
            case "--requests":
                requests = (int)value;
                break;
            case "--demand-mib":
                demandMib = value;
                break;
            default:
                throw new ArgumentException($"Unknown option {args[i]}.", nameof(args));
        }
    }

    if (rounds < 1 || requests < 1 || demandMib < 1)
    {
        throw new ArgumentException("Rounds, requests and demand must be positive.", nameof(args));
    }

    Console.WriteLine(FormattableString.Invariant(
        $"{rounds} rounds, {requests} timed requests, {demandMib} MiB demand, {WarmupRequests} warmups; one process per mode per round, order alternates"));
    Console.WriteLine("Median per-request process CPU time in ms.");
    Console.WriteLine("round  order      NAM     Safe   Safe/NAM");
    List<double> namMedians = [];
    List<double> safeMedians = [];
    List<double> ratios = [];
    for (int round = 1; round <= rounds; round++)
    {
        bool namFirst = round % 2 == 1;
        double nam;
        double safe;
        if (namFirst)
        {
            nam = RunChild("nam", demandMib, requests);
            safe = RunChild("safe", demandMib, requests);
        }
        else
        {
            safe = RunChild("safe", demandMib, requests);
            nam = RunChild("nam", demandMib, requests);
        }

        namMedians.Add(nam);
        safeMedians.Add(safe);
        ratios.Add(safe / nam);
        Console.WriteLine(FormattableString.Invariant(
            $"{round,5}  {(namFirst ? "NAM,Safe" : "Safe,NAM")} {nam,8:F1} {safe,8:F1} {safe / nam,9:F3}"));
    }

    double namMedian = Median(namMedians);
    double safeMedian = Median(safeMedians);
    Console.WriteLine(FormattableString.Invariant($"median of NAM medians:  {namMedian:F1} ms"));
    Console.WriteLine(FormattableString.Invariant($"median of Safe medians: {safeMedian:F1} ms"));
    Console.WriteLine(FormattableString.Invariant($"Safe/NAM (medians):     {safeMedian / namMedian:F3}"));
    Console.WriteLine(FormattableString.Invariant($"Safe/NAM per-round range: {ratios.Min():F3} - {ratios.Max():F3}"));
    return 0;
}

static double Median(List<double> values)
{
    double[] sorted = [.. values.Order()];
    return sorted[sorted.Length / 2];
}

static double RunChild(string mode, long demandMib, int requests)
{
    string? host = Environment.ProcessPath ?? throw new InvalidOperationException("Process path is unavailable.");
    ProcessStartInfo startInfo = new(host)
    {
        RedirectStandardOutput = true,
        UseShellExecute = false,
    };

    // Run through "dotnet <dll>" when this process was started by the dotnet host rather than an apphost.
    if (string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase))
    {
        startInfo.ArgumentList.Add(Assembly.GetEntryAssembly()?.Location
            ?? throw new InvalidOperationException("Entry assembly path is unavailable."));
    }

    startInfo.ArgumentList.Add("time");
    startInfo.ArgumentList.Add(mode);
    startInfo.ArgumentList.Add(demandMib.ToString(CultureInfo.InvariantCulture));
    startInfo.ArgumentList.Add(requests.ToString(CultureInfo.InvariantCulture));
    using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start child process.");
    string output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"Child '{mode}' exited with code {process.ExitCode}.");
    }

    foreach (string line in output.Split('\n'))
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 4 && string.Equals(parts[0], mode, StringComparison.Ordinal) && string.Equals(parts[1], "cpu", StringComparison.Ordinal)
            && string.Equals(parts[2], "median", StringComparison.Ordinal))
        {
            return double.Parse(parts[3], CultureInfo.InvariantCulture);
        }
    }

    throw new InvalidOperationException($"Could not find the CPU median in the output of '{mode}': {output}");
}
