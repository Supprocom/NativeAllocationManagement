using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeSpecializedPreparedSafetyAnalyzerTests
{
    [Theory]
    [InlineData("using PreparedPooled<int> lease = pool.Rent(1, static writer => writer.Write(7)); lease.Access(_ => pool.Dispose());", "NAM1005")]
    [InlineData("using PreparedPooled<int> lease = pool.Rent(1, static writer => writer.Write(7)); External(lease);", "NAM1016")]
    [InlineData("using PreparedPooled<int> lease = pool.Rent(1, static writer => writer.Write(7)); PreparedPooled<int> alias = lease; _ = alias.Read(static view => view[0]);", "NAM1002")]
    [InlineData("using PreparedPooled<int> lease = pool.Rent(1, static writer => writer.Write(7)); NativeLeaseOperations.Access(lease, lease, (_, _) => pool.Dispose());", "NAM1005")]
    [InlineData("GC.SuppressFinalize(pool);", "NAM1001")]
    public async Task UnsafeOwnershipAndLifecycleOperationsRemainRejected(string body, string expected)
    {
        string source = $$"""
            using System;
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run()
                {
                    using NativePreparedPool<int> pool = new(new NativePoolPreparation(2, 2, 2), null);
                    {{body}}
                }
                private static void External(PreparedPooled<int> value) { }
            }
            """;
        AssertCompiles(source);
        Assert.Contains(expected, AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)), StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("lease.Access(_ => pool.Dispose());")]
    [InlineData("NativeLeaseOperations.Access(lease, lease, (_, _) => pool.Dispose());")]
    public async Task ExplicitOwnerCannotDisposeDuringPreparedBorrow(string operation)
    {
        string source = $$"""
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run()
                {
                    NativePreparedPool<int> pool = new(new NativePoolPreparation(2, 2, 2), null);
                    try
                    {
                        using PreparedPooled<int> lease = pool.Rent(1, static writer => writer.Write(7));
                        {{operation}}
                    }
                    finally { pool.Dispose(); }
                }
            }
            """;
        AssertCompiles(source);
        Assert.Contains("NAM1007", AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)), StringComparer.Ordinal);
    }

    [Fact]
    public async Task AwaitCannotCrossAnActivePreparedLease()
    {
        const string source = """
            using System.Threading.Tasks;
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static async Task Run()
                {
                    using NativePreparedPool<int> pool = new(new NativePoolPreparation(1, 1, 1), null);
                    PreparedPooled<int> value = pool.Rent(1, static writer => writer.Write(7));
                    await Task.Yield();
                    value.Dispose();
                }
            }
            """;
        AssertCompiles(source);
        Assert.Contains("NAM1011", AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)), StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedOwnerFieldNeedsAnEffectiveDisposePath(bool disposed)
    {
        string source = $$"""
            using System;
            using Supprocom.NativeAllocationManagement;
            public sealed class Sample : IDisposable
            {
                private readonly NativePreparedPool<int> _pool = new(new NativePoolPreparation(1, 1, 1), null);
                public void Dispose() { {{(disposed ? "_pool.Dispose();" : string.Empty)}} }
            }
            """;
        AssertCompiles(source);
        string[] ids = AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source));
        if (disposed) Assert.Empty(ids);
        else Assert.Contains("NAM1015", ids, StringComparer.Ordinal);
    }

    [Fact]
    public async Task SourceLookalikesDoNotAcquireRuntimeOwnershipAuthentication()
    {
        const string source = """
            using Supprocom.NativeAllocationManagement;
            namespace Supprocom.NativeAllocationManagement
            {
                public sealed class NativePreparedPool<T> { public PreparedPooled<T> Rent() => new(); }
                public sealed class PreparedPooled<T> { public static PreparedPooled<T> Move(ref PreparedPooled<T> source) => source; }
            }
            public static class Sample
            {
                public static void Run()
                {
                    NativePreparedPool<int> owner = new();
                    PreparedPooled<int> handle = owner.Rent();
                    _ = PreparedPooled<int>.Move(ref handle);
                }
            }
            """;
        AssertCompiles(source);
        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)));
    }

    [Fact]
    public async Task PreparedCompositeSourceAndScopedPublicationRetainLifetimeProof()
    {
        const string source = """
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run()
                {
                    using NativePreparedPool<int> pool = new(new NativePoolPreparation(2, 2, 2), null);
                    using NativeConcurrentArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
                    using PreparedPooled<int> first = pool.Rent(1, static writer => writer.Write(7));
                    using PreparedPooled<int> second = pool.Rent(1, static writer => writer.Write(11));
                    NativeLeaseOperations.Access(first, second, static (a, b) => a[0] = b[0]);
                    try
                    {
                        NativeLeaseOperations.InitializeScoped<int, int, int, int, int>(first, arena, 1, 1, 1, 1,
                            static (input, a, b, c, d) => { a.Fill(input[0]); b.Fill(11); c.Fill(13); d.Fill(17); },
                            out ConcurrentArenaLease<int> a, out ConcurrentArenaLease<int> b,
                            out ConcurrentArenaLease<int> c, out ConcurrentArenaLease<int> d);
                        NativeLeaseOperations.Access(a, b, c, d, static (_, _, _, _) => { });
                    }
                    finally { arena.RecycleScoped(); }
                }
            }
            """;
        AssertCompiles(source);
        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)));
    }

    private static void AssertCompiles(string source) =>
        Assert.DoesNotContain(AnalyzerContractTests.Compile(source), static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
}
