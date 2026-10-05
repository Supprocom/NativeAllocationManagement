using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

namespace Supprocom.NativeAllocationManagement.Tests;

internal static class PackageFixtureEvidence
{
    internal const string BuildProperties = """
        <Project>
          <PropertyGroup>
            <ArtifactsPath>$(MSBuildThisFileDirectory).build</ArtifactsPath>
            <UseArtifactsOutput>true</UseArtifactsOutput>
            <LangVersion>13.0</LangVersion>
          </PropertyGroup>
        </Project>
        """;

    internal static bool IsEnabled(string? selection) => string.Equals(selection, "1", StringComparison.Ordinal);

    internal static string RestoreConfiguration(string candidateSource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateSource);
        return new XDocument(new XElement("configuration",
            new XElement("packageSources", new XElement("clear"),
                new XElement("add", new XAttribute("key", "candidate"), new XAttribute("value", candidateSource)),
                new XElement("add", new XAttribute("key", "nuget.org"), new XAttribute("value", "https://api.nuget.org/v3/index.json"))),
            new XElement("packageSourceMapping",
                new XElement("packageSource", new XAttribute("key", "candidate"),
                    new XElement("package", new XAttribute("pattern", "Supprocom.NativeAllocationManagement"))),
                new XElement("packageSource", new XAttribute("key", "nuget.org"),
                    new XElement("package", new XAttribute("pattern", "*")))))).ToString();
    }

    internal static string? Begin(string workingDirectory, bool retain)
    {
        if (!retain) return null;
        string directory = Path.Combine(Path.GetTempPath(), "nam-command-evidence", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        if (!Path.GetFileName(workingDirectory).StartsWith("nam-package-smoke-", StringComparison.Ordinal)) return directory;
        string sources = Path.Combine(directory, "source");
        Directory.CreateDirectory(sources);
        List<object> manifest = [];
        foreach (string file in Directory.EnumerateFiles(workingDirectory).Order(StringComparer.Ordinal))
        {
            string extension = Path.GetExtension(file);
            if (extension is not (".cs" or ".csproj" or ".props" or ".targets" or ".config")) continue;
            string name = Path.GetFileName(file);
            string retained = Path.Combine(sources, name);
            File.Copy(file, retained, overwrite: false);
            using FileStream content = File.OpenRead(retained);
            manifest.Add(new { Name = name, Length = content.Length, SHA256 = Convert.ToHexString(SHA256.HashData(content)) });
        }
        File.WriteAllText(Path.Combine(directory, "source-manifest.json"), JsonSerializer.Serialize(manifest));
        return directory;
    }

    internal static async Task CompleteAsync(string? directory, string executable, string arguments, string workingDirectory,
        DateTimeOffset startedAt, int? exitCode, bool timedOut, string standardOutput, string standardError, double? deadlineSeconds = null)
    {
        if (directory is null) return;
        DateTimeOffset endedAt = DateTimeOffset.UtcNow;
        string record = JsonSerializer.Serialize(new
        {
            Executable = executable,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            StartedAt = startedAt,
            EndedAt = endedAt,
            ElapsedMilliseconds = (endedAt - startedAt).TotalMilliseconds,
            ExitCode = exitCode,
            TimedOut = timedOut,
            DeadlineSeconds = deadlineSeconds
        });
        await File.WriteAllTextAsync(Path.Combine(directory, "command.json"), record).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(directory, "stdout.log"), standardOutput).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(directory, "stderr.log"), standardError).ConfigureAwait(false);
    }
}
