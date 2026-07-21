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
                        using NativePool<int> pool = new(doNotLeaseOnDeclaration: true);
                        pool.LeaseFromMemory();
                        {
                            Pooled<int> values = pool.Rent(1);
                            values[0] = 7;
                            values.Dispose();
                        }
                        _ = pool.TrimRetainedMemory();
                        _ = pool.TrimRetainedMemoryByBytes(1);
                        _ = pool.TrimRetainedMemoryByLeaseSize(1);

                        using (NativeRegion region = new(doNotLeaseOnDeclaration: true))
                        {
                            region.LeaseFromMemory();
                            Local<int> local = region.Lease<int>(1);
                            local[0] = 7;
                            _ = region.TrimRetainedMemory();
                            _ = region.TrimRetainedMemoryByBytes(1);
                            _ = region.TrimRetainedMemoryByLeaseSize<int>(1);
                            {
                                scoped Local<int> scopedLocal = region.LeaseScoped<int>(1);
                                scopedLocal[0] = 11;
                            }
                            region.RecycleScoped();
                        }

                        using NativeArena arena = new(doNotLeaseOnDeclaration: true);
                        arena.LeaseFromMemory();
                        {
                            ArenaLease<string> labels = arena.Scratch<string>(1);
                            labels[0] = "package";
                        }

                        arena.ReleaseLeasesToNativeMemory();
                        _ = arena.TrimRetainedMemory();
                        _ = arena.TrimRetainedMemoryByBytes(1);
                        _ = arena.TrimRetainedMemoryByLeaseSize<int>(1);

                        {
                            scoped ArenaLease<int> scopedValues = arena.ScratchScoped<int>(1);
                            scopedValues[0] = 9;
                        }

                        arena.RecycleScoped();
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
    public async Task PackageAnalyzerAcceptsExplicitRegionUsingStatementWithDelayedActivation()
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

                using (NativeRegion region = new(doNotLeaseOnDeclaration: true))
                {
                    region.LeaseFromMemory();
                    Local<int> value = region.Lease<int>(1);
                    value[0] = 42;
                }
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
    public async Task PackageAnalyzerRejectsTopLevelRegionUsingDeclarationWithoutLocalEscape()
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

                using NativeRegion region = new(doNotLeaseOnDeclaration: true);
                region.LeaseFromMemory();
                Local<int> value = region.Lease<int>(1);
                value[0] = 42;
                """);

            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"",
                consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);

            CommandResult build = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo", consumerRoot);
            Assert.True(build.ExitCode != 0, build.Output);
            Assert.Contains("NAM1006", build.Output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("NAM1012", build.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task PackageAnalyzerRejectsRegionUsingDeclarationsWithoutNestedOrLocalDiagnostics()
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

                using NativeRegion outer = new(doNotLeaseOnDeclaration: true);
                using NativeRegion inner = new(doNotLeaseOnDeclaration: true);
                outer.LeaseFromMemory();
                inner.LeaseFromMemory();
                Local<int> value = outer.Lease<int>(1);
                value[0] = 42;
                """);

            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"",
                consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);

            CommandResult build = await RunDotnetAsync($"build \"{project}\" --no-restore --nologo", consumerRoot);
            Assert.True(build.ExitCode != 0, build.Output);
            Assert.Contains("NAM1006", build.Output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("NAM1010", build.Output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("NAM1012", build.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task PackageAnalyzerRejectsBlockRegionUsingDeclarationWithoutLocalEscape()
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
                        using NativeRegion region = new(doNotLeaseOnDeclaration: true);
                        region.LeaseFromMemory();
                        Local<int> value = region.Lease<int>(1);
                        value[0] = 42;
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
            Assert.Contains("NAM1006", build.Output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("NAM1012", build.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task PackageAnalyzerRejectsPreActivationUse()
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
                        NativePool<int> pool = new(doNotLeaseOnDeclaration: true);
                        _ = pool.Rent(1);
                        pool.Dispose();

                        using (NativeRegion region = new(doNotLeaseOnDeclaration: true))
                        {
                            Local<int> value = region.Lease<int>(1);
                            _ = value.Length;
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
            Assert.True(build.ExitCode != 0, build.Output);
            Assert.Contains("NAM1009", build.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task PackageRegionRequiresLeaseOperation()
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
                        using (NativeRegion region = new())
                        {
                            _ = region.Allocate<int>(1);
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
            Assert.True(build.ExitCode != 0, build.Output);
            Assert.Contains("CS1061", build.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task PackageRejectsTrimOnDerivedHandlesAndRemovedLifecycleSpellings()
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
                        Pooled<int> value = pool.Rent(1);
                        _ = value.TrimRetainedMemory();
                        pool.ReturnToNativeMemory();
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
            Assert.Contains("CS1061", build.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("TrimRetainedMemory", build.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ReturnToNativeMemory", build.Output, StringComparison.OrdinalIgnoreCase);
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
                        pool.ReturnMemoryToNativeMemory();
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
    public async Task DeferredReturnWarningFollowsConsumerWarningsAsErrorsPolicy()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(
                consumerRoot,
                package,
                excludeAnalyzer: false,
                suppressDiagnostics: false);
            File.WriteAllText(
                Path.Combine(consumerRoot, "Consumer.cs"),
                """
                using Supprocom.NativeAllocationManagement;

                public static class Consumer
                {
                    public static void Run()
                    {
                        NativePool<int> pool = new();
                        Pooled<int> values = pool.Rent(1);
                        pool.ReturnMemoryToGarbageCollector();
                        pool.Dispose();
                    }
                }
                """);

            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"",
                consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);

            CommandResult warningBuild = await RunDotnetAsync(
                $"build \"{project}\" --no-restore --nologo",
                consumerRoot);
            Assert.True(warningBuild.ExitCode == 0, warningBuild.Output);
            Assert.Contains("warning NAM1017", warningBuild.Output, StringComparison.OrdinalIgnoreCase);

            WriteConsumerProject(
                consumerRoot,
                package,
                excludeAnalyzer: false,
                suppressDiagnostics: false,
                treatWarningsAsErrors: true);
            CommandResult errorBuild = await RunDotnetAsync(
                $"build \"{project}\" --no-restore --nologo",
                consumerRoot);
            Assert.True(errorBuild.ExitCode != 0, errorBuild.Output);
            Assert.Contains("error NAM1017", errorBuild.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task NativeMemoryReturnLiveRootIsAHardPackageAnalyzerError()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: false, suppressDiagnostics: false);
            File.WriteAllText(
                Path.Combine(consumerRoot, "Consumer.cs"),
                """
                using Supprocom.NativeAllocationManagement;

                public static class Consumer
                {
                    public static void Run()
                    {
                        NativePool<int> pool = new();
                        Pooled<int> value = pool.Rent(1);
                        pool.ReturnMemoryToNativeMemory();
                        pool.Dispose();
                    }
                }
                """);

            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"",
                consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);

            CommandResult build = await RunDotnetAsync(
                $"build \"{project}\" --no-restore --nologo",
                consumerRoot);
            Assert.True(build.ExitCode != 0, build.Output);
            Assert.Contains("error NAM1007", build.Output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("NAM1017", build.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteConsumerRoot(consumerRoot);
        }
    }

    [Fact]
    public async Task ScopedCompletionWarningFollowsConsumerWarningsAsErrorsPolicy()
    {
        PackageEvidence package = await GetPackageAsync();
        WriteEvidence(package);
        string consumerRoot = CreateConsumerRoot();
        try
        {
            WriteConsumerProject(consumerRoot, package, excludeAnalyzer: false, suppressDiagnostics: false);
            File.WriteAllText(
                Path.Combine(consumerRoot, "Consumer.cs"),
                """
                using Supprocom.NativeAllocationManagement;

                public static class Consumer
                {
                    public static void Run()
                    {
                        using NativeArena arena = new();
                        {
                            scoped ArenaLease<int> values = arena.ScratchScoped<int>(1);
                            values[0] = 1;
                        }
                    }
                }
                """);

            string project = Path.Combine(consumerRoot, "Consumer.csproj");
            CommandResult restore = await RunDotnetAsync(
                $"restore \"{project}\" --nologo --force --no-cache --packages \"{Path.Combine(consumerRoot, ".packages")}\" --source \"{package.SourceDirectory}\"",
                consumerRoot);
            Assert.True(restore.ExitCode == 0, restore.Output);

            CommandResult warningBuild = await RunDotnetAsync(
                $"build \"{project}\" --no-restore --nologo",
                consumerRoot);
            Assert.True(warningBuild.ExitCode == 0, warningBuild.Output);
            Assert.Contains("warning NAM1020", warningBuild.Output, StringComparison.OrdinalIgnoreCase);

            WriteConsumerProject(
                consumerRoot,
                package,
                excludeAnalyzer: false,
                suppressDiagnostics: false,
                treatWarningsAsErrors: true);
            CommandResult errorBuild = await RunDotnetAsync(
                $"build \"{project}\" --no-restore --nologo",
                consumerRoot);
            Assert.True(errorBuild.ExitCode != 0, errorBuild.Output);
            Assert.Contains("error NAM1020", errorBuild.Output, StringComparison.OrdinalIgnoreCase);
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
                        NativePool<int> deferredPool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
                        Pooled<int> borrowed = deferredPool.Rent(1);
                        bool callbackCompleted = false;
                        borrowed.Access(span =>
                        {
                            deferredPool.ReturnMemoryToGarbageCollector();
                            span[0] = 42;
                            callbackCompleted = span[0] == 42;
                        });
                        if (!callbackCompleted)
                        {
                            return 12;
                        }

                        try
                        {
                            _ = borrowed.Length;
                            return 13;
                        }
                        catch (NativeAllocationReturnedException)
                        {
                            borrowed.Dispose();
                        }

                        deferredPool.LeaseFromMemory();
                        Pooled<int> current = deferredPool.Rent(1);
                        if (current[0] != 0)
                        {
                            return 14;
                        }

                        current.Dispose();
                        deferredPool.Dispose();

                        NativePool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
                        Pooled<int> stale = pool.Rent(1);
                        pool.ReturnMemoryToNativeMemory();
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

                        NativePool<int> guardedPool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
                        Pooled<int> guarded = guardedPool.Rent(1);
                        try
                        {
                            guarded.Access(_ => guardedPool.ReturnMemoryToNativeMemory());
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
        bool executable = false,
        bool treatWarningsAsErrors = false)
    {
        string analyzerAssets = excludeAnalyzer ? " ExcludeAssets=\"analyzers\"" : string.Empty;
        string outputType = executable || suppressDiagnostics ? "Exe" : "Library";
        string warningsAsErrors = treatWarningsAsErrors
            ? "<TreatWarningsAsErrors>true</TreatWarningsAsErrors>"
            : string.Empty;
        string analyzerRemovalTarget = excludeAnalyzer
            ? """
              <Target Name="RemoveBundledAnalyzerAsset" BeforeTargets="NAMVerifyAnalyzerPresence">
                <ItemGroup>
                  <Analyzer Remove="@(Analyzer)" />
                </ItemGroup>
              </Target>
            """
            : string.Empty;
        string noWarn = suppressDiagnostics
            ? "<NoWarn>$(NoWarn);NAM1003;NAM1004;NAM1007;NAM1017</NoWarn>"
            : string.Empty;
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
                {warningsAsErrors}
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
            string version = "0.1.3-smoke." + Guid.NewGuid().ToString("N")[..12];
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
