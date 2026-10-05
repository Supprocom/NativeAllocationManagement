using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class PackageFixtureEvidenceTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData("1 ", false)]
    [InlineData("1", true)]
    public void RetentionRequiresTheExplicitSelection(string? selection, bool expected)
        => Assert.Equal(expected, PackageFixtureEvidence.IsEnabled(selection));

    [Fact]
    public void DisabledRetentionDoesNotCreateOrReadAConsumerDirectory()
        => Assert.Null(PackageFixtureEvidence.Begin(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), retain: false));

    [Fact]
    public async Task CommandsKeepTheirOwnPreExecutionSourcesAndHonestFailureRecords()
    {
        string root = Path.Combine(Path.GetTempPath(), "nam-package-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string program = Path.Combine(root, "Program.cs");
        const string positive = "public static class Consumer { public static int Value => 17; }";
        const string negative = "public static class Consumer { public static int Value => Invalid; }";
        await File.WriteAllTextAsync(program, positive);
        await File.WriteAllTextAsync(Path.Combine(root, "Consumer.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        await File.WriteAllTextAsync(Path.Combine(root, "Directory.Build.props"), PackageFixtureEvidence.BuildProperties);
        await File.WriteAllTextAsync(Path.Combine(root, "notes.md"), "not compiler input");
        DateTimeOffset started = DateTimeOffset.UtcNow;
        string first = PackageFixtureEvidence.Begin(root, retain: true)!;
        await File.WriteAllTextAsync(program, negative);
        string second = PackageFixtureEvidence.Begin(root, retain: true)!;
        Assert.False(string.Equals(first, second, StringComparison.Ordinal));
        Assert.Equal(positive, await File.ReadAllTextAsync(Path.Combine(first, "source", "Program.cs")));
        Assert.Equal(negative, await File.ReadAllTextAsync(Path.Combine(second, "source", "Program.cs")));
        Assert.False(File.Exists(Path.Combine(first, "source", "notes.md")));
        using JsonDocument manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(first, "source-manifest.json")));
        JsonElement entry = manifest.RootElement.EnumerateArray().First(static item =>
            string.Equals(item.GetProperty("Name").GetString(), "Program.cs", StringComparison.Ordinal));
        byte[] bytes = Encoding.UTF8.GetBytes(positive);
        Assert.Equal(bytes.LongLength, entry.GetProperty("Length").GetInt64());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), entry.GetProperty("SHA256").GetString());

        await PackageFixtureEvidence.CompleteAsync(first, "fixture", "build positive", root, started,
            0, timedOut: false, "positive output", string.Empty, deadlineSeconds: 90);
        await PackageFixtureEvidence.CompleteAsync(second, "fixture", "build negative", root, started,
            1, timedOut: false, "negative output", "actual diagnostic", deadlineSeconds: 300);
        using JsonDocument failure = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(second, "command.json")));
        Assert.Equal(1, failure.RootElement.GetProperty("ExitCode").GetInt32());
        Assert.False(failure.RootElement.GetProperty("TimedOut").GetBoolean());
        Assert.Equal(300d, failure.RootElement.GetProperty("DeadlineSeconds").GetDouble());
        Assert.Equal(root, failure.RootElement.GetProperty("WorkingDirectory").GetString());
        Assert.Equal("actual diagnostic", await File.ReadAllTextAsync(Path.Combine(second, "stderr.log")));
        Assert.True(failure.RootElement.GetProperty("EndedAt").GetDateTimeOffset() >= started);
    }

    [Fact]
    public async Task ATimeoutWithoutAnObservedExitNeverBecomesExitZero()
    {
        string directory = PackageFixtureEvidence.Begin(Path.GetTempPath(), retain: true)!;
        await PackageFixtureEvidence.CompleteAsync(directory, "fixture", "timed out", Path.GetTempPath(), DateTimeOffset.UtcNow,
            exitCode: null, timedOut: true, "partial stdout", "partial stderr");
        using JsonDocument record = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "command.json")));
        Assert.Equal(JsonValueKind.Null, record.RootElement.GetProperty("ExitCode").ValueKind);
        Assert.True(record.RootElement.GetProperty("TimedOut").GetBoolean());
        Assert.Equal(JsonValueKind.Null, record.RootElement.GetProperty("DeadlineSeconds").ValueKind);
        Assert.Equal("partial stdout", await File.ReadAllTextAsync(Path.Combine(directory, "stdout.log")));
    }
}
