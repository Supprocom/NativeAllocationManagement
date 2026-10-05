using System.Text.Json;
using System.Xml.Linq;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class RepositoryLayoutTests
{
    [Fact]
    public void CanonicalToolchainUsesStableLanguageAndExactSdk()
    {
        string root = RepositoryTestPaths.Root;
        XDocument properties = XDocument.Load(Path.Combine(root, "Directory.Build.props"));
        XElement[] languages = properties.Descendants("LangVersion").ToArray();
#pragma warning disable HLQ005 // Xunit.Assert.Single verifies required cardinality; this is not a LINQ Single/First operation.
        Assert.Equal("13.0", Assert.Single(languages).Value);
#pragma warning restore HLQ005
        using JsonDocument selection = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "global.json")));
        JsonElement sdk = selection.RootElement.GetProperty("sdk");
        Assert.Equal("10.0.302", sdk.GetProperty("version").GetString());
        Assert.Equal("disable", sdk.GetProperty("rollForward").GetString());
        Assert.False(sdk.GetProperty("allowPrerelease").GetBoolean());
        Assert.Contains("<LangVersion>13.0</LangVersion>", PackageFixtureEvidence.BuildProperties, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeDeploymentAnalysisIsRequiredRatherThanSuppressed()
    {
        string project = Path.Combine(RepositoryTestPaths.Root, "Supprocom.NativeAllocationManagement", "Supprocom.NativeAllocationManagement.csproj");
        XDocument runtime = XDocument.Load(project);
        XElement[] compatibility = runtime.Descendants("IsAotCompatible").ToArray();
#pragma warning disable HLQ005 // Xunit.Assert.Single verifies required cardinality; First would hide duplicate configuration.
        Assert.Equal("true", Assert.Single(compatibility).Value);
#pragma warning restore HLQ005
        Assert.Empty(runtime.Descendants("NoWarn"));
        Assert.Empty(runtime.Descendants("EnableTrimAnalyzer"));
        Assert.Empty(runtime.Descendants("EnableAotAnalyzer"));
    }

    [Fact]
    public void AgentInstructionFilesRemainExcludedByBothCaseInsensitiveRules()
    {
        string policy = File.ReadAllText(Path.Combine(RepositoryTestPaths.Root, ".gitignore"));
        Assert.Contains("**/[Aa][Gg][Ee][Nn][Tt].[Mm][Dd]", policy, StringComparison.Ordinal);
        Assert.Contains("**/[Aa][Gg][Ee][Nn][Tt][Ss].[Mm][Dd]", policy, StringComparison.Ordinal);
    }

    [Fact]
    public void SolutionContainsEveryDirectRootProjectWithoutGroupingDirectories()
    {
        string root = RepositoryTestPaths.Root;
        XDocument solution = XDocument.Load(Path.Combine(root, "Supprocom.NativeAllocationManagement.slnx"));
        string[] projects = solution.Descendants("Project")
            .Select(project => Assert.IsType<string>(project.Attribute("Path")?.Value))
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] actual = Directory.EnumerateDirectories(root)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.csproj"))
            .Select(project => Path.GetRelativePath(root, project).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(projects);
        Assert.Equal(actual, projects);
        foreach (string project in projects)
        {
            string[] parts = project.Split('/');
            Assert.Equal(2, parts.Length);
            Assert.Equal(parts[0], Path.GetFileNameWithoutExtension(parts[1]));
            Assert.True(File.Exists(Path.Combine(root, project)));
        }
    }
}
