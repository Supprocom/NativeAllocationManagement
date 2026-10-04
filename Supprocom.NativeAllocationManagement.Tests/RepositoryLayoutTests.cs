using System.Xml.Linq;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class RepositoryLayoutTests
{
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
