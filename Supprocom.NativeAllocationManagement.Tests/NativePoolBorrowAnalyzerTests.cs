using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativePoolBorrowAnalyzerTests
{
    [Fact]
    public async Task SourceVisiblePoolHelpersCanRentAndReturnOwnership()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> pool = new();
                    RunBatch(pool, 4);
                    Forward(pool);
                }

                private static void RunBatch<T>(NativePool<T> pool, int rounds)
                    where T : unmanaged
                {
                    for (int index = 0; index < rounds; index++)
                    {
                        NativeTransfer<T>? transfer = pool.RentTransferable(
                            8,
                            static writer => writer.Fill(default));
                        try
                        {
                            transfer.Access(static values => values[0] = default);
                        }
                        finally
                        {
                            transfer.Dispose();
                        }
                    }
                }

                private static void Forward(NativePool<int> pool)
                {
                    Nested(pool);

                    static void Nested(NativePool<int> nestedPool)
                    {
                        using Pooled<int> values = nestedPool.Rent(
                            8,
                            static writer => writer.Fill(default));
                        values.Access(static span => span[0] = 7);
                        _ = nestedPool.GetStatistics();
                    }
                }
            }
            """);

        Assert.True(
            AnalyzerContractTests.NativeDiagnostics(diagnostics).Length == 0,
            string.Join(Environment.NewLine, diagnostics));
    }

    [Fact]
    public async Task FieldPropertyBoxAndInterfaceRetentionAreRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                private static NativePool<int>? _field;
                private static NativePool<int>? Property { get; set; }

                public static void Run()
                {
                    using NativePool<int> first = new();
                    using NativePool<int> second = new();
                    using NativePool<int> third = new();
                    using NativePool<int> fourth = new();
                    StoreField(first);
                    StoreProperty(second);
                    Box(third);
                    Convert(fourth);
                }

                private static void StoreField(NativePool<int> pool) => _field = pool;
                private static void StoreProperty(NativePool<int> pool) => Property = pool;
                private static void Box(NativePool<int> pool) { object value = pool; }
                private static void Convert(NativePool<int> pool) { IDisposable value = pool; }
            }
            """);

        Assert.True(
            AnalyzerContractTests.NativeDiagnostics(diagnostics).Count(id => id == "NAM1001") >= 4,
            string.Join(Environment.NewLine, diagnostics));
    }

    [Fact]
    public async Task ClosureTaskIteratorAndUnknownForwardingAreRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> first = new();
                    using NativePool<int> second = new();
                    using NativePool<int> third = new();
                    using NativePool<int> fourth = new();
                    Capture(first);
                    _ = Suspend(second);
                    _ = Iterate(third);
                    Unknown(fourth);
                }

                private static void Capture(NativePool<int> pool)
                {
                    Action callback = () => _ = pool.GetStatistics();
                    _ = callback;
                }

                private static async Task Suspend(NativePool<int> pool)
                {
                    await Task.Yield();
                    _ = pool.GetStatistics();
                }

                private static IEnumerable<int> Iterate(NativePool<int> pool)
                {
                    _ = pool.GetStatistics();
                    yield return 1;
                }

                private static void Unknown(NativePool<int> pool) => GC.KeepAlive(pool);
            }
            """);

        Assert.True(
            AnalyzerContractTests.NativeDiagnostics(diagnostics).Count(id => id == "NAM1001") >= 4,
            string.Join(Environment.NewLine, diagnostics));
    }

    [Fact]
    public async Task ReturnReferenceForwardingAndLifecycleConsumptionAreRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> first = new();
                    using NativePool<int> second = new();
                    using NativePool<int> third = new();
                    NativePool<int> fourth = new();
                    _ = Return(first);
                    Forward(ref second);
                    Dispose(third);
                    Replace(out fourth);
                    fourth.Dispose();
                }

                private static NativePool<int> Return(NativePool<int> pool) => pool;
                private static void Forward(ref NativePool<int> pool) { }
                private static void Dispose(NativePool<int> pool) => pool.Dispose();
                private static void Replace(out NativePool<int> pool) => pool = new();
            }
            """);

        Assert.True(
            AnalyzerContractTests.NativeDiagnostics(diagnostics).Count(id => id == "NAM1001") >= 4,
            string.Join(Environment.NewLine, diagnostics));
    }
}
