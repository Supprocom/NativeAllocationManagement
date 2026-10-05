using System.Reflection;
using System.Xml.Linq;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeDiagnosticDefinitionTests
{
    [Fact]
    public void EveryPublishedObservationFieldHasItsOwnShippedDefinition()
    {
        Assembly runtime = typeof(NativeMemoryStatistics).Assembly;
        string xmlPath = Path.ChangeExtension(runtime.Location, ".xml");
        Assert.True(File.Exists(xmlPath), xmlPath);
        XDocument xml = XDocument.Load(xmlPath);
        Dictionary<string, XElement> definitions = xml.Descendants("member")
            .ToDictionary(static member => (string)member.Attribute("name")!, StringComparer.Ordinal);
        int observedFields = 0;
        foreach (Type type in runtime.GetTypes())
        {
            if (!type.IsPublic || !(type.Name.EndsWith("Statistics", StringComparison.Ordinal)
                || type.Name.EndsWith("DiagnosticSnapshot", StringComparison.Ordinal)
                || type.Name.EndsWith("TraceEvent", StringComparison.Ordinal)))
            {
                continue;
            }

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                string key = type.FullName + "." + property.Name;
                string? definition = definitions.GetValueOrDefault("P:" + key)?.Value;
                if (string.IsNullOrWhiteSpace(definition))
                {
                    definition = definitions.GetValueOrDefault("T:" + type.FullName)?.Elements("param")
                        .FirstOrDefault(parameter => string.Equals((string?)parameter.Attribute("name"), property.Name, StringComparison.Ordinal))?.Value;
                }

                Assert.False(string.IsNullOrWhiteSpace(definition), key);
                observedFields++;
            }
        }

        // Prevent an empty or narrowed reflection selection from becoming a pass.
        Assert.True(observedFields >= 251, observedFields.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
