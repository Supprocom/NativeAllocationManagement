using System.Reflection;

namespace Supprocom.NativeAllocationManagement.Tests;

internal static class RepositoryTestPaths
{
    internal static string Root
    {
        get
        {
            string? recorded = typeof(RepositoryTestPaths).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => string.Equals(attribute.Key, "NAM.RepositoryRoot", StringComparison.Ordinal))
                ?.Value;
            if (recorded is null)
            {
                throw new DirectoryNotFoundException("The test assembly has no repository root metadata.");
            }

            string path = Path.GetFullPath(recorded);
            if (!File.Exists(Path.Combine(path, "Supprocom.NativeAllocationManagement.slnx")))
            {
                throw new DirectoryNotFoundException("The recorded repository root does not contain the solution.");
            }

            return path;
        }
    }
}
