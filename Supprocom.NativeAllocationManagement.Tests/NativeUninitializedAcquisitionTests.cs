using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed partial class NativeUninitializedAcquisitionTests
{
    [Fact]
    public void PoolRentalSkipsTheFillButKeepsLeaseAuthority()
    {
        using NativePool<int> pool = new(preLease: 8, NativeMemoryReturn.ToNativeMemory);
        Pooled<int> first = pool.RentUninitialized(8);
        Pooled<int> stale = first;
        first.AsSpan().Fill(5);
        Assert.Equal(8, first.Length);
        Assert.Equal(32, pool.GetStatistics().RequestedBytes);
        first.Dispose();
        Assert.True(IsReturned(stale));
        using Pooled<int> second = pool.RentUninitialized(8);
        second.AsSpan().Fill(6);
        Assert.Equal(6, second.AsSpan()[7]);
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.RentUninitialized(-1));
    }

    [Fact]
    public void ArenaAndRegionRangesArePublishedWithoutAFill()
    {
        using NativeArena arena = new();
        Span<long> ordinary = arena.ScratchUninitialized<long>(4).AsSpan();
        ordinary.Fill(3);
        Assert.Equal(12, ordinary[0] + ordinary[1] + ordinary[2] + ordinary[3]);
        {
            scoped ArenaLease<byte> scopedRange = arena.ScratchScopedUninitialized<byte>(16);
            scopedRange.AsSpan().Fill(1);
            Assert.Equal(16, scopedRange.Length);
        }

        arena.RecycleScoped();
        arena.Reset();
        using (NativeRegion region = new(256, NativeMemoryReturn.ToNativeMemory))
        {
            Local<double> values = region.LeaseUninitialized<double>(3);
            values.AsSpan().Fill(0.5);
            Assert.Equal(1.5, values.Read(static view => view[0] + view[1] + view[2]));
        }
    }

    [Fact]
    public void TransferIsWrittenThroughItsOwnBoundedOperations()
    {
        NativeTransfer<int> transfer = NativeTransfer<int>.CreateUninitialized(4);
        try
        {
            transfer.Access(static view => view.AsSpan().Fill(9));
            Assert.Equal(36, transfer.Read(static view => view[0] + view[1] + view[2] + view[3]));
        }
        finally
        {
            transfer.Dispose();
        }

        Assert.Throws<ArgumentOutOfRangeException>(static () => NativeTransfer<int>.CreateUninitialized(-1));
    }

    // Each body runs with the filled and the uninitialized acquisition and must
    // report the same diagnostics. Expected: "" none, otherwise that diagnostic.
    [Theory]
    [InlineData("using NativeArena arena = new(); { ArenaLease<int> values = SCRATCH(4); values.AsSpan()[0] = 1; } arena.Reset();", "")]
    [InlineData("using NativeArena arena = new(); ArenaLease<int> values = SCRATCH(1); arena.Reset(); values.Access(static view => view[0] = 1);", "NAM1007")]
    [InlineData("using NativeArena arena = new(); Span<int> s = SCRATCH(4).AsSpan(); arena.Reset(); s[0] = 1;", "NAM1051")]
    [InlineData("using NativeArena arena = new(); Span<int> s = SCRATCH(4).AsSpan(); s[0] = 1; arena.Reset();", "")]
    [InlineData("NativeArena arena = new(); { scoped ArenaLease<int> values = SCOPED(2); values.AsSpan()[0] = 1; } arena.RecycleScoped(); arena.Dispose();", "")]
    [InlineData("using NativeArena arena = new(); { ArenaLease<int> values = SCOPED(2); values.AsSpan()[0] = 1; } arena.RecycleScoped();", "NAM1018")]
    [InlineData("using (NativeRegion region = new(4096, NativeMemoryReturn.ToNativeMemory)) { LEASE(4).AsSpan()[0] = 1; }", "")]
    [InlineData("Span<int> s = default; using (NativeRegion region = new(64, NativeMemoryReturn.ToNativeMemory)) { s = LEASE(4).AsSpan(); } s[0] = 1;", "NAM1051")]
    [InlineData("using NativePool<int> pool = new(4); using Pooled<int> lease = RENT(4); lease.AsSpan()[0] = 1;", "")]
    [InlineData("using NativePool<int> pool = new(4); Pooled<int> lease = RENT(4); lease.AsSpan()[0] = 1;", "NAM1003")]
    [InlineData("using NativePool<int> pool = new(4); Span<int> s = RENT(4).AsSpan();", "NAM1013")]
    [InlineData("NativePool<int> pool = new(4); pool.Dispose(); using Pooled<int> lease = RENT(4);", "NAM1009")]
    [InlineData("using NativeConcurrentPool<int> pool = new(4); using ConcurrentPooled<int> lease = RENT(4); lease.Clear();", "")]
    [InlineData("using NativeConcurrentPool<int> pool = new(4); ConcurrentPooled<int> lease = RENT(4); lease.Clear();", "NAM1003")]
    public async Task UninitializedAcquisitionHasTheFilledFormsDiagnostics(string body, string expected)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(expected);
        string[] filled = await NativeIdsAsync(Expand(body, uninitialized: false));
        string[] uninitialized = await NativeIdsAsync(Expand(body, uninitialized: true));
        Assert.Equal(filled, uninitialized);
        if (expected.Length == 0)
        {
            Assert.Empty(uninitialized);
        }
        else
        {
            Assert.Contains(expected, uninitialized, StringComparer.Ordinal);
        }
    }

    private static string Expand(string body, bool uninitialized) =>
        Acquisition().Replace(body, match =>
        {
            string count = match.Groups["count"].Value;
            return (match.Groups["kind"].Value, uninitialized) switch
            {
                ("SCRATCH", false) => $"arena.Scratch<int>({count}, 0)",
                ("SCRATCH", true) => $"arena.ScratchUninitialized<int>({count})",
                ("SCOPED", false) => $"arena.ScratchScoped<int>({count}, 0)",
                ("SCOPED", true) => $"arena.ScratchScopedUninitialized<int>({count})",
                ("LEASE", false) => $"region.Lease<int>({count}, 0)",
                ("LEASE", true) => $"region.LeaseUninitialized<int>({count})",
                ("RENT", false) => $"pool.Rent({count}, 0)",
                _ => $"pool.RentUninitialized({count})"
            };
        });

    [GeneratedRegex(@"(?<kind>SCRATCH|SCOPED|LEASE|RENT)\((?<count>\d+)\)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Acquisition();

    private static bool IsReturned(scoped Pooled<int> lease)
    {
        try
        {
            _ = lease.AsSpan();
            return false;
        }
        catch (NativeAllocationReturnedException)
        {
            return true;
        }
    }

    private static async Task<string[]> NativeIdsAsync(string body)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            $$"""
            using System;
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
