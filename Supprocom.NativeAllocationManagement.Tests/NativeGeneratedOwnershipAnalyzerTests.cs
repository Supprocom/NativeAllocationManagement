using Microsoft.CodeAnalysis;
using Xunit.Abstractions;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeGeneratedOwnershipAnalyzerTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("NativeTransfer<int>")]
    [InlineData("NativeMemoryReservation<int>")]
    [InlineData("NativeLayoutOwner")]
    [InlineData("NativeLayoutReservation")]
    [InlineData("NativeShared<int>")]
    [InlineData("NativeWeak<int>")]
    public async Task GeneratedParameterProgramsPreserveEveryOwnershipObligation(string type)
    {
        ArgumentNullException.ThrowIfNull(type);
        // This small oracle describes the public contract, not NAM's flow states:
        // a value parameter owns cleanup; ref/in/out never establish ownership.
        foreach (bool lambda in new[] { false, true })
        {
            foreach (bool cleanup in new[] { false, true })
            {
                string body = cleanup ? "owner.Dispose();" : string.Empty;
                string member = lambda
                    ? $"public static void Run() {{ System.Action<{type}> callback = owner => {{ {body} }}; }}"
                    : $"public static void Run({type} owner) {{ {body} }}";
                await VerifyAsync(type, $"value-lambda{lambda}-cleanup{cleanup}", Wrap(member), cleanup ? [] : ["NAM1025"]);
            }
        }

        foreach (string modifier in new[] { "ref", "in", "out" })
        {
            string body = string.Equals(modifier, "out", StringComparison.Ordinal) ? "owner = default;" : string.Empty;
            string member = $"public static void Run({modifier} {type} owner) {{ {body} }}";
            await VerifyAsync(type, modifier, Wrap(member), ["NAM1027"]);
        }
    }

    [Theory]
    [InlineData("NativeTransfer<int>")]
    [InlineData("NativeMemoryReservation<int>")]
    [InlineData("NativeLayoutOwner")]
    [InlineData("NativeLayoutReservation")]
    [InlineData("NativeShared<int>")]
    [InlineData("NativeWeak<int>")]
    public async Task IdentityRequiresARealControlButNotLivePayloadAuthority(string type)
    {
        ArgumentNullException.ThrowIfNull(type);
        string defaultIdentity = Wrap($"public static void Run() {{ {type} owner = default; _ = owner.Id; }}");
        await VerifyAsync(type, "default-identity", defaultIdentity, ["NAM1022"]);
        string endedIdentity = Wrap($"public static void Run({type} owner) {{ owner.Dispose(); _ = owner.Id; }}");
        await VerifyAsync(type, "ended-identity", endedIdentity, []);
    }

    [Theory]
    [InlineData("NativeMemoryReservation<int>", "budget.TryReserve<int>(1, out NativeMemoryReservation<int>? owner, out _)")]
    [InlineData("NativeLayoutReservation", "layout.TryReserve(budget, out NativeLayoutReservation? owner, out _)")]
    public async Task UnguardedAdmissionDoesNotEstablishIdentity(string type, string acquisition)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(acquisition);
        string source = Wrap($$"""
            public static void Run(NativeMemoryBudget budget, NativeLayout layout)
            {
                {{acquisition}};
                _ = owner.Value.Id;
                owner?.Dispose();
            }
            """);
        await VerifyAsync(type, "unguarded-identity", source, ["NAM1050"]);
    }

    private async Task VerifyAsync(string type, string operation, string source, string[] expected)
    {
        Assert.DoesNotContain(AnalyzerContractTests.Compile(source), static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        string[] actual = AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source).ConfigureAwait(true))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (PackageFixtureEvidence.IsEnabled(Environment.GetEnvironmentVariable("NAM_RETAIN_PACKAGE_EVIDENCE")))
        {
            string root = Path.Combine(Path.GetTempPath(), "nam-generated-ownership", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "Program.cs"), source).ConfigureAwait(true);
            await File.WriteAllLinesAsync(Path.Combine(root, "oracle.txt"),
                [$"type={type}", $"operation={operation}", $"expected={string.Join(',', expected)}", $"actual={string.Join(',', actual)}"]).ConfigureAwait(true);
            output.WriteLine($"generatedEvidence={root}");
        }
        Assert.True(expected.SequenceEqual(actual, StringComparer.Ordinal),
            $"{type}: {operation}; expected {string.Join(',', expected)}; actual {string.Join(',', actual)}{Environment.NewLine}{source}");
    }

    private static string Wrap(string member) => $$"""
        using Supprocom.NativeAllocationManagement;
        public static class Sample
        {
            {{member}}
        }
        """;
}
