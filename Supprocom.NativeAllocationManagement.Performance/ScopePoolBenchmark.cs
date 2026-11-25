using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Performance;

// Scope pool against ArrayPool<int>.Shared and NativePool<int>.Shared. Each op is
// rent + fill + return of Length ints; every row reports ns per rent. The scope rows
// include the cost of the scope itself (header, first block, final free) spread over
// the rents of one Run. Every row is measured in its own process, once per sample,
// over eight code layouts (see LayoutAveraging.cs); samples interleave across rows.
//
//   --scope-pool [--length 64] [--samples 9] [--iterations 2000000] [--quick]
//   --scope-pool-worker --row array-pool|native-pool|native-instance|scope-1|scope-16|scope-1024 ...
internal static class ScopePoolBenchmark
{
    private const string Footnote = "ns per rent (rent + fill + return), scope rows include scope setup and teardown; median over samples per layout; avg is the mean of the layouts.";
    private const string DllName = "Supprocom.NativeAllocationManagement.Performance.dll";

    private static readonly (string Name, string Kind, int Rents)[] Rows =
    [
        ("ArrayPool<int>.Shared", "array-pool", 1),
        ("NativePool<int>.Shared", "native-pool", 1),
        ("new NativePool<int>() instance", "native-instance", 1),
        ("NativeScopePool, 1 rent per Run", "scope-1", 1),
        ("NativeScopePool, 16 rents per Run", "scope-16", 16),
        ("NativeScopePool, 1024 rents per Run", "scope-1024", 1024)
    ];

    internal static async Task<int> RunCommandAsync(string[] args)
    {
        Options options = Options.Parse(args);
        return string.Equals(args[0], "--scope-pool-worker", StringComparison.Ordinal)
            ? RunWorker(options)
            : await RunDriverAsync(options).ConfigureAwait(false);
    }

    private sealed class Options
    {
        internal int Length = 64;
        internal int Samples = 9; // at least 7 for the keep/drop rule
        internal int Iterations = 2_000_000;
        internal int WarmRounds = 5;
        internal bool Quick;
        internal string Row = "native-pool";
        internal int MaxMinutes = 90;

        internal static Options Parse(string[] args)
        {
            Options o = new();
            for (int i = 1; i < args.Length; i++)
            {
                string name = args[i];
                string Next() => i + 1 < args.Length
                    ? args[++i]
                    : throw new ArgumentException($"{name} needs a value.", nameof(args));
                switch (name)
                {
                    case "--length": o.Length = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--samples": o.Samples = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--iterations": o.Iterations = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--warm-rounds": o.WarmRounds = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--row": o.Row = Next(); break;
                    case "--max-minutes": o.MaxMinutes = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--quick": o.Quick = true; break;
                    default: throw new ArgumentException($"Unknown option {name}.", nameof(args));
                }
            }

            if (o.Quick)
            {
                o.Samples = 1;
                o.Iterations = Math.Min(o.Iterations, 300_000);
                o.WarmRounds = Math.Min(o.WarmRounds, 3);
            }

            return o;
        }
    }

    // ---- worker ----

    private struct ArrayPoolBody : ILayoutBody
    {
        internal int Length;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public readonly void Invoke()
        {
            ArrayPool<int> pool = ArrayPool<int>.Shared;
            int[] array = pool.Rent(Length);
            array.AsSpan(0, Length).Fill(7);
            pool.Return(array);
        }
    }

    private struct NativePoolBody : ILayoutBody
    {
        internal int Length;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public readonly void Invoke()
        {
            using Pooled<int> lease = NativePool<int>.Shared.Rent(Length, 7);
        }
    }

    private struct NativeInstanceBody : ILayoutBody
    {
        internal NativePool<int> Pool;
        internal int Length;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public readonly void Invoke()
        {
            using Pooled<int> lease = Pool.Rent(Length, 7);
        }
    }

    private static readonly NativePoolScopeAction<int, (int Rents, int Length)> ScopeAction =
        static (scoped ref NativeScopePool<int> pool, (int Rents, int Length) state) =>
        {
            for (int i = 0; i < state.Rents; i++)
            {
                pool.Rent(state.Length, 7).Dispose();
            }
        };

