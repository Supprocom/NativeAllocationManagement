using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;
using Xunit.Abstractions;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class PackageSmokeTests
{
    private static readonly SemaphoreSlim PackageGate = new(1, 1);
    private static PackageEvidence? _package;
    private readonly ITestOutputHelper _output;

    public PackageSmokeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task PackageReferenceDeliversRuntimeAndAnalyzerWithoutProjectReferences()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: false, suppressDiagnostics: false);
            File.WriteAllText(
                Path.Combine(consumerRoot, "Program.cs"),
                """
                using Supprocom.NativeAllocationManagement;

                public static class Consumer
                {
                    public static void Run()
                    {
                        using NativePool<int> pool = new();
                        using Pooled<int> values = pool.Rent(1);
                        values[0] = 7;
                    }
                }
                """);

            CommandResult restore = await RunDotnetAsync(
                $"restore \"{Path.Combine(consumerRoot, "Consumer.csproj")}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"",
                consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);

            CommandResult build = await RunDotnetAsync(
                $"build \"{Path.Combine(consumerRoot, "Consumer.csproj")}\" --no-restore --nologo",
                consumerRoot);
            Assert.True(build.ExitCode == 0, build.Output);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task PackageAnalyzerAcceptsTopLevelRegionUsingDeclaration()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: false, suppressDiagnostics: false, executable: true);
            File.WriteAllText(
                Path.Combine(consumerRoot, "Program.cs"),
                """
                using Supprocom.NativeAllocationManagement;

                using NativeRegion region = new();
                Local<int> value = region.Allocate<int>(1);
                value[0] = 42;
                """);

            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"",
                consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);

            CommandResult build = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo", consumerRoot);
            Assert.True(build.ExitCode == 0, build.Output);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task PackageAnalyzerRejectsTopLevelRegionLocalEscape()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: false, suppressDiagnostics: false, executable: true);
            File.WriteAllText(
                Path.Combine(consumerRoot, "Program.cs"),
                """
                using Supprocom.NativeAllocationManagement;

                using NativeRegion region = new();
                Local<int> value = region.Allocate<int>(1);
                Consumer.Take(value);

                public static class Consumer
                {
                    public static void Take(Local<int> value) { }
                }
                """);

            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"",
                consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);

            CommandResult build = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo", consumerRoot);
            Assert.True(build.ExitCode != 0, build.Output);
            Assert.Contains("NAM1012", build.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task PackageAnalyzerRejectsTopLevelNestedRegionsWithoutMisclassifyingTheLocal()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: false, suppressDiagnostics: false, executable: true);
            File.WriteAllText(
                Path.Combine(consumerRoot, "Program.cs"),
                """
                using Supprocom.NativeAllocationManagement;

                using NativeRegion outer = new();
                using NativeRegion inner = new();
                Local<int> value = outer.Allocate<int>(1);
                value[0] = 42;
                """);

            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"",
                consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);

            CommandResult build = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo", consumerRoot);
            Assert.True(build.ExitCode != 0, build.Output);
            Assert.Contains("NAM1010", build.Output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("NAM1012", build.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task PackageAnalyzerRejectsNestedRootAbandonedBeforeOwnerDisposeInIsolatedConsumer()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: false, suppressDiagnostics: false);
            File.WriteAllText(
                Path.Combine(consumerRoot, "Program.cs"),
                """
                using Supprocom.NativeAllocationManagement;

                public static class Consumer
                {
                    public static void AbandonNestedLease()
                    {
                        NativePool<int> pool = new();
                        {
                            Pooled<int> value = pool.Rent(1);
                        }

                        pool.Dispose();
                    }

                    public static void AbandonNestedLeaseBeforeOwnerDispose()
                    {
                        NativePool<int> pool = new();
                        {
                            Pooled<int> value = pool.Rent(1);
                        }

                        pool.Dispose();
                    }
                }
                """);

            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"",
                consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);

            CommandResult build = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo", consumerRoot);
            Assert.True(build.ExitCode != 0, build.Output);
            string[] diagnostics = build.Output
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.Contains("error NAM1003", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(2, diagnostics.Length);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task PackageAnalyzerRejectsStaleHandleInAnIsolatedConsumer()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: false, suppressDiagnostics: false);
            File.WriteAllText(
                Path.Combine(consumerRoot, "Program.cs"),
                """
                using Supprocom.NativeAllocationManagement;

                public static class Consumer
                {
                    public static void Run()
                    {
                        NativePool<int> pool = new();
                        Pooled<int> stale = pool.Rent(1);
                        pool.ReturnToNativeMemory();
                        _ = stale.Length;
                    }
                }
                """);

            CommandResult restore = await RunDotnetAsync(
                $"restore \"{Path.Combine(consumerRoot, "Consumer.csproj")}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"",
                consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);

            CommandResult build = await RunDotnetAsync(
                $"build \"{Path.Combine(consumerRoot, "Consumer.csproj")}\" --no-restore --nologo",
                consumerRoot);
            Assert.True(build.ExitCode != 0, build.Output);
            Assert.True(build.Output.Contains("NAM1004", StringComparison.OrdinalIgnoreCase), build.Output);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task RemovingThePackageAnalyzerFailsThroughBuildTransitiveVerification()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: true, suppressDiagnostics: false);
            File.WriteAllText(
                Path.Combine(consumerRoot, "Program.cs"),
                """
                using Supprocom.NativeAllocationManagement;

                public static class Consumer
                {
                    public static void Run()
                    {
                        using NativePool<int> pool = new();
                        using Pooled<int> values = pool.Rent(1);
                        values[0] = 7;
                    }
                }
                """);

            CommandResult restore = await RunDotnetAsync(
                $"restore \"{Path.Combine(consumerRoot, "Consumer.csproj")}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"",
                consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);

            CommandResult build = await RunDotnetAsync(
                $"build \"{Path.Combine(consumerRoot, "Consumer.csproj")}\" --no-restore --nologo",
                consumerRoot);
            Assert.True(build.ExitCode != 0, build.Output);
            Assert.True(build.Output.Contains("NAM9001", StringComparison.OrdinalIgnoreCase), build.Output);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task SuppressedAnalyzerStillGetsTheRuntimeStaleHandleGuard()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: false, suppressDiagnostics: true);
            File.WriteAllText(
                Path.Combine(consumerRoot, "Program.cs"),
                """
                using Supprocom.NativeAllocationManagement;

                public static class Consumer
                {
                    public static int Main()
                    {
                        NativePool<int> pool = new(returnOnDispose: NativeReturn.ToNativeMemory);
                        Pooled<int> stale = pool.Rent(1);
                        pool.ReturnToNativeMemory();
                        try
                        {
                            _ = stale.Length;
                            return 10;
                        }
                        catch (NativeAllocationReturnedException)
                        {
                            pool.Dispose();
                            stale.Dispose();
                        }

                        NativePool<int> guardedPool = new(returnOnDispose: NativeReturn.ToNativeMemory);
                        Pooled<int> guarded = guardedPool.Rent(1);
                        try
                        {
                            guarded.Access(_ => guardedPool.ReturnToNativeMemory());
                            return 11;
                        }
                        catch (NativeAllocationInUseException)
                        {
                            guarded.Dispose();
                            guardedPool.Dispose();
                            return 0;
                        }
                    }
                }
                """);

            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"",
                consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);

            CommandResult build = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo", consumerRoot);
            Assert.True(build.ExitCode == 0, build.Output);

            CommandResult run = await RunDotnetAsync($"run \"{project}\" --no-build --no-restore --nologo", consumerRoot);
            Assert.True(run.ExitCode == 0, run.Output);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    private void WriteEvidence(PackageEvidence package)
    {
        _output.WriteLine($"package={package.Path}");
        _output.WriteLine($"version={package.Version}");
        _output.WriteLine($"commit={package.RepositoryCommit}");
        _output.WriteLine($"artifactSha256={package.ArtifactSha256}");
        _output.WriteLine($"runtimeSha256={package.RuntimeAssemblySha256}");
        _output.WriteLine($"analyzerSha256={package.AnalyzerAssemblySha256}");
    }

    private static void WriteConsumerProject(
        string consumerRoot,
        PackageEvidence package,
        bool excludeAnalyzer,
        bool suppressDiagnostics,
        bool executable = false)
    {
        string analyzerAssets = excludeAnalyzer ? " ExcludeAssets=\"analyzers\"" : string.Empty;
        string outputType = executable || suppressDiagnostics ? "Exe" : "Library";
        string analyzerRemovalTarget = excludeAnalyzer
            ? """
              <Target Name="RemoveBundledAnalyzerAsset" BeforeTargets="NAMVerifyAnalyzerPresence">
                <ItemGroup>
                  <Analyzer Remove="@(Analyzer)" />
                </ItemGroup>
              </Target>
            """
            : string.Empty;
        string noWarn = suppressDiagnostics ? "<NoWarn>$(NoWarn);NAM1003;NAM1004;NAM1007</NoWarn>" : string.Empty;
        File.WriteAllText(
            Path.Combine(consumerRoot, "Consumer.csproj"),
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>{outputType}</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                {noWarn}
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Supprocom.NativeAllocationManagement" Version="{package.Version}"{analyzerAssets} />
              </ItemGroup>
              {analyzerRemovalTarget}
            </Project>
            """);
    }

    private static async Task<PackageEvidence> GetPackageAsync()
    {
        await PackageGate.WaitAsync();
        try
        {
            if (_package is not null)
            {
                return _package;
            }

            string repositoryRoot = FindRepositoryRoot();
            string version = "0.1.0-smoke." + Guid.NewGuid().ToString("N")[..12];
            string packageDirectory = Path.Combine(Path.GetTempPath(), "nam-package-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(packageDirectory);
            string packagePath = Path.Combine(packageDirectory, $"Supprocom.NativeAllocationManagement.{version}.nupkg");
            CommandResult pack = await RunDotnetAsync(
                $"pack Supprocom.NativeAllocationManagement\\Supprocom.NativeAllocationManagement.csproj --no-restore --nologo -c Release -p:PackageVersion={version} -p:PackageOutputPath=\"{packageDirectory}\"",
                repositoryRoot);
            Assert.True(pack.ExitCode == 0, pack.Output);
            Assert.True(File.Exists(packagePath), pack.Output);

            PackageEvidence evidence = ReadPackage(packagePath, packageDirectory, version);
            string expectedCommit = await ReadGitHeadAsync(repositoryRoot);
            Assert.Equal(expectedCommit, evidence.RepositoryCommit);
            _package = evidence;
            return evidence;
        }
        finally
        {
            PackageGate.Release();
        }
    }

    private static PackageEvidence ReadPackage(string packagePath, string sourceDirectory, string version)
    {
        using FileStream stream = File.OpenRead(packagePath);
        string artifactHash = Convert.ToHexString(SHA256.HashData(stream));
        stream.Position = 0;
        using ZipArchive archive = new(stream, ZipArchiveMode.Read, leaveOpen: false);
        ZipArchiveEntry nuspecEntry = archive.GetEntry("Supprocom.NativeAllocationManagement.nuspec")
            ?? throw new InvalidDataException("The package does not contain its nuspec.");
        using StreamReader reader = new(nuspecEntry.Open());
        XDocument nuspec = XDocument.Parse(reader.ReadToEnd());
        string commit = nuspec.Descendants().First(element => element.Name.LocalName == "repository").Attribute("commit")?.Value
            ?? throw new InvalidDataException("The package nuspec does not contain repository commit metadata.");
        string authors = nuspec.Descendants().First(element => element.Name.LocalName == "authors").Value;
        string description = nuspec.Descendants().First(element => element.Name.LocalName == "description").Value;
        XElement licenseElement = nuspec.Descendants().First(element => element.Name.LocalName == "license");
        string license = licenseElement.Attribute("type")?.Value
            ?? throw new InvalidDataException("The package nuspec does not contain license metadata.");
        Assert.Equal("Supprocom", authors);
        Assert.DoesNotContain("Package Description", description, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("expression", license);
        Assert.Equal("AGPL-3.0-only", licenseElement.Value);
        string runtimeHash = HashEntry(archive, "lib/net10.0/Supprocom.NativeAllocationManagement.dll");
        string analyzerHash = HashEntry(archive, "analyzers/dotnet/cs/Supprocom.NativeAllocationManagement.Analyzers.dll");
        return new PackageEvidence(packagePath, sourceDirectory, version, commit, artifactHash, runtimeHash, analyzerHash);
    }

    private static string HashEntry(ZipArchive archive, string name)
    {
        ZipArchiveEntry entry = archive.GetEntry(name) ?? throw new InvalidDataException($"The package does not contain {name}.");
        using Stream content = entry.Open();
        return Convert.ToHexString(SHA256.HashData(content));
    }

    private static async Task<string> ReadGitHeadAsync(string repositoryRoot)
    {
        CommandResult result = await RunProcessAsync("git", "rev-parse HEAD", repositoryRoot);
        Assert.Equal(0, result.ExitCode);
        return result.Output.Trim();
    }

    private static async Task<CommandResult> RunDotnetAsync(string arguments, string workingDirectory)
    {
        return await RunProcessAsync(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet", arguments, workingDirectory);
    }

    private static async Task<CommandResult> RunProcessAsync(string fileName, string arguments, string workingDirectory)
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        Assert.True(process.Start(), $"The {fileName} process did not start.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw new TimeoutException($"{fileName} {arguments} exceeded the 90 second smoke-test timeout.");
        }

        string output = await stdout + Environment.NewLine + await stderr;
        return new CommandResult(process.ExitCode, output);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Supprocom.NativeAllocationManagement.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("The repository root was not found from the test output directory.");
    }

    private static string CreateConsumerRoot()
    {
        string path = Path.Combine(Path.GetTempPath(), "nam-package-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteConsumerRoot(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed record PackageEvidence(
        string Path,
        string SourceDirectory,
        string Version,
        string RepositoryCommit,
        string ArtifactSha256,
        string RuntimeAssemblySha256,
        string AnalyzerAssemblySha256);

    private sealed class CommandResult
    {
        internal CommandResult(int exitCode, string output)
        {
            ExitCode = exitCode;
            Output = output;
        }

        internal int ExitCode { get; }
        internal string Output { get; }
    }
}
