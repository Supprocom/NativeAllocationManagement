using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class AllocatorWorkerEvidenceTests
{
    // Normal completion includes cold PowerShell startup on a shared host.
    // Deliberate timeout behavior has its own short, unchanged deadline below.
    private const int NormalExitTimeoutMilliseconds = 60_000;

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task ActualSuccessAndFailureKeepExactOutputAndIdentities(int exitCode)
    {
        const string output = "{\"Checksum\":9025015936285474816}";
        string script = CreateScript($"[Console]::Out.WriteLine('{output}'); [Console]::Error.WriteLine('actual stderr'); exit {exitCode}");
        ProcessStartInfo start = Start(script);
        string? directory = null;
        AllocatorPerformanceRegressionTests.WorkerResult result =
            await AllocatorPerformanceRegressionTests.RunWorkerProcessAsync(start, script, "Fixture", retain: true,
                NormalExitTimeoutMilliseconds, value => directory = value);

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(output + Environment.NewLine, result.StandardOutput);
        Assert.Equal("actual stderr" + Environment.NewLine, result.StandardError);
        Assert.NotNull(directory);
        Assert.Equal(result.StandardOutput, await File.ReadAllTextAsync(Path.Combine(directory, "stdout.log")));
        Assert.Equal(result.StandardError, await File.ReadAllTextAsync(Path.Combine(directory, "stderr.log")));
        using JsonDocument command = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "command.json")));
        Assert.Equal(exitCode, command.RootElement.GetProperty("ExitCode").GetInt32());
        Assert.False(command.RootElement.GetProperty("TimedOut").GetBoolean());
        Assert.Equal(NormalExitTimeoutMilliseconds / 1000d, command.RootElement.GetProperty("DeadlineSeconds").GetDouble());
        Assert.Equal(start.FileName, command.RootElement.GetProperty("Executable").GetString());
        Assert.Equal(Environment.CurrentDirectory, command.RootElement.GetProperty("WorkingDirectory").GetString());
        Assert.True(command.RootElement.GetProperty("EndedAt").GetDateTimeOffset()
            >= command.RootElement.GetProperty("StartedAt").GetDateTimeOffset());
        using JsonDocument identity = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "allocator-worker.json")));
        JsonElement worker = identity.RootElement.GetProperty("Worker");
        byte[] bytes = await File.ReadAllBytesAsync(script);
        Assert.Equal(script, worker.GetProperty("Path").GetString());
        Assert.Equal(bytes.LongLength, worker.GetProperty("Length").GetInt64());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), worker.GetProperty("SHA256").GetString());
        Assert.Equal(start.ArgumentList, identity.RootElement.GetProperty("Arguments").EnumerateArray()
            .Select(static item => item.GetString()), StringComparer.Ordinal);
        Assert.Equal("0", identity.RootElement.GetProperty("CompilationEnvironment").GetProperty("DOTNET_TieredCompilation").GetString());
        Assert.Equal("0", identity.RootElement.GetProperty("CompilationEnvironment").GetProperty("DOTNET_TieredPGO").GetString());
        byte[] runtime = await File.ReadAllBytesAsync(typeof(NativeRegion).Assembly.Location);
        Assert.Equal(runtime.LongLength, identity.RootElement.GetProperty("CandidateRuntime").GetProperty("Length").GetInt64());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(runtime)),
            identity.RootElement.GetProperty("CandidateRuntime").GetProperty("SHA256").GetString());
    }

    [Fact]
    public async Task ActualTimeoutKeepsPartialStreamsAndNeverClaimsSuccess()
    {
        string script = CreateScript("[Console]::Out.WriteLine('partial stdout'); [Console]::Error.WriteLine('partial stderr'); Start-Sleep -Seconds 30");
        string? directory = null;
        await Assert.ThrowsAsync<TimeoutException>(() =>
            AllocatorPerformanceRegressionTests.RunWorkerProcessAsync(Start(script), script, "TimeoutFixture", retain: true,
                5_000, value => directory = value));
        Assert.NotNull(directory);
        using JsonDocument command = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "command.json")));
        Assert.True(command.RootElement.GetProperty("TimedOut").GetBoolean());
        Assert.NotEqual(0, command.RootElement.GetProperty("ExitCode").GetInt32());
        Assert.Equal(5d, command.RootElement.GetProperty("DeadlineSeconds").GetDouble());
        Assert.Equal("partial stdout" + Environment.NewLine, await File.ReadAllTextAsync(Path.Combine(directory, "stdout.log")));
        Assert.Equal("partial stderr" + Environment.NewLine, await File.ReadAllTextAsync(Path.Combine(directory, "stderr.log")));
    }

    [Fact]
    public async Task ActualStartFailureKeepsAnUnavailableExitAndDiagnostic()
    {
        string script = CreateScript("exit 0");
        ProcessStartInfo start = Start(script);
        start.FileName = Path.Combine(Path.GetDirectoryName(script)!, "does-not-exist");
        string? directory = null;
        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() =>
            AllocatorPerformanceRegressionTests.RunWorkerProcessAsync(start, script, "StartFailureFixture", retain: true,
                10_000, value => directory = value));
        Assert.NotNull(directory);
        using JsonDocument command = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "command.json")));
        Assert.Equal(JsonValueKind.Null, command.RootElement.GetProperty("ExitCode").ValueKind);
        Assert.False(command.RootElement.GetProperty("TimedOut").GetBoolean());
        Assert.NotEmpty(await File.ReadAllTextAsync(Path.Combine(directory, "stderr.log")));
        Assert.Empty(await File.ReadAllTextAsync(Path.Combine(directory, "stdout.log")));
    }

    [Fact]
    public async Task DisabledRetentionRunsWithoutCreatingEvidence()
    {
        string script = CreateScript("[Console]::Out.WriteLine('not retained'); exit 0");
        bool created = false;
        AllocatorPerformanceRegressionTests.WorkerResult result =
            await AllocatorPerformanceRegressionTests.RunWorkerProcessAsync(Start(script), script, "DisabledFixture", retain: false,
                NormalExitTimeoutMilliseconds, _ => created = true);
        Assert.Equal(0, result.ExitCode);
        Assert.False(created);
        Assert.Equal("not retained" + Environment.NewLine, result.StandardOutput);
    }

    private static string CreateScript(string content)
    {
        string directory = Path.Combine(Path.GetTempPath(), "nam-allocator-evidence-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string script = Path.Combine(directory, "worker.ps1");
        File.WriteAllText(script, content);
        return script;
    }

    private static ProcessStartInfo Start(string script)
    {
        ProcessStartInfo start = new(Environment.GetEnvironmentVariable("POWERSHELL_HOST_PATH") ?? "pwsh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        start.Environment["DOTNET_TieredCompilation"] = "0";
        start.Environment["DOTNET_TieredPGO"] = "0";
        return start;
    }
}
