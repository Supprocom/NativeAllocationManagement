using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeSharingAnalyzerTests
{
    [Fact]
    public async Task UniqueConversionAndLexicalSharedCleanupAreAccepted()
    {
        string source = Wrap("""
            using NativeShared<int> shared = NativeShared<int>.Create(ref source, new(2, 1));
            _ = shared.Read(static view => view[0]);
            """);
        AssertCompiles(source);
        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)));
    }

    [Fact]
    public async Task ExplicitGuardedStrongWeakAndUpgradeBindingsHaveIndependentCleanup()
    {
        string source = Wrap("""
            using NativeShared<int> shared = NativeShared<int>.Create(ref source, new(3, 1));
            if (!shared.TryShare(out NativeShared<int> share, out _)) return;
            try
            {
                if (!share.TryDowngrade(out NativeWeak<int> weak, out _)) return;
                try
                {
                    if (weak.TryUpgrade(out NativeShared<int> upgraded, out _))
                    {
                        try { _ = upgraded.Read(static view => view[0]); }
                        finally { upgraded.Dispose(); }
                    }
                }
                finally { weak.Dispose(); }
            }
            finally { share.Dispose(); }
            """);
        AssertCompiles(source);
        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)));
    }

    [Theory]
    [InlineData("TryShare(out NativeShared<int> acquired, out _)")]
    [InlineData("TrySlice(0, 1, out NativeShared<int> acquired, out _)")]
    [InlineData("TryDowngrade(out NativeWeak<int> acquired, out _)")]
    [InlineData("TryDetach(new NativeMemoryBudget(4), out NativeTransfer<int> acquired)")]
    public async Task IgnoredAcquisitionResultCannotGrantAuthority(string call)
    {
        string source = Wrap($$"""
            using NativeShared<int> shared = NativeShared<int>.Create(ref source, new(2, 1));
            shared.{{call}};
            acquired.Dispose();
            """);
        AssertCompiles(source);
        Assert.Contains("NAM1050", AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)), StringComparer.Ordinal);
    }

    [Fact]
    public async Task FailedGuardCannotUseDefaultAcquisitionOutput()
    {
        string source = Wrap("""
            using NativeShared<int> shared = NativeShared<int>.Create(ref source, new(2, 1));
            if (shared.TryShare(out NativeShared<int> acquired, out _))
            { acquired.Dispose(); return; }
            acquired.Dispose();
            """);
        AssertCompiles(source);
        Assert.Contains("NAM1022", AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)), StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("NativeShared<int> alias = shared; alias.Dispose();")]
    [InlineData("object alias = shared;")]
    [InlineData("NativeShared<int>[] aliases = [shared];")]
    public async Task AssignmentBoxingAndStorageDoNotAcquireIndependentBindings(string operation)
    {
        string source = Wrap($$"""
            using NativeShared<int> shared = NativeShared<int>.Create(ref source, new(2, 1));
            {{operation}}
            """);
        AssertCompiles(source);
        Assert.Contains("NAM1021", AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)), StringComparer.Ordinal);
    }

    [Fact]
    public async Task ConversionInvalidatesUniqueAliasesAndRejectsUsingSources()
    {
        string source = Wrap("""
            using NativeShared<int> shared = NativeShared<int>.Create(ref source, new(1, 0));
            source.Value.Access(static view => view[0] = 99);
            """);
        AssertCompiles(source);
        Assert.Contains("NAM1022", AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)), StringComparer.Ordinal);
    }

    [Fact]
    public async Task SharedAcquisitionRequiresDeterministicCleanup()
    {
        string source = Wrap("""
            NativeShared<int> shared = NativeShared<int>.Create(ref source, new(1, 0));
            _ = shared.Length;
            """);
        AssertCompiles(source);
        Assert.Contains("NAM1025", AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)), StringComparer.Ordinal);
    }

    [Fact]
    public async Task ReleasedBindingsCanObserveCleanupButCannotBorrowAgain()
    {
        string source = Wrap("""
            NativeShared<int> shared = NativeShared<int>.Create(ref source, new(1, 0));
            shared.Dispose();
            _ = shared.CaptureSnapshot();
            _ = shared.TryCompletePayloadReturn();
            """);
        AssertCompiles(source);
        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)));
        string inactive = source.Replace("_ = shared.CaptureSnapshot();", "_ = shared.Read(static view => view[0]);", StringComparison.Ordinal);
        Assert.Contains("NAM1022", AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(inactive)), StringComparer.Ordinal);
    }

    [Fact]
    public async Task DefaultBindingsCannotUseDiagnosticCallsToGainAnOwner()
    {
        const string source = """
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run() { NativeShared<int> shared = default; _ = shared.CaptureSnapshot(); }
            }
            """;
        AssertCompiles(source);
        Assert.Contains("NAM1022", AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)), StringComparer.Ordinal);
    }

    [Fact]
    public async Task SameNamedForeignSharingTypesAreNotNativeOwnership()
    {
        const string source = """
            using System;
            public readonly struct NativeShared<T> : IDisposable
            {
                public static NativeShared<T> Create() => default;
                public void Dispose() { }
            }
            public static class Sample
            {
                public static void Run()
                { NativeShared<int> shared = NativeShared<int>.Create(); NativeShared<int> alias = shared; alias.Dispose(); }
            }
            """;
        AssertCompiles(source);
        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)));
    }

    [Fact]
    public async Task ReadOnlyViewsStillRejectUnscopedEscapeBoundaries()
    {
        string source = Wrap("""
            using NativeShared<int> shared = NativeShared<int>.Create(ref source, new(1, 0));
            shared.Access(static view => Consume(view));
            """).Replace("public static void Run()", "private static void Consume(NativeReadOnlyLeaseView<int> view) { _ = view.Length; } public static void Run()", StringComparison.Ordinal);
        AssertCompiles(source);
        Assert.Contains("NAM1024", AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)), StringComparer.Ordinal);
    }

    [Fact]
    public async Task CollectionExpressionsCannotAliasUniqueOwnershipEither()
    {
        string source = Wrap("""
            NativeTransfer<int>[] aliases = [source.Value];
            source.Value.Dispose();
            """);
        AssertCompiles(source);
        Assert.Contains("NAM1021", AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)), StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("source?.Dispose();")]
    [InlineData("if (source.HasValue) source.Value.Dispose();")]
    public async Task NullableSourceCleanupCoversPreparedConversionAndMultipleEarlyExits(string cleanup)
    {
        string source = Wrap($$"""
            try
            {
                NativeShared<int> shared = NativeShared<int>.Create(ref source, new(2, 1));
                if (!shared.TryDowngrade(out NativeWeak<int> weak, out _)) { shared.Dispose(); return; }
                try
                {
                    try
                    {
                        if (source.HasValue || shared.Read(static view => view[0]) != 42) return;
                        if (!shared.TryShare(out NativeShared<int> share, out _)) return;
                        try { _ = share.Read(static view => view[0]); }
                        finally { share.Dispose(); }
                    }
                    finally { shared.Dispose(); }
                    _ = weak.IsExpired;
                }
                finally { weak.Dispose(); }
            }
            finally { {{cleanup}} }
            """);
        AssertCompiles(source);
        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)));
    }

    [Theory]
    [InlineData("source?.Dispose();")]
    [InlineData("if (source.HasValue) source.Value.Dispose();")]
    public async Task NullableCleanupCannotHideAlreadyReleasedAuthority(string cleanup)
    {
        string source = Wrap($$"""
            source.Value.Dispose();
            {{cleanup}}
            """);
        AssertCompiles(source);
        Assert.Contains("NAM1022", AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)), StringComparer.Ordinal);
    }

    [Fact]
    public async Task NullablePresenceDoesNotGrantAuthorityToADefaultValue()
    {
        const string source = """
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run()
                {
                    NativeTransfer<int>? source = default(NativeTransfer<int>);
                    source?.Dispose();
                }
            }
            """;
        AssertCompiles(source);
        Assert.Contains("NAM1022", AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)), StringComparer.Ordinal);
    }

    private static string Wrap(string body) => $$"""
        using Supprocom.NativeAllocationManagement;
        public static class Sample
        {
            public static void Run()
            {
                using NativeBuilder<int> builder = new(4);
                builder.Append(42);
                NativeTransfer<int>? source = builder.Complete();
                {{body}}
            }
        }
        """;

    private static void AssertCompiles(string source) =>
        Assert.DoesNotContain(AnalyzerContractTests.Compile(source), static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
}