    // One Invoke is one Run of Rents rents.
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ScopeBody : ILayoutBody
    {
        internal int Rents;
        internal int Length;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public readonly void Invoke() =>
            NativePoolScope.Run<int, (int Rents, int Length)>((Rents, Length), ScopeAction);
    }

    private static int RunWorker(Options o)
    {
        double[] ns;
        switch (o.Row)
        {
            case "array-pool":
                ArrayPoolBody a = new() { Length = o.Length };
                LayoutLoop.Warm(a, o.WarmRounds);
                ns = LayoutLoop.Measure(a, o.Iterations);
                break;
            case "native-pool":
                NativePoolBody n = new() { Length = o.Length };
                LayoutLoop.Warm(n, o.WarmRounds);
                ns = LayoutLoop.Measure(n, o.Iterations);
                break;
            case "native-instance":
                using (NativePool<int> instance = new())
                {
                    NativeInstanceBody i = new() { Pool = instance, Length = o.Length };
                    LayoutLoop.Warm(i, o.WarmRounds);
                    ns = LayoutLoop.Measure(i, o.Iterations);
                }

                break;
            case "scope-1":
            case "scope-16":
            case "scope-1024":
                int rents = Rows.First(r => string.Equals(r.Kind, o.Row, StringComparison.Ordinal)).Rents;
                ScopeBody s = new() { Rents = rents, Length = o.Length };
                LayoutLoop.Warm(s, o.WarmRounds, iterations: Math.Max(2, 500 / rents));
                int runs = Math.Max(1, o.Iterations / rents);
                ns = LayoutLoop.Measure(s, runs);
                ns = ns.Select(v => v / rents).ToArray();

                break;
            default:
                throw new ArgumentException($"Unknown row {o.Row}.", nameof(o));
        }

        Console.WriteLine("RESULT " + string.Join(' ', ns.Select(v => v.ToString("F4", CultureInfo.InvariantCulture))));
        return 0;
    }

    // ---- driver ----

    private static string LoadAverage()
    {
        try
        {
            return File.ReadAllText("/proc/loadavg").Trim();
        }
        catch (IOException)
        {
            return "unavailable";
        }
    }

    private static async Task<int> RunDriverAsync(Options o)
    {
        Console.WriteLine($"scope pool: length={o.Length} samples={o.Samples} iterations={o.Iterations} layouts={LayoutLoop.Layouts}{(o.Quick ? " (quick)" : string.Empty)}");
        Console.WriteLine($"start {DateTime.Now:HH:mm:ss} loadavg {LoadAverage()}");
        Stopwatch wall = Stopwatch.StartNew();
        // Samples interleave across the rows (sample 1 of every row, then sample 2 ...), so a
        // slow stretch of the shared host lands on every row alike instead of on one.
        List<double[]>[] collected = Rows.Select(_ => new List<double[]>()).ToArray();
        for (int s = 0; s < o.Samples; s++)
        {
            for (int r = 0; r < Rows.Length; r++)
            {
                if (wall.Elapsed.TotalMinutes > o.MaxMinutes)
                {
                    Console.WriteLine($"stopped: exceeded --max-minutes {o.MaxMinutes}");
                    return 4;
                }

                collected[r].Add(await RunWorkerProcessAsync(Rows[r].Name, Rows[r].Kind, o).ConfigureAwait(false));
            }
        }

        StringBuilder table = new();
        table.Append(CultureInfo.InvariantCulture, $"{"row",-40}");
        for (int l = 0; l < LayoutLoop.Layouts; l++)
        {
            table.Append(CultureInfo.InvariantCulture, $"{"L" + l,7}");
        }

        table.AppendLine(CultureInfo.InvariantCulture, $"{"avg",8}{"min",7}{"max",7}");
        for (int r = 0; r < Rows.Length; r++)
        {
            List<double[]> samples = collected[r];
            double[] median = new double[LayoutLoop.Layouts];
            for (int l = 0; l < LayoutLoop.Layouts; l++)
            {
                double[] column = samples.Select(x => x[l]).Order().ToArray();
                median[l] = column.Length % 2 == 1
                    ? column[column.Length / 2]
                    : (column[column.Length / 2 - 1] + column[column.Length / 2]) / 2;
            }

            table.Append(CultureInfo.InvariantCulture, $"{Rows[r].Name,-40}");
            foreach (double v in median)
            {
                table.Append(CultureInfo.InvariantCulture, $"{v,7:F2}");
            }

            table.AppendLine(CultureInfo.InvariantCulture, $"{median.Average(),8:F2}{median.Min(),7:F2}{median.Max(),7:F2}");
        }

        Console.WriteLine(table.ToString().TrimEnd());
        Console.WriteLine();
        PrintSamplesAndVerdicts(collected, o);
        Console.WriteLine();
        await Console.Out.WriteLineAsync(Footnote).ConfigureAwait(false);
        Console.WriteLine($"end {DateTime.Now:HH:mm:ss} loadavg {LoadAverage()} elapsed {wall.Elapsed.TotalMinutes:F1} min");
        return 0;
    }

