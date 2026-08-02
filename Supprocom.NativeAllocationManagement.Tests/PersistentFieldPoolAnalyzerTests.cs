using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class PersistentFieldPoolAnalyzerTests
{
    [Fact]
    public async Task DisposableReadonlyFieldCanRentInBoundedWorkerLoops()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public sealed class Worker : IDisposable
            {
                private readonly NativePool<int> _pool = new(preAllocateElements: 64);

                public int Run(int rounds, bool stop, bool fail)
                {
                    for (int outer = 0; outer < rounds; outer++)
                    {
                        for (int inner = 0; inner < 2; inner++)
                        {
                            Pooled<int> lease = _pool.Rent(
                                16,
                                static writer => writer.Fill(default));
                            try
                            {
                                lease.Access(static values => values[0] = 11);
                                if (fail)
                                {
                                    throw new InvalidOperationException();
                                }

                                if (stop)
                                {
                                    return lease.Read(static values => values[0]);
                                }
                            }
                            finally
                            {
                                lease.Dispose();
                            }
                        }
                    }

                    return RunLocal();

                    int RunLocal()
                    {
                        using Pooled<int> lease = _pool.Rent(
                            4,
                            static writer => writer.Fill(default));
                        return lease.Read(static values => values[0]);
                    }
                }

                public NativeTransfer<int> CreateTransfer()
                {
                    NativeTransfer<int>? transfer = _pool.RentTransferable(
                        4,
                        static writer => writer.Fill(default));
                    try
                    {
                        return NativeTransfer<int>.Move(ref transfer);
                    }
                    finally
                    {
                        transfer?.Dispose();
                    }
                }

                public void Dispose() => _pool.Dispose();
            }
            """);

        Assert.True(
            AnalyzerContractTests.NativeDiagnostics(diagnostics).Length == 0,
            string.Join(Environment.NewLine, diagnostics));
    }

    [Fact]
    public async Task IncompleteFieldLeaseCleanupRemainsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public sealed class Worker : IDisposable
            {
                private readonly NativePool<int> _pool = new(preAllocateElements: 16);

                public void Run(bool cleanup)
                {
                    for (int index = 0; index < 2; index++)
                    {
                        Pooled<int> lease = _pool.Rent(
                            8,
                            static writer => writer.Fill(default));
                        if (cleanup)
                        {
                            lease.Dispose();
                        }
                    }
                }

                public void Dispose() => _pool.Dispose();
            }
            """);

        Assert.Contains("NAM1003", AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task FieldOwnerLifecycleChangesRemainVisibleAcrossLoops()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public sealed class Worker : IDisposable
            {
                private readonly NativePool<int> _pool = new(preAllocateElements: 16);

                public void Run()
                {
                    Pooled<int> lease = _pool.Rent(
                        8,
                        static writer => writer.Fill(default));
                    _pool.ReleaseLeasesToNativeMemory();
                    lease.Access(static values => values[0] = 1);
                    lease.Dispose();
                    _pool.Dispose();
                    for (int index = 0; index < 2; index++)
                    {
                        using Pooled<int> invalid = _pool.Rent(
                            1,
                            static writer => writer.Fill(default));
                    }
                }

                public void Dispose() => _pool.Dispose();
            }
            """);

        string[] ids = AnalyzerContractTests.NativeDiagnostics(diagnostics);
        Assert.Contains("NAM1007", ids);
        Assert.Contains("NAM1009", ids);
    }

    [Fact]
    public async Task NonReadonlyFieldDoesNotGainStableOwnerAuthority()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public sealed class Worker : IDisposable
            {
                private NativePool<int> _pool = new(preAllocateElements: 16);

                public void Run()
                {
                    for (int index = 0; index < 2; index++)
                    {
                        using Pooled<int> lease = _pool.Rent(
                            8,
                            static writer => writer.Fill(default));
                    }
                }

                public void Dispose() => _pool.Dispose();
            }
            """);

        Assert.Contains("NAM1009", AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task FieldLeasesCannotEscapeByReturnStorageCaptureOrAwait()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using System;
            using System.Threading.Tasks;
            using Supprocom.NativeAllocationManagement;

            public sealed class Worker : IDisposable
            {
                private readonly NativePool<int> _pool = new();

                public Pooled<int> ReturnLease()
                {
                    return _pool.Rent(4, static writer => writer.Fill(default));
                }

                public void StoreLease()
                {
                    Pooled<int> lease = _pool.Rent(
                        4,
                        static writer => writer.Fill(default));
                    LeaseHolder holder = new() { Lease = lease };
                    _ = holder;
                    lease.Dispose();
                }

                public void CaptureLease()
                {
                    Pooled<int> lease = _pool.Rent(
                        4,
                        static writer => writer.Fill(default));
                    Action callback = () => lease.Dispose();
                    _ = callback;
                }

                public async Task CrossAwait()
                {
                    Pooled<int> lease = _pool.Rent(
                        4,
                        static writer => writer.Fill(default));
                    await Task.Yield();
                    lease.Dispose();
                }

                public void Dispose() => _pool.Dispose();

                private ref struct LeaseHolder
                {
                    public Pooled<int> Lease;
                }
            }
            """);

        string[] ids = AnalyzerContractTests.NativeDiagnostics(diagnostics);
        Assert.Contains("NAM1013", ids);
        Assert.Contains("NAM1011", ids);
    }
}
