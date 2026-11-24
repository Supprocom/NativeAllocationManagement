using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Performance;

// NativePool<T>.Shared rent cost ablation: which step costs the gap to
// ArrayPool<T>.Shared. Every row is measured in its own process, once per sample,
// over eight code layouts (see LayoutAveraging.cs).
//
//   --shared-rent-ablation [--length 64] [--samples 9] [--iterations 2000000]
//       [--variants-root artifacts/shared-rent-ablation] [--variants A,B,.. (default: none)] [--quick]
//       drives the rows; every NativePool row runs against the library build in
//       <variants-root>/<Variant>/ (Default = the unmodified library).
//   --shared-rent-worker --row array-pool|native-pool ...   one process, one row.
//   --shared-rent-offsets [--variants-root ..]   disassembles the layout copies of
//       the Default build and prints each copy's loop start offset mod 32.
//
// Extra rows are Release builds of other commits placed in <variants-root>/<Name>/
// and named with --variants; samples interleave across all rows.
internal static class SharedRentAblation
{
    private const string Footnote = "ns per op (rent + fill + return), median over samples per layout; avg is the mean of the layouts.";
    private const string DllName = "Supprocom.NativeAllocationManagement.Performance.dll";
    private static readonly string[] AllVariants = [];

    internal static async Task<int> RunCommandAsync(string[] args)
    {
        Options options = Options.Parse(args);
        return args[0] switch
        {
            "--shared-rent-worker" => RunWorker(options),
            "--shared-rent-offsets" => await RunOffsetsAsync(options).ConfigureAwait(false),
            _ => await RunDriverAsync(options).ConfigureAwait(false)
        };
    }

