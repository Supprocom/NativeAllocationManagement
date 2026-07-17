using System.Diagnostics;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class PackageSmokeTests
{
    [Fact]
    public async Task PackageReferenceDeliversRuntimeAndAnalyzerWithoutProjectReferences()
    {
        string repositoryRoot = FindRepositoryRoot();
        string packageDirectory = Path.Combine(repositoryRoot, "artifacts", "packages");
        string packagePath = Path.Combine(packageDirectory, "Supprocom.NativeAllocationManagement.0.1.0.nupkg");
        await EnsurePackageAsync(repositoryRoot, packagePath);

        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, excludeAnalyzer: false);
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
                $"restore \"{Path.Combine(consumerRoot, "Consumer.csproj")}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{Path.GetDirectoryName(packagePath)!}\"",
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
    public async Task PackageAnalyzerRejectsStaleHandleInAnIsolatedConsumer()
    {
        string repositoryRoot = FindRepositoryRoot();
        string packagePath = Path.Combine(repositoryRoot, "artifacts", "packages", "Supprocom.NativeAllocationManagement.0.1.0.nupkg");
        await EnsurePackageAsync(repositoryRoot, packagePath);

        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, excludeAnalyzer: false);
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
                $"restore \"{Path.Combine(consumerRoot, "Consumer.csproj")}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{Path.GetDirectoryName(packagePath)!}\"",
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
        string repositoryRoot = FindRepositoryRoot();
        string packagePath = Path.Combine(repositoryRoot, "artifacts", "packages", "Supprocom.NativeAllocationManagement.0.1.0.nupkg");
        await EnsurePackageAsync(repositoryRoot, packagePath);

        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, excludeAnalyzer: true);
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
                $"restore \"{Path.Combine(consumerRoot, "Consumer.csproj")}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{Path.GetDirectoryName(packagePath)!}\"",
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

    private static void WriteConsumerProject(string consumerRoot, bool excludeAnalyzer)
    {
        string analyzerAssets = excludeAnalyzer ? " ExcludeAssets=\"analyzers\"" : string.Empty;
        string analyzerRemovalTarget = excludeAnalyzer
            ? """
              <Target Name="RemoveBundledAnalyzerAsset" BeforeTargets="NAMVerifyAnalyzerPresence">
                <ItemGroup>
                  <Analyzer Remove="@(Analyzer)" />
                </ItemGroup>
              </Target>
            """
            : string.Empty;
        File.WriteAllText(
            Path.Combine(consumerRoot, "Consumer.csproj"),
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Supprocom.NativeAllocationManagement" Version="0.1.0"{analyzerAssets} />
              </ItemGroup>
              {analyzerRemovalTarget}
            </Project>
            """);
    }

    private static async Task EnsurePackageAsync(string repositoryRoot, string packagePath)
    {
        if (File.Exists(packagePath))
        {
            return;
        }

        CommandResult pack = await RunDotnetAsync(
            "pack Supprocom.NativeAllocationManagement\\Supprocom.NativeAllocationManagement.csproj --no-restore --nologo -c Release",
            repositoryRoot);
        Assert.Equal(0, pack.ExitCode);
        Assert.True(File.Exists(packagePath), pack.Output);
    }

    private static async Task<CommandResult> RunDotnetAsync(string arguments, string workingDirectory)
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        Assert.True(process.Start(), "The dotnet process did not start.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
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

            throw new TimeoutException($"dotnet {arguments} exceeded the 60 second smoke-test timeout.");
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
