using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Supprocom.NativeAllocationManagement.Analyzers;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class AnalyzerContractTests
{
    [Fact]
    public async Task ScopedPoolAndLeaseHaveNoOwnershipDiagnostics()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> pool = new();
                    using Pooled<int> values = pool.Rent(4);
                    values.Access(static span => Fill(span));
                }

                private static void Fill(scoped Span<int> span)
                {
                    span[0] = 1;
                }
            }
            """);

        Assert.Empty(NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task ScopedPoolAndLeaseStatementFormsHaveNoOwnershipDiagnostics()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using (NativePool<int> pool = new())
                    {
                        using (Pooled<int> values = pool.Rent(1))
                        {
                            values[0] = 1;
                        }
                    }
                }
            }
            """);

        Assert.Empty(NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task ReturnedHandleUseIsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    NativePool<int> pool = new();
                    Pooled<int> oldValues = pool.Rent(1);
                    pool.ReturnToNativeMemory();
                    _ = oldValues.Length;
                    pool.LeaseFromMemory();
                    Pooled<int> currentValues = pool.Rent(1);
                    _ = currentValues.Length;
                    pool.Dispose();
                }
            }
            """);

        Assert.Contains("NAM1004", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task DiagnosticsCarryStructuredSourceAndProvenanceFacts()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    NativePool<int> pool = new();
                    Pooled<int> stale = pool.Rent(1);
                    pool.ReturnToNativeMemory();
                    _ = stale.Length;
                }
            }
            """);

        Diagnostic diagnostic = diagnostics.First(item => item.Id == "NAM1004");
        Assert.Equal("NAM1004", diagnostic.Properties["NAM.DiagnosticId"]);
        Assert.Contains("stale", diagnostic.Properties["NAM.Provenance"]!);
        Assert.Equal(diagnostic.Properties["NAM.Provenance"], diagnostic.Properties["NAM.ProvenancePath"]);
        Assert.NotEmpty(diagnostic.Properties["NAM.Operation"]!);
        Assert.Equal("NAM1004", diagnostic.Properties["NAM.OperationId"]);
        Assert.Contains(":", diagnostic.Properties["NAM.Source"]!);
        Assert.NotEmpty(diagnostic.Properties["NAM.SourceFile"]!);
        Assert.NotEmpty(diagnostic.Properties["NAM.SourceLine"]!);
        Assert.NotEmpty(diagnostic.Properties["NAM.SourceColumn"]!);
    }

    [Fact]
    public async Task AliasUnknownCallAndLifetimeEscapeAreRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    NativePool<int> pool = new();
                    Pooled<int> values = pool.Rent(1);
                    Pooled<int> alias = values;
                    Consume(values);
                }

                private static void Consume(Pooled<int> values)
                {
                    _ = values.Length;
                }
            }
            """);

        string[] ids = NativeDiagnostics(diagnostics);
        Assert.Contains("NAM1002", ids);
        Assert.Contains("NAM1016", ids);
        Assert.Contains("NAM1003", ids);
    }

    [Fact]
    public async Task RegionRequiresUsingAndRejectsNestedRegions()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    NativeRegion unscoped = new();
                    Local<int> first = unscoped.Allocate<int>(1);
                    using NativeRegion outer = new();
                    using NativeRegion inner = new();
                    Local<int> second = outer.Allocate<int>(1);
                }
            }
            """);

        string[] ids = NativeDiagnostics(diagnostics);
        Assert.Contains("NAM1006", ids);
        Assert.Contains("NAM1010", ids);
    }

    [Fact]
    public async Task BorrowedCallbackCannotReturnItsPool()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    NativePool<int> pool = new();
                    using Pooled<int> values = pool.Rent(1);
                    values.Access(span => pool.ReturnToNativeMemory());
                }
            }
            """);

        Assert.Contains("NAM1007", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task ScopedPoolCannotUseExplicitLifecycleOrLeaseAfterReturn()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> pool = new();
                    pool.ReturnToGarbageCollector();
                    pool.LeaseFromMemory();
                }
            }
            """);

        string[] ids = NativeDiagnostics(diagnostics);
        Assert.Contains("NAM1005", ids);
        Assert.Equal(2, ids.Count(id => id == "NAM1005"));
    }

    [Fact]
    public async Task DeterministicFieldRequiresAnExplicitDisposePath()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public sealed class Sample
            {
                private readonly NativePool<int> _pool = new(returnOnDispose: NativeReturn.ToNativeMemory);
            }
            """);

        Assert.Contains("NAM1015", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task DeterministicFieldWithAnEffectiveDisposePathIsAccepted()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;
            using System;

            public sealed class Sample : IDisposable
            {
                private readonly NativePool<int> _pool = new(returnOnDispose: NativeReturn.ToNativeMemory);

                public void Dispose()
                {
                    _pool.Dispose();
                }
            }
            """);

        Assert.Empty(NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task AwaitCannotCrossAnActiveLease()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System.Threading.Tasks;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static async Task Run()
                {
                    NativePool<int> pool = new();
                    Pooled<int> values = pool.Rent(1);
                    await Task.Yield();
                    _ = values.Length;
                    pool.Dispose();
                }
            }
            """);

        Assert.Contains("NAM1011", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task BranchesMayReturnTheSameGenerationExactlyOnce()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(bool condition)
                {
                    NativePool<int> pool = new();
                    if (condition)
                    {
                        pool.ReturnToNativeMemory();
                    }
                    else
                    {
                        pool.ReturnToNativeMemory();
                    }

                    pool.LeaseFromMemory();
                    Pooled<int> values = pool.Rent(1);
                    values.Dispose();
                    pool.Dispose();

                }
            }
            """);

        Assert.DoesNotContain("NAM1009", NativeDiagnostics(diagnostics));
        Assert.DoesNotContain("NAM1003", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task LoopStateUsesTheLeastPermissivePostLoopFact()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(bool condition)
                {
                    NativePool<int> pool = new();
                    while (condition)
                    {
                        pool.ReturnToNativeMemory();
                    }

                    _ = pool.Rent(1);
                }
            }
            """);

        Assert.Contains("NAM1009", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task FinallyDisposesAHandleOnEveryExceptionalPath()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    NativePool<int> pool = new(returnOnDispose: NativeReturn.ToNativeMemory);
                    Pooled<int> values = pool.Rent(1);
                    try
                    {
                        values[0] = 1;
                    }
                    finally
                    {
                        values.Dispose();
                    }

                    pool.Dispose();
                }
            }
            """);

        Assert.Empty(NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task TupleAndLocalFunctionEscapesAreRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    NativePool<int> pool = new();
                    Pooled<int> values = pool.Rent(1);
                    var tuple = (values, 1);

                    void ReadLater()
                    {
                        _ = values.Length;
                    }

                    ReadLater();
                    values.Dispose();
                    pool.Dispose();
                }
            }
            """);

        string[] ids = NativeDiagnostics(diagnostics);
        Assert.Contains("NAM1013", ids);
    }

    [Fact]
    public async Task RegionLocalsCannotBeUsedOutsideTheirRegion()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    Local<int> value;
                    using (NativeRegion region = new())
                    {
                        value = region.Allocate<int>(1);
                    }

                    _ = value.Length;
                }
            }
            """);

        Assert.Contains("NAM1012", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task SequentialRegionStatementsAreAccepted()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using (NativeRegion first = new())
                    {
                        Local<int> value = first.Allocate<int>(1);
                        value[0] = 1;
                    }

                    using (NativeRegion second = new())
                    {
                        Local<int> value = second.Allocate<int>(1);
                        value[0] = 2;
                    }
                }
            }
            """);

        Assert.DoesNotContain("NAM1010", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task SourceVisibleLifecycleHelpersCarryOwnerEffects()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    NativePool<int> pool = new();
                    ReturnPool(pool);
                    pool.LeaseFromMemory();
                    Pooled<int> values = pool.Rent(1);
                    values.Dispose();
                    pool.Dispose();

                }

                private static void ReturnPool(NativePool<int> pool)
                {
                    pool.ReturnToNativeMemory();
                }
            }
            """);

        Assert.DoesNotContain("NAM1001", NativeDiagnostics(diagnostics));
        Assert.DoesNotContain("NAM1009", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task EarlyReturnCannotAbandonAnActiveNativeValue()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(bool condition)
                {
                    NativePool<int> pool = new(returnOnDispose: NativeReturn.ToNativeMemory);
                    Pooled<int> values = pool.Rent(1);
                    if (condition)
                    {
                        return;
                    }

                    values.Dispose();
                    pool.Dispose();
                }
            }
            """);

        Assert.Contains("NAM1003", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task FinallyCleanupMakesAnEarlyReturnValid()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(bool condition)
                {
                    NativePool<int> pool = new(returnOnDispose: NativeReturn.ToNativeMemory);
                    Pooled<int> values = pool.Rent(1);
                    try
                    {
                        if (condition)
                        {
                            return;
                        }

                        values[0] = 1;
                    }
                    finally
                    {
                        values.Dispose();
                        pool.Dispose();
                    }
                }
            }
            """);

        Assert.Empty(NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task YieldCannotCrossAnActiveLease()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System.Collections.Generic;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static IEnumerable<int> Run()
                {
                    NativePool<int> pool = new();
                    Pooled<int> values = pool.Rent(1);
                    yield return 1;
                    values.Dispose();
                    pool.Dispose();
                }
            }
            """);

        Assert.Contains("NAM1011", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task EveryPostReturnHandleOperationIsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    NativePool<int> pool = new();
                    Pooled<int> stale = pool.Rent(2);
                    pool.ReturnToNativeMemory();
                    _ = stale.Length;
                    _ = stale.Capacity;
                    _ = stale[0];
                    stale[0] = 1;
                    stale.Clear();
                    stale.CopyFrom(new int[2]);
                    stale.CopyTo(new int[2]);
                    stale.Access(static _ => { });
                    _ = stale.Read(static span => span[0]);
                    stale.Dispose();
                    pool.Dispose();
                }
            }
            """);

        Assert.True(
            NativeDiagnostics(diagnostics).Count(id => id == "NAM1004") >= 10,
            string.Join(", ", NativeDiagnostics(diagnostics)));
    }

    [Fact]
    public async Task ExpressionAndExceptionalExitsRemainChecked()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static Pooled<int> ReturnHandle(NativePool<int> pool) => pool.Rent(1);

                public static void ReturnInsideTry(bool condition)
                {
                    NativePool<int> pool = new(returnOnDispose: NativeReturn.ToNativeMemory);
                    Pooled<int> value = pool.Rent(1);
                    try
                    {
                        if (condition)
                        {
                            return;
                        }

                        throw new InvalidOperationException();
                    }
                    catch (InvalidOperationException)
                    {
                        return;
                    }
                    finally
                    {
                        if (condition)
                        {
                            value.Dispose();
                            pool.Dispose();
                        }
                    }
                }

                public static void ThrowWithoutCleanup()
                {
                    NativePool<int> pool = new(returnOnDispose: NativeReturn.ToNativeMemory);
                    Pooled<int> value = pool.Rent(1);
                    throw new InvalidOperationException();
                }
            }
            """);

        string[] ids = NativeDiagnostics(diagnostics);
        Assert.Contains("NAM1013", ids);
        Assert.Contains("NAM1003", ids);
    }

    [Fact]
    public async Task OneBranchOnlyOwnerReturnRemainsAmbiguous()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(bool condition)
                {
                    NativePool<int> pool = new(returnOnDispose: NativeReturn.ToNativeMemory);
                    if (condition)
                    {
                        pool.ReturnToNativeMemory();
                    }

                    pool.LeaseFromMemory();
                }
            }
            """);

        Assert.Contains("NAM1009", NativeDiagnostics(diagnostics));
        Assert.Contains("NAM1003", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task OneBranchOnlyLeaseDisposalRemainsAmbiguous()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(bool condition)
                {
                    NativePool<int> pool = new();
                    Pooled<int> values = pool.Rent(1);
                    if (condition)
                    {
                        values.Dispose();
                    }

                    pool.Dispose();
                }
            }
            """);

        Assert.Contains("NAM1003", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task ZeroOneManyLoopIterationsRemainConservative()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(int attempts)
                {
                    NativePool<int> pool = new();
                    for (int index = 0; index < attempts; index++)
                    {
                        if (index == 0)
                        {
                            continue;
                        }

                        pool.ReturnToNativeMemory();
                        if (index == 2)
                        {
                            break;
                        }
                    }

                    pool.LeaseFromMemory();
                }
            }
            """);

        Assert.Contains("NAM1009", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task BalancedReusablePoolGenerationLoopAcceptsZeroOneAndManyIterations()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(int iterations)
                {
                    NativePool<int> pool = new();
                    for (int index = 0; index < iterations; index++)
                    {
                        pool.ReturnToNativeMemory();
                        pool.LeaseFromMemory();
                        Pooled<int> values = pool.Rent(1);
                        values.Dispose();
                    }

                    pool.Dispose();
                }
            }
            """);

        Assert.Empty(NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task BalancedReusablePoolLoopAcceptsBothReturnPoliciesOnEveryCycle()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(int iterations, bool native)
                {
                    NativePool<int> pool = new();
                    for (int index = 0; index < iterations; index++)
                    {
                        if (native)
                        {
                            pool.ReturnToNativeMemory();
                        }
                        else
                        {
                            pool.ReturnToGarbageCollector();
                        }

                        pool.LeaseFromMemory();
                        Pooled<int> values = pool.Rent(1);
                        values.Dispose();
                    }

                    pool.Dispose();
                }
            }
            """);

        Assert.Empty(NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task BalancedReusablePoolRetryGotoAcceptsEveryCompletedCycle()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(int iterations)
                {
                    NativePool<int> pool = new();
                    int completed = 0;
                Retry:
                    if (completed == iterations)
                    {
                        goto Done;
                    }

                    completed++;
                    pool.ReturnToNativeMemory();
                    pool.LeaseFromMemory();
                    goto Retry;

                Done:
                    pool.Dispose();
                }
            }
            """);

        Assert.Empty(NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task HandleRetainedFromAnEarlierGenerationRemainsStaleAfterBalancedCycles()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(int iterations)
                {
                    NativePool<int> pool = new();
                    Pooled<int> stale = pool.Rent(1);
                    for (int index = 0; index < iterations; index++)
                    {
                        pool.ReturnToNativeMemory();
                        pool.LeaseFromMemory();
                        _ = stale.Length;
                    }

                    pool.Dispose();
                }
            }
            """);

        Assert.Contains("NAM1004", NativeDiagnostics(diagnostics));
        Assert.DoesNotContain("NAM1009", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task NestedDefaultGcRootAbandonedReportsLifetimeEscape()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    NativePool<int> pool = new();
                    {
                        Pooled<int> value = pool.Rent(1);
                    }

                    pool.Dispose();
                }
            }
            """);

        Assert.Contains("NAM1003", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task NestedRootFollowedOnlyByOwnerDisposeReportsTheRoot()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    NativePool<int> pool = new();
                    {
                        Pooled<int> value = pool.Rent(1);
                    }

                    pool.Dispose();
                }
            }
            """);

        Diagnostic[] lifetimeDiagnostics = diagnostics.Where(diagnostic => diagnostic.Id == "NAM1003").ToArray();
        Assert.Single(lifetimeDiagnostics);
        Assert.Contains("value", lifetimeDiagnostics[0].Properties["NAM.Provenance"]!);
    }

    [Fact]
    public async Task NestedRootExplicitlyDisposedIsAccepted()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    NativePool<int> pool = new();
                    {
                        Pooled<int> value = pool.Rent(1);
                        value.Dispose();
                    }

                    pool.Dispose();
                }
            }
            """);

        Assert.Empty(NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task NestedRootEndedByWholeGenerationReturnIsAccepted()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    NativePool<int> pool = new();
                    {
                        Pooled<int> value = pool.Rent(1);
                        pool.ReturnToNativeMemory();
                    }

                    pool.LeaseFromMemory();
                    pool.Dispose();
                }
            }
            """);

        Assert.Empty(NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task LoopLocalDisposedRootIsAcceptedForZeroOneAndManyIterations()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(int iterations)
                {
                    NativePool<int> pool = new();
                    for (int index = 0; index < iterations; index++)
                    {
                        Pooled<int> value = pool.Rent(1);
                        value.Dispose();
                    }

                    pool.Dispose();
                }
            }
            """);

        Assert.Empty(NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task ActiveRootsLeavingSwitchBreakAndContinueEdgesAreRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(int value, bool continueLoop)
                {
                    NativePool<int> pool = new();
                    while (continueLoop)
                    {
                        switch (value)
                        {
                            case 1:
                                Pooled<int> switched = pool.Rent(1);
                                break;
                            default:
                                Pooled<int> continued = pool.Rent(1);
                                continue;
                        }

                        break;
                    }

                    pool.Dispose();
                }
            }
            """);

        Assert.True(NativeDiagnostics(diagnostics).Count(id => id == "NAM1003") >= 2);
    }

    [Fact]
    public async Task ActiveRootsLeavingGotoFallthroughReturnAndExceptionEdgesAreRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void GotoPath()
                {
                    NativePool<int> pool = new();
                    {
                        Pooled<int> value = pool.Rent(1);
                        goto Done;
                    }

                Done:
                    pool.Dispose();
                }

                public static int ReturnPath()
                {
                    NativePool<int> pool = new();
                    {
                        Pooled<int> value = pool.Rent(1);
                        return 42;
                    }
                }

                public static void ExceptionPath()
                {
                    NativePool<int> pool = new();
                    try
                    {
                        Pooled<int> value = pool.Rent(1);
                        throw new InvalidOperationException();
                    }
                    catch (InvalidOperationException)
                    {
                    }

                    pool.Dispose();
                }
            }
            """);

        Assert.True(NativeDiagnostics(diagnostics).Count(id => id == "NAM1003") >= 3);
    }

    [Fact]
    public async Task SwitchWithoutDefaultAndExceptionalExitRemainConservative()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(int value)
                {
                    NativePool<int> pool = new();
                    switch (value)
                    {
                        case 1:
                            pool.ReturnToNativeMemory();
                            break;
                    }

                    try
                    {
                        pool.ReturnToGarbageCollector();
                    }
                    catch (InvalidOperationException)
                    {
                    }

                    pool.LeaseFromMemory();
                }
            }
            """);

        Assert.Contains("NAM1009", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task GotoAndRetryPathsRemainConservative()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(bool condition, bool shouldReturn)
                {
                    NativePool<int> pool = new();
                Retry:
                    if (condition)
                    {
                        condition = false;
                        goto Retry;
                    }

                    if (shouldReturn)
                    {
                        pool.ReturnToNativeMemory();
                    }

                    pool.LeaseFromMemory();
                }
            }
            """);

        Assert.Contains("NAM1009", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task GotoCanSkipCleanupAndMustNotSuppressExitDiagnostics()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(bool condition)
                {
                    NativePool<int> pool = new(returnOnDispose: NativeReturn.ToNativeMemory);
                    Pooled<int> value = pool.Rent(1);
                    if (condition)
                    {
                        goto Done;
                    }

                    value.Dispose();
                    pool.Dispose();
                Done:
                    _ = value.Length;
                }
            }
            """);

        Assert.Contains("NAM1003", NativeDiagnostics(diagnostics));
        Assert.Contains("NAM1004", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task ValueReturnsReportActiveOwnershipAtEveryExit()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static int Run(bool condition)
                {
                    NativePool<int> pool = new(returnOnDispose: NativeReturn.ToNativeMemory);
                    Pooled<int> values = pool.Rent(1);
                    if (condition)
                    {
                        return 42;
                    }

                    return 7;
                }
            }
            """);

        Assert.Contains("NAM1003", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task UnrelatedUsingBodiesDoNotCreateAutomaticCleanupScopes()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public sealed class DisposableThing : IDisposable
            {
                public void Dispose() { }
            }

            public static class Sample
            {
                public static void Run()
                {
                    using (new DisposableThing())
                    {
                        NativeRegion region = new();
                        NativePool<int> pool = new(returnOnDispose: NativeReturn.ToNativeMemory);
                        Pooled<int> values = pool.Rent(1);
                    }
                }
            }
            """);

        string[] ids = NativeDiagnostics(diagnostics);
        Assert.Contains("NAM1006", ids);
        Assert.Contains("NAM1003", ids);
        Assert.DoesNotContain("NAM1005", ids);
    }

    [Fact]
    public async Task ConditionalAndDeferredLifecycleHelpersRemainUnknown()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(bool condition)
                {
                    NativePool<int> pool = new();
                    ConditionalReturn(pool, condition);
                    Action deferred = () => pool.ReturnToNativeMemory();
                    deferred();
                    pool.LeaseFromMemory();
                }

                private static void ConditionalReturn(NativePool<int> pool, bool condition)
                {
                    if (condition)
                    {
                        pool.ReturnToNativeMemory();
                    }
                }
            }
            """);

        Assert.Contains("NAM1009", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task MultipleTransitionAndTryHelpersRemainUnknown()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    NativePool<int> pool = new();
                    Multiple(pool);
                    TryReturn(pool);
                    pool.LeaseFromMemory();
                }

                private static void Multiple(NativePool<int> pool)
                {
                    pool.ReturnToNativeMemory();
                    pool.ReturnToGarbageCollector();
                }

                private static void TryReturn(NativePool<int> pool)
                {
                    try
                    {
                        pool.ReturnToNativeMemory();
                    }
                    finally
                    {
                    }
                }
            }
            """);

        Assert.Contains("NAM1009", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task FieldProofRejectsTextOnlyConditionalAndDeferredDisposal()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public sealed class Sample : IDisposable
            {
                private readonly NativePool<int> _pool = new(returnOnDispose: NativeReturn.ToNativeMemory);

                public void Dispose()
                {
                    Console.WriteLine(nameof(_pool));
                    if (DateTime.UtcNow.Ticks == 0)
                    {
                        _pool.Dispose();
                    }

                    Action deferred = () => _pool.Dispose();
                }
            }
            """);

        Assert.Contains("NAM1015", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task FieldProofAcceptsDelegatedAndExplicitInterfaceDisposal()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public sealed class Delegated : IDisposable
            {
                private readonly NativePool<int> _pool = new(returnOnDispose: NativeReturn.ToNativeMemory);
                public void Dispose() => Release();
                private void Release() => _pool.Dispose();
            }

            public sealed class Explicit : IDisposable
            {
                private readonly NativePool<int> _pool = new(returnOnDispose: NativeReturn.ToNativeMemory);
                void IDisposable.Dispose() => _pool.Dispose();
            }
            """);

        Assert.Empty(NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task HelperWithUnconditionalTryFinallyLifecycleIsAccepted()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    NativePool<int> pool = new();
                    ReturnPool(pool);
                    pool.LeaseFromMemory();
                    Pooled<int> value = pool.Rent(1);
                    value.Dispose();
                    pool.Dispose();
                }

                private static void ReturnPool(NativePool<int> pool)
                {
                    try
                    {
                        pool.ReturnToNativeMemory();
                    }
                    finally
                    {
                    }
                }
            }
            """);

        Assert.DoesNotContain("NAM1009", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task NestedAndPostTransitionEarlyReturnHelpersAreAccepted()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    NativePool<int> pool = new();
                    ReturnOuter(pool);
                    pool.LeaseFromMemory();
                    Pooled<int> values = pool.Rent(1);
                    values.Dispose();
                    pool.Dispose();

                    NativePool<int> second = new();
                    ReturnThenMaybeExit(second, condition: true);
                    second.LeaseFromMemory();
                    Pooled<int> secondValues = second.Rent(1);
                    secondValues.Dispose();
                    second.Dispose();
                }

                private static void ReturnOuter(NativePool<int> pool)
                    => ReturnInner(pool);

                private static void ReturnInner(NativePool<int> pool)
                    => pool.ReturnToNativeMemory();

                private static void ReturnThenMaybeExit(NativePool<int> pool, bool condition)
                {
                    pool.ReturnToNativeMemory();
                    if (condition)
                    {
                        return;
                    }
                }
            }
            """);

        Assert.DoesNotContain("NAM1009", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task HelperWithAnEarlyReturnOrNormalCatchIsUnknown()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(bool condition)
                {
                    NativePool<int> pool = new();
                    ReturnMaybe(pool, condition);
                    ReturnWithCatch(pool);
                    pool.LeaseFromMemory();
                }

                private static void ReturnMaybe(NativePool<int> pool, bool condition)
                {
                    if (condition)
                    {
                        return;
                    }

                    pool.ReturnToNativeMemory();
                }

                private static void ReturnWithCatch(NativePool<int> pool)
                {
                    try
                    {
                        pool.ReturnToNativeMemory();
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            }
            """);

        Assert.Contains("NAM1009", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task FieldProofFollowsTheNormalBaseAndDerivedDisposeChain()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public class Base : IDisposable
            {
                protected readonly NativePool<int> BasePool = new(returnOnDispose: NativeReturn.ToNativeMemory);

                public void Dispose() => Dispose(disposing: true);

                protected virtual void Dispose(bool disposing)
                {
                    if (disposing)
                    {
                        BasePool.Dispose();
                    }
                }
            }

            public sealed class Derived : Base
            {
                private readonly NativePool<int> _pool = new(returnOnDispose: NativeReturn.ToNativeMemory);

                protected override void Dispose(bool disposing)
                {
                    base.Dispose(disposing);
                    if (disposing)
                    {
                        _pool.Dispose();
                    }
                }
            }
            """);

        Assert.Empty(NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task FieldProofDoesNotAcceptAnUncalledDisposeBooleanOverload()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public sealed class Sample : IDisposable
            {
                private readonly NativePool<int> _pool = new(returnOnDispose: NativeReturn.ToNativeMemory);

                public void Dispose()
                {
                }

                private void Dispose(bool disposing)
                {
                    if (disposing)
                    {
                        _pool.Dispose();
                    }
                }
            }
            """);

        Assert.Contains("NAM1015", NativeDiagnostics(diagnostics));
    }

    internal static string[] NativeDiagnostics(ImmutableArray<Diagnostic> diagnostics)
    {
        return diagnostics
            .Where(diagnostic => diagnostic.Id.StartsWith("NAM", StringComparison.Ordinal))
            .Select(diagnostic => diagnostic.Id)
            .ToArray();
    }

    internal static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
    {
        CSharpParseOptions parseOptions = new(LanguageVersion.Preview);
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, parseOptions);
        List<MetadataReference> references =
        [
            ..GetTrustedPlatformReferences(),
            MetadataReference.CreateFromFile(typeof(NativePool<int>).Assembly.Location)
        ];

        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName: "NativeAllocationAnalyzerContract",
            syntaxTrees: [tree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        CompilationWithAnalyzers analyzed = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new NativeAllocationAnalyzer()));
        return await analyzed.GetAnalyzerDiagnosticsAsync();
    }

    private static IEnumerable<MetadataReference> GetTrustedPlatformReferences()
    {
        string trustedAssemblies =
            AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string
            ?? throw new InvalidOperationException("The test host did not expose trusted platform assemblies.");

        return trustedAssemblies
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
    }
}
