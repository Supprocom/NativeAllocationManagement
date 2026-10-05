using System.Globalization;
using System.Text.Json;
using Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class ExternalObservationTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("max", null)]
    [InlineData("-1", null)]
    [InlineData("1e3", null)]
    [InlineData("9223372036854775808", null)]
    [InlineData(" 0\n", 0L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    public void CountersDistinguishMissingInvalidAndMeasuredZero(string? text, long? expected) =>
        Assert.Equal(expected, ExternalObservation.ParseCounter(text));

    [Theory]
    [InlineData(null, null)]
    [InlineData("-1", null)]
    [InlineData("2147483648", null)]
    [InlineData("0", 0)]
    [InlineData("2147483647", int.MaxValue)]
    public void CountsRejectOverflowWithoutInventingZero(string? text, int? expected) =>
        Assert.Equal(expected, ExternalObservation.ParseCount(text));

    [Fact]
    public void LimitsDistinguishFiniteUnlimitedAndUnavailable()
    {
        Assert.Equal(((long?)0, (bool?)false), ExternalObservation.ParseLimit("0", v2: true));
        Assert.Equal(((long?)4096, (bool?)false), ExternalObservation.ParseLimit("4096", v2: false));
        Assert.Equal(((long?)null, (bool?)true), ExternalObservation.ParseLimit("max", v2: true));
        Assert.Equal(((long?)null, (bool?)true), ExternalObservation.ParseLimit("-1", v2: false));
        long sentinel = long.MaxValue - (Environment.SystemPageSize - 1);
        Assert.Equal(((long?)null, (bool?)true), ExternalObservation.ParseLimit(sentinel.ToString(CultureInfo.InvariantCulture), v2: false));
        Assert.Equal(((long?)null, (bool?)null), ExternalObservation.ParseLimit("max", v2: false));
        Assert.Equal(((long?)null, (bool?)null), ExternalObservation.ParseLimit("-1", v2: true));
        Assert.Equal(((long?)null, (bool?)null), ExternalObservation.ParseLimit(null, v2: true));
    }

    [Fact]
    public void KeyedFilesPreservePartialDataButRejectAmbiguousAndMalformedKeys()
    {
        IReadOnlyDictionary<string, long?> fields = ExternalObservation.ParseCounters("zero\t0\npositive 19\nduplicate 1\nduplicate 2\nduplicate 3\nnegative -4\nlarge 9223372036854775808\nmissing\nextra 3 units\n");
        Assert.Equal(0L, fields["zero"]);
        Assert.Equal(19L, fields["positive"]);
        foreach (string key in new[] { "duplicate", "negative", "large", "missing", "extra" })
        {
            Assert.Null(fields[key]);
        }

        Assert.False(fields.ContainsKey("absent"));
        Assert.Empty(ExternalObservation.ParseCounters(null));
    }

    [Fact]
    public void FileSectionsBindNamesWithoutPositionalShiftsOrFailedCommandClaims()
    {
        const string output = "[memory.current]\n0\n[memory.events]\noom 0\noom_kill 2\n[memory.stat]\nfile 64\n[cpu.stat]\nusage_usec 0\n";
        CgroupMemorySnapshot snapshot = CgroupMemorySnapshot.FromFiles(ExternalObservation.ParseFileSections(0, output));
        Assert.True(snapshot.Available);
        Assert.Equal(2, snapshot.Version);
        Assert.Equal(0L, snapshot.CurrentBytes);
        Assert.Equal(0L, snapshot.OomEvents);
        Assert.Equal(2L, snapshot.OomKillEvents);
        Assert.Equal(64L, snapshot.FileBytes);
        Assert.Equal(0L, snapshot.CpuUsageMicroseconds);
        Assert.Null(snapshot.LimitBytes);
        Assert.Null(snapshot.LimitUnlimited);
        Assert.Null(snapshot.PeakBytes);
        Assert.Null(snapshot.AnonBytes);
        Assert.Null(snapshot.SwapPeakBytes);
        Assert.Null(snapshot.CpuThrottledMicroseconds);
        Assert.False(CgroupMemorySnapshot.FromFiles(ExternalObservation.ParseFileSections(1, output)).Available);
        Assert.False(CgroupMemorySnapshot.FromFiles(ExternalObservation.ParseFileSections(0, "[memory.current]\n2\n[memory.current]\n3\n")).Available);
    }

    [Fact]
    public void V2MapsEveryAdvertisedKernelCounterWithoutFillingAbsentV1Fields()
    {
        CgroupMemorySnapshot snapshot = CgroupMemorySnapshot.FromFiles(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["memory.max"] = "4096",
            ["memory.current"] = "32",
            ["memory.peak"] = "128",
            ["memory.events"] = "low 1\nhigh 2\nmax 3\noom 4\noom_kill 5\noom_group_kill 6\n",
            ["memory.stat"] = "anon 7\nfile 8\npgfault 15\npgmajfault 16\n",
            ["memory.swap.current"] = "9",
            ["memory.swap.peak"] = "10",
            ["cpu.stat"] = "usage_usec 30\nuser_usec 11\nsystem_usec 19\nnr_periods 14\nnr_throttled 13\nthrottled_usec 17\n"
        });
        Assert.True(snapshot.Available);
        Assert.Equal(2, snapshot.Version);
        Assert.Equal(4096L, snapshot.LimitBytes);
        Assert.False(snapshot.LimitUnlimited);
        Assert.Equal(32L, snapshot.CurrentBytes);
        Assert.Equal(128L, snapshot.PeakBytes);
        Assert.Equal(1L, snapshot.LowEvents);
        Assert.Equal(2L, snapshot.HighEvents);
        Assert.Equal(3L, snapshot.MaxEvents);
        Assert.Equal(4L, snapshot.OomEvents);
        Assert.Equal(5L, snapshot.OomKillEvents);
        Assert.Equal(6L, snapshot.OomGroupKillEvents);
        Assert.Equal(7L, snapshot.AnonBytes);
        Assert.Equal(8L, snapshot.FileBytes);
        Assert.Equal(9L, snapshot.SwapCurrentBytes);
        Assert.Equal(10L, snapshot.SwapPeakBytes);
        Assert.Equal(30L, snapshot.CpuUsageMicroseconds);
        Assert.Equal(11L, snapshot.CpuUserMicroseconds);
        Assert.Equal(19L, snapshot.CpuSystemMicroseconds);
        Assert.Equal(14L, snapshot.CpuPeriods);
        Assert.Equal(13L, snapshot.CpuThrottledPeriods);
        Assert.Equal(17L, snapshot.CpuThrottledMicroseconds);
        Assert.Equal(15L, snapshot.PageFaults);
        Assert.Equal(16L, snapshot.MajorPageFaults);
        Assert.Null(snapshot.V1RssBytes);
        Assert.Null(snapshot.LimitHitEvents);
    }

    [Fact]
    public void V1UsesActualHierarchicalCountersAndDoesNotRelabelRssOrLimitHits()
    {
        CgroupMemorySnapshot snapshot = CgroupMemorySnapshot.FromFiles(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["memory.limit_in_bytes"] = "4096",
            ["memory.usage_in_bytes"] = "0",
            ["memory.max_usage_in_bytes"] = "128",
            ["memory.stat"] = "rss 17\ntotal_rss 32\ntotal_cache 64\ntotal_swap 0\ntotal_pgfault 41\ntotal_pgmajfault 0\n",
            ["memory.failcnt"] = "5",
            ["memory.oom_control"] = "under_oom 0\noom_kill 3\n"
        });
        Assert.True(snapshot.Available);
        Assert.Equal(1, snapshot.Version);
        Assert.Equal(4096L, snapshot.LimitBytes);
        Assert.False(snapshot.LimitUnlimited);
        Assert.Equal(0L, snapshot.CurrentBytes);
        Assert.Equal(128L, snapshot.PeakBytes);
        Assert.Equal(32L, snapshot.V1RssBytes);
        Assert.Equal(64L, snapshot.FileBytes);
        Assert.Equal(0L, snapshot.SwapCurrentBytes);
        Assert.Equal(5L, snapshot.LimitHitEvents);
        Assert.Equal(3L, snapshot.OomKillEvents);
        Assert.Equal(41L, snapshot.PageFaults);
        Assert.Equal(0L, snapshot.MajorPageFaults);
        Assert.Null(snapshot.AnonBytes);
        Assert.Null(snapshot.MaxEvents);
        Assert.Null(snapshot.OomEvents);
        Assert.Null(snapshot.CpuUsageMicroseconds);
        Assert.Null(snapshot.SwapPeakBytes);
    }

    [Fact]
    public void RealFilesystemKeepsUnavailableFilesDistinctFromMeasuredZero()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nam-external-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "memory.current"), "0\n");
            File.WriteAllText(Path.Combine(directory, "memory.max"), "max\n");
            File.WriteAllText(Path.Combine(directory, "cpu.stat"), "usage_usec 17\nuser_usec 11\nsystem_usec 6\n");
            Directory.CreateDirectory(Path.Combine(directory, "memory.swap.peak"));
            CgroupMemorySnapshot snapshot = CgroupMemorySnapshot.ReadFromDirectory(directory);
            Assert.True(snapshot.Available);
            Assert.True(snapshot.LimitUnlimited);
            Assert.Null(snapshot.LimitBytes);
            Assert.Equal(0L, snapshot.CurrentBytes);
            Assert.Equal(17L, snapshot.CpuUsageMicroseconds);
            Assert.Equal(11L, snapshot.CpuUserMicroseconds);
            Assert.Equal(6L, snapshot.CpuSystemMicroseconds);
            Assert.Null(snapshot.SwapPeakBytes);
            Assert.Equal(directory, snapshot.SourcePath);
            File.WriteAllText(Path.Combine(directory, "memory.current"), "invalid");
            snapshot = CgroupMemorySnapshot.ReadFromDirectory(directory);
            Assert.False(snapshot.Available);
            Assert.Null(snapshot.CurrentBytes);
            Assert.Equal(17L, snapshot.CpuUsageMicroseconds);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("0B", 0L)]
    [InlineData("1.5 KiB", 1536L)]
    [InlineData("2kB", 2000L)]
    [InlineData("1TiB", 1099511627776L)]
    [InlineData("9223372036854775807B", long.MaxValue)]
    [InlineData("9223372036854775808B", null)]
    [InlineData("79228162514264337593543950335GiB", null)]
    [InlineData("2XB", null)]
    [InlineData("-1B", null)]
    [InlineData("NaNB", null)]
    [InlineData(null, null)]
    public void ByteSizesUseKnownUnitsAndCheckedRange(string? text, long? expected) =>
        Assert.Equal(expected, ExternalObservation.ParseByteSize(text));

    [Theory]
    [InlineData("0%", 0.0)]
    [InlineData("125.5%", 125.5)]
    [InlineData("NaN", null)]
    [InlineData("Infinity", null)]
    [InlineData("-1%", null)]
    [InlineData(null, null)]
    public void PercentagesCanExceedOneCoreButCannotFabricateFiniteMeasurements(string? text, double? expected) =>
        Assert.Equal(expected, ExternalObservation.ParsePercent(text));

    [Fact]
    public void DerivedMeasurementsRequireValidEndpointsAndFrequency()
    {
        Assert.Equal(0L, ExternalObservation.Delta(17, 17));
        Assert.Equal(4L, ExternalObservation.Delta(17, 21));
        Assert.Null(ExternalObservation.Delta(null, 0));
        Assert.Null(ExternalObservation.Delta(7, 0));
        Assert.Null(ExternalObservation.Delta(-1, 7));
        Assert.Equal(0.0, ExternalObservation.CpuMilliseconds(0, 0, 100));
        Assert.Equal(150.0, ExternalObservation.CpuMilliseconds(10, 5, 100));
        Assert.Null(ExternalObservation.CpuMilliseconds(0, 0, null));
        Assert.Null(ExternalObservation.CpuMilliseconds(1, 2, 0));
        Assert.NotNull(ExternalObservation.CpuMilliseconds(long.MaxValue, long.MaxValue, 1));
        Assert.Equal(0.0, ExternalObservation.CpuCores(0, 10));
        Assert.Equal(2.0, ExternalObservation.CpuCores(20_000, 10));
        Assert.Null(ExternalObservation.CpuCores(null, 10));
        Assert.Null(ExternalObservation.CpuCores(0, double.PositiveInfinity));
        Assert.Null(ExternalObservation.CpuCores(0, 0));
        Assert.Equal(((long?)0, (long?)1024), ExternalObservation.ParseUsage("0B / 1KiB"));
        Assert.Equal(((long?)null, (long?)null), ExternalObservation.ParseUsage("0B"));
        Assert.Equal(((long?)null, (long?)0), ExternalObservation.ParseUsage("-- / 0B"));
    }

    [Fact]
    public void ProcessSamplingKeepsPartialAvailabilityAndNeverAssumesClockFrequency()
    {
        DateTime utc = new(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);
        const string output = "threads 2\nvoluntary 0\nnonvoluntary 3\nuser_ticks 10\nsystem_ticks 5\nclock_ticks 100\nworking_set_kib 17\n";
        PressureExternalProcessSnapshot snapshot = PressureExternalProcessSnapshot.FromCommand(utc, default, 0, output);
        Assert.True(snapshot.Available);
        Assert.Equal(2, snapshot.ThreadCount);
        Assert.Equal(0L, snapshot.VoluntaryContextSwitches);
        Assert.Equal(3L, snapshot.NonvoluntaryContextSwitches);
        Assert.Equal(150.0, snapshot.ProcessCpuMilliseconds);
        Assert.Equal(17L * 1024, snapshot.WorkingSetBytes);
        snapshot = PressureExternalProcessSnapshot.FromCommand(utc, default, 0, output.Replace("clock_ticks 100\n", string.Empty, StringComparison.Ordinal));
        Assert.False(snapshot.Available);
        Assert.Null(snapshot.ClockTicksPerSecond);
        Assert.Null(snapshot.ProcessCpuMilliseconds);
        Assert.Equal(10L, snapshot.ProcessUserTicks);
        snapshot = PressureExternalProcessSnapshot.FromCommand(utc, default, 1, output);
        Assert.False(snapshot.Available);
        Assert.Null(snapshot.ThreadCount);
        Assert.Null(snapshot.WorkingSetBytes);
        Assert.Equal(utc, snapshot.Utc);
        snapshot = PressureExternalProcessSnapshot.FromCommand(utc, default, 0, "working_set_kib 9223372036854775807\nthreads 2147483648\n");
        Assert.Null(snapshot.ThreadCount);
        Assert.Null(snapshot.WorkingSetBytes);
    }

    [Fact]
    public void SummaryDoesNotCountHistoricalPeaksOrMissingSamplesAsCurrentMeasurements()
    {
        PressureExternalSummary missing = PressureExternalSummary.Capture(default, default, [], null);
        Assert.Null(missing.ObservedPeakBytes);
        Assert.Null(missing.CpuPercentMean);
        Assert.Null(missing.CpuPercentPeak);
        Assert.Null(missing.EffectiveCpuCores);
        Assert.Null(missing.PageFaultsDelta);
        Assert.Null(missing.MajorPageFaultsDelta);
        Assert.Equal(0, missing.CpuSampleCount);
        Assert.False(missing.NewKernelHighWater);
        CgroupMemorySnapshot before = default(CgroupMemorySnapshot) with { CurrentBytes = 0, PeakBytes = 1000, CpuUsageMicroseconds = 0, PageFaults = 9, MajorPageFaults = 1 };
        CgroupMemorySnapshot after = before with { CurrentBytes = 64, CpuUsageMicroseconds = 20_000, PageFaults = 9, MajorPageFaults = 0 };
        PressureHostSample[] samples =
        [
            default(PressureHostSample) with { CgroupMemoryBytes = 128, CpuPercent = 0 },
            default,
            default(PressureHostSample) with { CpuPercent = 200 },
            default(PressureHostSample) with { CpuPercent = double.NaN }
        ];
        PressureExternalSummary summary = PressureExternalSummary.Capture(before, after, samples, 10);
        Assert.Equal(128L, summary.ObservedPeakBytes);
        Assert.False(summary.NewKernelHighWater);
        Assert.Equal(2, summary.CpuSampleCount);
        Assert.Equal(100.0, summary.CpuPercentMean);
        Assert.Equal(200.0, summary.CpuPercentPeak);
        Assert.Equal(2.0, summary.EffectiveCpuCores);
        Assert.Equal(0L, summary.PageFaultsDelta);
        Assert.Null(summary.MajorPageFaultsDelta);
        summary = PressureExternalSummary.Capture(before, after with { PeakBytes = 1200 }, [], 10);
        Assert.Equal(1200L, summary.ObservedPeakBytes);
        Assert.True(summary.NewKernelHighWater);
        summary = PressureExternalSummary.Capture(before with { PeakBytes = null }, after, [], 10);
        Assert.Equal(64L, summary.ObservedPeakBytes);
        Assert.False(summary.NewKernelHighWater);
    }

    [Fact]
    public void JsonRoundTripsUnavailableAndMeasuredZeroWithoutLosingMeaning()
    {
        CgroupMemorySnapshot original = CgroupMemorySnapshot.FromFiles(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["memory.current"] = "0",
            ["memory.max"] = "max"
        });
        string json = JsonSerializer.Serialize(original, VoxelJson.Options);
        Assert.Contains("\"currentBytes\":0", json, StringComparison.Ordinal);
        Assert.Contains("\"swapPeakBytes\":null", json, StringComparison.Ordinal);
        Assert.Contains("\"limitUnlimited\":true", json, StringComparison.Ordinal);
        Assert.Equal(original, JsonSerializer.Deserialize<CgroupMemorySnapshot>(json, VoxelJson.Options));
        PressureExternalProcessSnapshot unavailable = PressureExternalProcessSnapshot.FromCommand(DateTime.UtcNow, original, 1, string.Empty);
        Assert.Equal(unavailable, JsonSerializer.Deserialize<PressureExternalProcessSnapshot>(JsonSerializer.Serialize(unavailable, VoxelJson.Options), VoxelJson.Options));
    }

    [VoxelDemonstrationFact]
    public void ActualOsSnapshotIdentifiesItsSourceAndCompletedGcObservation()
    {
        CgroupMemorySnapshot cgroup = CgroupMemorySnapshot.Read();
        if (OperatingSystem.IsLinux() && cgroup.Available)
        {
            Assert.True(cgroup.Available);
            Assert.NotNull(cgroup.SourcePath);
            Assert.True(cgroup.CurrentBytes >= 0);
            Assert.True(cgroup.Version is 1 or 2);
        }
        else
        {
            Assert.False(cgroup.Available);
            Assert.Null(cgroup.CurrentBytes);
            if (!OperatingSystem.IsLinux())
            {
                Assert.Null(cgroup.Version);
            }
        }

        GC.Collect();
        PressureRuntimeSnapshot runtime = PressureRuntimeSnapshot.Capture();
        Assert.True(runtime.LastCompletedGcIndex > 0);
        Assert.NotNull(runtime.HeapSizeBytes);
        Assert.NotNull(runtime.LargeObjectHeapBytes);
        Assert.True(runtime.HeapSizeBytes >= runtime.LargeObjectHeapBytes);
        Assert.Null(default(PressureRuntimeSnapshot).LargeObjectHeapBytes);
    }
}
