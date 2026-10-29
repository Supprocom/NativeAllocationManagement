using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

// The value-filled Scratch, ScratchScoped and Lease overloads must keep exactly the
// ownership proof of their initializer forms: each body runs with both acquisitions.
// Expected: "" none, null at least one, "*" parity only, otherwise that diagnostic.
public sealed class NativeFilledBumpAnalyzerParityTests
{
    private const string Initializer = "static writer => writer.Fill(default!)";
    private const string Filled = "0";

    [Theory]
    [InlineData("using NativeArena arena = new(); { ArenaLease<int> values = arena.Scratch<int>(4, ACQ); values.Access(static view => view[0] = 1); } arena.Reset();", "")]
    [InlineData("using NativeArena arena = new(); ArenaLease<int> values = arena.Scratch<int>(1, ACQ); arena.Reset(); values.Access(static view => view[0] = 1);", "NAM1007")]
    [InlineData("NativeArena arena = new(); { scoped ArenaLease<int> values = arena.ScratchScoped<int>(2, ACQ); values.Access(static view => view[0] = 1); } arena.RecycleScoped(); arena.Dispose();", "")]
    [InlineData("using NativeArena arena = new(); { scoped ArenaLease<int> values = arena.ScratchScoped<int>(2, ACQ); values.Access(static view => view[0] = 1); }", "NAM1020")]
    [InlineData("using NativeArena arena = new(); { ArenaLease<int> values = arena.ScratchScoped<int>(2, ACQ); values.Access(static view => view[0] = 1); } arena.RecycleScoped();", "NAM1018")]
    [InlineData("using NativeArena arena = new(); { scoped ArenaLease<int> values = arena.Scratch<int>(2, ACQ); values.Access(static view => view[0] = 1); }", "NAM1019")]
    [InlineData("using (NativeRegion region = new(4096, NativeMemoryReturn.Free)) { Local<int> values = region.Lease<int>(4, ACQ); values.Access(static view => view[0] = 1); }", "")]
    [InlineData("using NativeRegion region = new(); Local<int> values = region.Lease<int>(4, ACQ); values.Access(static view => view[0] = 1);", "*")]
    [InlineData("Local<int> outer = default; using (NativeRegion region = new()) { outer = region.Lease<int>(4, ACQ); } outer.Access(static view => view[0] = 1);", null)]
    public async Task FilledAcquisitionHasTheInitializerFormsDiagnostics(string body, string? expected)
    {
        ArgumentNullException.ThrowIfNull(body);
        string[] initializer = await NativeIdsAsync(body.Replace("ACQ", Initializer, StringComparison.Ordinal));
        string[] filled = await NativeIdsAsync(body.Replace("ACQ", Filled, StringComparison.Ordinal));
        Assert.Equal(initializer, filled);
        if (string.Equals(expected, "*", StringComparison.Ordinal))
        {
            return;
        }

        if (expected is null)
        {
            Assert.NotEmpty(filled);
        }
        else if (expected.Length == 0)
        {
            Assert.Empty(filled);
        }
        else
        {
            Assert.Contains(expected, filled, StringComparer.Ordinal);
        }
    }

    private static async Task<string[]> NativeIdsAsync(string body)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            $$"""
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run()
                {
                    {{body}}
                }
            }
            """).ConfigureAwait(false);
        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Id.StartsWith("CS", StringComparison.Ordinal) && diagnostic.Severity == DiagnosticSeverity.Error);
        string[] ids = AnalyzerContractTests.NativeDiagnostics(diagnostics);
        Array.Sort(ids, StringComparer.Ordinal);
        return ids;
    }
}