    // Raw per-sample ns/op (mean of the layouts), then the keep/drop rule applied mechanically:
    // KEEP only if the scope row is faster than the "new NativePool<int>()" row in EVERY sample
    // and its mean advantage exceeds twice the larger sample-to-sample spread (max - min) of the two rows.
    private static void PrintSamplesAndVerdicts(List<double[]>[] collected, Options o)
    {
        double[][] perSample = collected.Select(c => c.Select(x => x.Average()).ToArray()).ToArray();
        Console.Out.WriteLine("raw ns per rent per sample (mean of the layouts):");
        for (int r = 0; r < Rows.Length; r++)
        {
            double[] v = perSample[r];
            Console.WriteLine($"{Rows[r].Name,-40} {string.Join(' ', v.Select(x => x.ToString("F2", CultureInfo.InvariantCulture)))}  | mean {v.Average():F2} spread {v.Max() - v.Min():F2}");
        }

        int instance = Array.FindIndex(Rows, r => string.Equals(r.Kind, "native-instance", StringComparison.Ordinal));
        foreach (int rents in new[] { 16, 1024 })
        {
            int scope = Array.FindIndex(Rows, r => string.Equals(r.Kind, $"scope-{rents}", StringComparison.Ordinal));
            double[] a = perSample[scope];
            double[] b = perSample[instance];
            bool every = a.Zip(b, (x, y) => x < y).All(x => x);
            double advantage = b.Average() - a.Average();
            double spread = Math.Max(a.Max() - a.Min(), b.Max() - b.Min());
            bool keep = every && advantage > 2 * spread && o.Samples >= 7;
            Console.WriteLine($"{(keep ? "KEEP" : "DROP")} at {rents} rents per scope: faster in every sample={every} ({a.Zip(b, (x, y) => x < y).Count(x => x)}/{a.Length}); mean advantage {advantage:F2} ns vs 2 x max spread {2 * spread:F2} ns (scope spread {a.Max() - a.Min():F2}, instance spread {b.Max() - b.Min():F2}); samples={o.Samples}{(o.Samples < 7 ? " (fewer than 7: not valid for the rule)" : string.Empty)}");
        }

        Console.Out.WriteLine("1 rent per scope is information only.");
    }

    private static async Task<double[]> RunWorkerProcessAsync(string name, string row, Options o)
    {
        ProcessStartInfo info = new()
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, DllName));
        info.ArgumentList.Add("--scope-pool-worker");
        info.ArgumentList.Add("--row");
        info.ArgumentList.Add(row);
        info.ArgumentList.Add("--length");
        info.ArgumentList.Add(o.Length.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add("--iterations");
        info.ArgumentList.Add(o.Iterations.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add("--warm-rounds");
        info.ArgumentList.Add(o.WarmRounds.ToString(CultureInfo.InvariantCulture));
        // Same settings as the shared-rent ablation: tier up at once, no loop alignment.
        info.Environment["DOTNET_TC_CallCountingDelayMs"] = "0";
        info.Environment["DOTNET_JitAlignLoops"] = "0";
        using Process process = new() { StartInfo = info };
        process.Start();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        Task exited = process.WaitForExitAsync();
        if (await Task.WhenAny(exited, Task.Delay(TimeSpan.FromMinutes(5))).ConfigureAwait(false) != exited)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"Worker for {name} exceeded 5 minutes.");
        }

        string text = await output.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Worker for {name} failed ({process.ExitCode}): {await error.ConfigureAwait(false)}");
        }

        string line = text.Split('\n').First(l => l.StartsWith("RESULT ", StringComparison.Ordinal));
        return line["RESULT ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
    }
}
