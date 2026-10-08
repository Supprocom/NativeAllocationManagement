using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

namespace Supprocom.NativeAllocationManagement.Tests;

internal static class PackageFixtureEvidence
{
    internal static string? RetainArchive(string sourceDirectory, string label)
    {
        string? destination = Environment.GetEnvironmentVariable("NAM_DURABLE_PACKAGE_EVIDENCE_ROOT");
        return string.IsNullOrEmpty(destination) ? null : ArchiveTree(sourceDirectory, destination, label);
    }

    internal static string ArchiveTree(string sourceDirectory, string destinationDirectory, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        string source = Path.GetFullPath(sourceDirectory);
        string destination = Path.GetFullPath(destinationDirectory);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (destination.Equals(source, comparison)
            || destination.StartsWith(Path.TrimEndingDirectorySeparator(source) + Path.DirectorySeparatorChar, comparison))
            throw new ArgumentException("The archive must be outside its source tree.", nameof(destinationDirectory));
        foreach (string entry in Directory.EnumerateFileSystemEntries(source, "*", SearchOption.AllDirectories))
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != (FileAttributes)0)
                throw new InvalidDataException("Retention cannot follow a link.");
        Directory.CreateDirectory(destination);
        string archivePath = Path.Combine(destination, Guid.NewGuid().ToString("N") + ".zip");
        List<ArchiveEntryIdentity> manifest = [];
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            using FileStream input = File.OpenRead(file);
            manifest.Add(new(Path.GetRelativePath(source, file).Replace('\\', '/'), input.Length,
                Convert.ToHexString(SHA256.HashData(input))));
        }
        ZipFile.CreateFromDirectory(source, archivePath, CompressionLevel.Fastest, includeBaseDirectory: false);
        using (ZipArchive archive = ZipFile.OpenRead(archivePath))
        {
            if (archive.Entries.Count(static entry => !entry.FullName.EndsWith('/')) != manifest.Count)
                throw new InvalidDataException("Retained archive file closure differs.");
            for (int index = 0; index < manifest.Count; index++)
            {
                ArchiveEntryIdentity expected = manifest[index];
                ZipArchiveEntry entry = archive.GetEntry(expected.Path)
                    ?? throw new InvalidDataException("Retained archive is missing a file.");
                using Stream retained = entry.Open();
                if (entry.Length != expected.Length
                    || !string.Equals(Convert.ToHexString(SHA256.HashData(retained)), expected.SHA256, StringComparison.Ordinal))
                    throw new InvalidDataException("Retained archive bytes differ from the produced file.");
            }
        }
        using FileStream archiveBytes = File.OpenRead(archivePath);
        File.WriteAllText(Path.ChangeExtension(archivePath, ".json"), JsonSerializer.Serialize(new
        {
            Label = label,
            SourceDirectory = source,
            Archive = archivePath,
            Length = archiveBytes.Length,
            SHA256 = Convert.ToHexString(SHA256.HashData(archiveBytes)),
            Files = manifest
        }));
        return archivePath;
    }

    private sealed record ArchiveEntryIdentity(string Path, long Length, string SHA256);

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
            new XElement("fallbackPackageFolders", new XElement("clear")),
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
        DateTimeOffset startedAt, int? exitCode, bool timedOut, string standardOutput, string standardError, double? deadlineSeconds = null,
        string? durableArchiveRoot = null)
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
        // A later intentional negative Rebuild removes the positive output.
        // Retain it at the successful run boundary, not at fixture teardown.
        // The observed command clock/deadline excludes this verification work.
        if (exitCode == 0 && !timedOut && !string.IsNullOrEmpty(durableArchiveRoot)
            && arguments.StartsWith("run ", StringComparison.Ordinal)
            && Path.GetFileName(workingDirectory).StartsWith("nam-package-smoke-", StringComparison.Ordinal))
        {
            string archive = ArchiveTree(workingDirectory, durableArchiveRoot, "successful-package-run");
            await File.WriteAllTextAsync(Path.Combine(directory, "consumer-archive.json"),
                JsonSerializer.Serialize(new { Archive = archive, CapturedAt = DateTimeOffset.UtcNow })).ConfigureAwait(false);
        }
    }
}
