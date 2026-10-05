using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

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

    [Theory]
    [InlineData("C:\\candidate feed\\packages")]
    [InlineData("/candidate & quoted \"feed\"/packages")]
    public void RestoreConfigurationPreservesSourcesAndPinsOnlyNamToTheCandidate(string candidate)
    {
        XDocument configuration = XDocument.Parse(PackageFixtureEvidence.RestoreConfiguration(candidate));
        XElement sources = configuration.Root!.Element("packageSources")!;
        Assert.NotNull(sources.Element("clear"));
        Assert.Collection(configuration.Root.Element("fallbackPackageFolders")!.Elements(),
            clear => Assert.Equal("clear", clear.Name.LocalName));
        Assert.Collection(sources.Elements("add"),
            local =>
            {
                Assert.Equal("candidate", (string?)local.Attribute("key"));
                Assert.Equal(candidate, (string?)local.Attribute("value"));
            },
            remote =>
            {
                Assert.Equal("nuget.org", (string?)remote.Attribute("key"));
                Assert.Equal("https://api.nuget.org/v3/index.json", (string?)remote.Attribute("value"));
            });
        Assert.Collection(configuration.Root.Element("packageSourceMapping")!.Elements(),
            local =>
            {
                Assert.Equal("candidate", (string?)local.Attribute("key"));
                Assert.Equal("Supprocom.NativeAllocationManagement", (string?)local.Element("package")!.Attribute("pattern"));
            },
            remote =>
            {
                Assert.Equal("nuget.org", (string?)remote.Attribute("key"));
                Assert.Equal("*", (string?)remote.Element("package")!.Attribute("pattern"));
            });
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t")]
    public void RestoreConfigurationRejectsAnAbsentCandidate(string candidate)
        => Assert.Throws<ArgumentException>(() => PackageFixtureEvidence.RestoreConfiguration(candidate));

    [Fact]
    public void RestoreConfigurationRejectsNullBeforeWritingAnything()
        => Assert.Throws<ArgumentNullException>(() => PackageFixtureEvidence.RestoreConfiguration(null!));

    [Fact]
    public async Task RestoreRejectsAnOtherwiseUsableInheritedFallbackPackage()
    {
        string root = Path.Combine(Path.GetTempPath(), "nam-restore-isolation-" + Guid.NewGuid().ToString("N"));
        string feed = Path.Combine(root, "feed");
        string missingFeed = Path.Combine(root, "empty-feed");
        string fallback = Path.Combine(root, "fallback");
        Directory.CreateDirectory(feed);
        Directory.CreateDirectory(missingFeed);
        const string version = "0.0.0-isolation";
        string package = Path.Combine(feed, $"Supprocom.NativeAllocationManagement.{version}.nupkg");
        ZipArchive archive = await ZipFile.OpenAsync(package, ZipArchiveMode.Create).ConfigureAwait(true);
        await using (archive.ConfigureAwait(true))
        {
            ZipArchiveEntry entry = archive.CreateEntry("Supprocom.NativeAllocationManagement.nuspec");
            StreamWriter writer = new(await entry.OpenAsync().ConfigureAwait(true));
            await using (writer.ConfigureAwait(true))
            {
                await writer.WriteAsync($"""
                    <package><metadata><id>Supprocom.NativeAllocationManagement</id><version>{version}</version>
                    <authors>Isolation fixture</authors><description>Fallback exclusion control only.</description>
                    </metadata></package>
                    """).ConfigureAwait(true);
            }
        }

        string seed = CreateRestoreProject(root, "seed", version);
        string seedConfiguration = Path.Combine(seed, "NuGet.config");
        await File.WriteAllTextAsync(seedConfiguration, PackageFixtureEvidence.RestoreConfiguration(feed));
        PackageSmokeTests.CommandResult seeded = await PackageSmokeTests.RunDotnetAsync(
            $"restore --nologo --force --no-cache --packages \"{fallback}\" --configfile \"{seedConfiguration}\"", seed);
        Assert.True(seeded.ExitCode == 0, seeded.Output);

        XDocument inherited = new(new XElement("configuration",
            new XElement("packageSources", new XElement("clear"),
                new XElement("add", new XAttribute("key", "empty"), new XAttribute("value", missingFeed))),
            new XElement("fallbackPackageFolders", new XElement("clear"),
                new XElement("add", new XAttribute("key", "control"), new XAttribute("value", fallback)))));
        await File.WriteAllTextAsync(Path.Combine(root, "NuGet.config"), inherited.ToString());
        string control = CreateRestoreProject(root, "control", version);
        await File.WriteAllTextAsync(Path.Combine(control, "Inherited.config"), inherited.ToString());
        string controlCache = Path.Combine(control, ".packages");
        PackageSmokeTests.CommandResult accepted = await PackageSmokeTests.RunDotnetAsync(
            $"restore --nologo --force --no-cache --packages \"{controlCache}\"", control);
        Assert.True(accepted.ExitCode == 0, accepted.Output);
        using JsonDocument controlAssets = JsonDocument.Parse(await File.ReadAllTextAsync(AssetsPath(control)));
        bool fallbackAdvertised = false;
        foreach (JsonProperty folder in controlAssets.RootElement.GetProperty("packageFolders").EnumerateObject())
        {
            fallbackAdvertised |= string.Equals(Path.TrimEndingDirectorySeparator(folder.Name), fallback, StringComparison.Ordinal);
        }
        Assert.True(fallbackAdvertised);
        Assert.False(Directory.Exists(Path.Combine(controlCache, "supprocom.nativeallocationmanagement")));

        string isolated = CreateRestoreProject(root, "isolated", version);
        await File.WriteAllTextAsync(Path.Combine(isolated, "NuGet.config"),
            PackageFixtureEvidence.RestoreConfiguration(missingFeed));
        PackageSmokeTests.CommandResult rejected = await PackageSmokeTests.RunDotnetAsync(
            $"restore --nologo --force --no-cache --packages \"{Path.Combine(isolated, ".packages")}\"", isolated);
        Assert.NotEqual(0, rejected.ExitCode);
        Assert.Contains("NU1101", rejected.Output, StringComparison.Ordinal);
        using JsonDocument isolatedAssets = JsonDocument.Parse(await File.ReadAllTextAsync(AssetsPath(isolated)));
        int folderCount = 0;
        foreach (JsonProperty folder in isolatedAssets.RootElement.GetProperty("packageFolders").EnumerateObject())
        {
            ++folderCount;
            Assert.Equal(Path.Combine(isolated, ".packages"), Path.TrimEndingDirectorySeparator(folder.Name));
        }
        Assert.Equal(1, folderCount);
    }

    private static string CreateRestoreProject(string parent, string name, string version)
    {
        string root = Path.Combine(parent, "nam-package-smoke-" + name);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Directory.Build.props"), PackageFixtureEvidence.BuildProperties);
        File.WriteAllText(Path.Combine(root, "Consumer.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            <ItemGroup><PackageReference Include="Supprocom.NativeAllocationManagement" Version="{version}" /></ItemGroup></Project>
            """);
        return root;
    }

    private static string AssetsPath(string root) => Path.Combine(root, ".build", "obj", "Consumer", "project.assets.json");

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
        string configurationPath = Path.Combine(root, "NuGet.config");
        string firstConfiguration = PackageFixtureEvidence.RestoreConfiguration(Path.Combine(root, "first-candidate"));
        string secondConfiguration = PackageFixtureEvidence.RestoreConfiguration(Path.Combine(root, "second-candidate"));
        await File.WriteAllTextAsync(configurationPath, firstConfiguration);
        DateTimeOffset started = DateTimeOffset.UtcNow;
        string first = PackageFixtureEvidence.Begin(root, retain: true)!;
        await File.WriteAllTextAsync(program, negative);
        await File.WriteAllTextAsync(configurationPath, secondConfiguration);
        string second = PackageFixtureEvidence.Begin(root, retain: true)!;
        Assert.False(string.Equals(first, second, StringComparison.Ordinal));
        Assert.Equal(positive, await File.ReadAllTextAsync(Path.Combine(first, "source", "Program.cs")));
        Assert.Equal(negative, await File.ReadAllTextAsync(Path.Combine(second, "source", "Program.cs")));
        Assert.Equal(firstConfiguration, await File.ReadAllTextAsync(Path.Combine(first, "source", "NuGet.config")));
        Assert.Equal(secondConfiguration, await File.ReadAllTextAsync(Path.Combine(second, "source", "NuGet.config")));
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