    private sealed class Options
    {
        internal int Length = 64;
        internal int Samples = 9;
        internal int Iterations = 2_000_000;
        internal int WarmRounds = 5;
        internal bool Quick;
        internal string Row = "native-pool";
        internal string Root = Path.Combine("artifacts", "shared-rent-ablation");
        internal string[] Variants = AllVariants;
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
                    case "--variants-root": o.Root = Next(); break;
                    case "--variants": o.Variants = Next().Split(',', StringSplitOptions.RemoveEmptyEntries); break;
                    case "--max-minutes": o.MaxMinutes = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--quick": o.Quick = true; break;
                    default: throw new ArgumentException($"Unknown option {name}.", nameof(args));
                }
            }

            if (o.Quick)
            {
                o.Samples = 1;
                o.Iterations = Math.Min(o.Iterations, 300_000);
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
            default:
                throw new ArgumentException($"Unknown row {o.Row}.", nameof(o));
        }

        Console.WriteLine("RESULT " + string.Join(' ', ns.Select(v => v.ToString("F4", CultureInfo.InvariantCulture))));
        return 0;
    }

    // ---- driver ----

    private sealed record Row(string Name, string RowKind, string Directory);

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

    private static string? LibraryDirectory(Options o, string variant)
    {
        string dir = Path.Combine(o.Root, variant);
        return File.Exists(Path.Combine(dir, DllName)) ? dir : null;
    }

    private static async Task<int> RunDriverAsync(Options o)
    {
        Console.WriteLine($"shared-rent ablation: length={o.Length} samples={o.Samples} iterations={o.Iterations} layouts={LayoutLoop.Layouts}");
        Console.WriteLine($"start {DateTime.Now:HH:mm:ss} loadavg {LoadAverage()}");
        string selfDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string baseline = LibraryDirectory(o, "Default") ?? selfDirectory;
        List<Row> rows =
        [
            new("ArrayPool<int>.Shared", "array-pool", baseline),
            new("NativePool<int>.Shared (default)", "native-pool", baseline)
        ];
        foreach (string variant in o.Variants)
        {
            string? dir = LibraryDirectory(o, variant);
            if (dir is null)
            {
                Console.WriteLine($"skip {variant}: no build in {Path.Combine(o.Root, variant)} (run shared-rent-ablation.sh)");
                continue;
            }

            rows.Add(new($"NativePool<int>.Shared [{variant}]", "native-pool", dir));
        }

        Stopwatch wall = Stopwatch.StartNew();
        StringBuilder table = new();
        table.Append(CultureInfo.InvariantCulture, $"{"row",-40}");
        for (int l = 0; l < LayoutLoop.Layouts; l++)
        {
            table.Append(CultureInfo.InvariantCulture, $"{"L" + l,7}");
        }

        table.AppendLine(CultureInfo.InvariantCulture, $"{"avg",8}{"min",7}{"max",7}");
        // Samples interleave across the rows (sample 1 of every row, then sample 2 ...), so a
        // slow stretch of the shared host lands on every row alike instead of on one.
        List<double[]>[] collected = rows.Select(_ => new List<double[]>()).ToArray();
        for (int s = 0; s < o.Samples; s++)
        {
            for (int r = 0; r < rows.Count; r++)
            {
                if (wall.Elapsed.TotalMinutes > o.MaxMinutes)
                {
                    Console.WriteLine($"stopped: exceeded --max-minutes {o.MaxMinutes}");
                    return 4;
                }

                collected[r].Add(await RunWorkerProcessAsync(rows[r], o).ConfigureAwait(false));
            }
        }

        for (int r = 0; r < rows.Count; r++)
        {
            Row row = rows[r];
            List<double[]> samples = collected[r];
            double[] median = new double[LayoutLoop.Layouts];
            for (int l = 0; l < LayoutLoop.Layouts; l++)
            {
                double[] column = samples.Select(x => x[l]).Order().ToArray();
                median[l] = column.Length % 2 == 1
                    ? column[column.Length / 2]
                    : (column[column.Length / 2 - 1] + column[column.Length / 2]) / 2;
            }

            table.Append(CultureInfo.InvariantCulture, $"{row.Name,-40}");
            foreach (double v in median)
            {
                table.Append(CultureInfo.InvariantCulture, $"{v,7:F2}");
            }

            table.AppendLine(CultureInfo.InvariantCulture, $"{median.Average(),8:F2}{median.Min(),7:F2}{median.Max(),7:F2}");
            Console.WriteLine(table.ToString().TrimEnd());
            Console.WriteLine();
            table.Clear();
        }

        await Console.Out.WriteLineAsync(Footnote).ConfigureAwait(false);
        Console.WriteLine($"end {DateTime.Now:HH:mm:ss} loadavg {LoadAverage()} elapsed {wall.Elapsed.TotalMinutes:F1} min");
        return 0;
    }

    private static ProcessStartInfo WorkerStartInfo(string directory, string row, Options o)
    {
        ProcessStartInfo info = new()
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        info.ArgumentList.Add(Path.Combine(directory, DllName));
        info.ArgumentList.Add("--shared-rent-worker");
        info.ArgumentList.Add("--row");
        info.ArgumentList.Add(row);
        info.ArgumentList.Add("--length");
        info.ArgumentList.Add(o.Length.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add("--iterations");
        info.ArgumentList.Add(o.Iterations.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add("--warm-rounds");
        info.ArgumentList.Add(o.WarmRounds.ToString(CultureInfo.InvariantCulture));
        // Tiering as in production, but promote at once so the copies reach tier 1.
        info.Environment["DOTNET_TC_CallCountingDelayMs"] = "0";
        // The JIT aligns the vectorized fill loops inside the body to 16 bytes, which
        // absorbs the copies' padding (copies 0-2 got the same loop offset). Without
        // alignment the k pad calls move the whole loop by 6k bytes.
        info.Environment["DOTNET_JitAlignLoops"] = "0";
        return info;
    }

    private static async Task<double[]> RunWorkerProcessAsync(Row row, Options o)
    {
        using Process process = new() { StartInfo = WorkerStartInfo(row.Directory, row.RowKind, o) };
        process.Start();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        Task exited = process.WaitForExitAsync();
        if (await Task.WhenAny(exited, Task.Delay(TimeSpan.FromMinutes(5))).ConfigureAwait(false) != exited)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"Worker for {row.Name} exceeded 5 minutes.");
        }

        string text = await output.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Worker for {row.Name} failed ({process.ExitCode}): {await error.ConfigureAwait(false)}");
        }

        string line = text.Split('\n').First(l => l.StartsWith("RESULT ", StringComparison.Ordinal));
        return line["RESULT ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
    }

    // ---- loop offsets ----

    private static async Task<int> RunOffsetsAsync(Options o)
    {
        string directory = LibraryDirectory(o, "Default") ?? AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string file = Path.Combine(Path.GetTempPath(), $"layout-disasm-{Environment.ProcessId}.txt");
        ProcessStartInfo info = WorkerStartInfo(directory, o.Row, o);
        info.Environment["DOTNET_JitDisasm"] = "Run";
        info.Environment["DOTNET_JitStdOutFile"] = file;
        info.Environment["DOTNET_JitDisasmWithCodeBytes"] = "1";
        File.Delete(file);
        using Process process = Process.Start(info)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        _ = await output.ConfigureAwait(false);
        _ = await error.ConfigureAwait(false);
        Console.WriteLine($"disassembly written to {file}");
        foreach (LoopPlacement p in LayoutOffsets.Parse(await File.ReadAllTextAsync(file).ConfigureAwait(false)))
        {
            Console.WriteLine($"copy {p.Copy} ({p.Tier}): loop head offset {p.HeadOffset} (mod 32 = {p.HeadMod32}); back-edge jump at {p.JumpOffset} len {p.JumpLength}{(p.JumpHitsBoundary ? " crosses/ends on a 32-byte boundary" : string.Empty)}");
        }

        return process.ExitCode;
    }
}
