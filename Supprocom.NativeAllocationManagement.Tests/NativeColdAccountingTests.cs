using System.Reflection;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeColdAccountingTests
{
    private static readonly int[] Input = [3, 5, 7, 11];

    [Theory]
    [InlineData(0)]
    [InlineData(4096)]
    public void DirectFillReadAndReturnDoNotClaimUnusedThreadAccounting(int length)
    {
        OnFreshThread(() =>
        {
            Assert.Null(ThreadAccounting());
            NativeMemoryBudget budget = new(length);
            Assert.True(budget.TryReserve<byte>(length, out NativeMemoryReservation<byte>? permission, out _));
            try
            {
                permission.Value.PrepareBacking();
                using NativeTransfer<byte> owner = NativeMemoryReservation<byte>.Activate(ref permission, static writer => writer.Fill(7));
                Assert.Equal(length * 7L, owner.Read(static view =>
                {
                    long sum = 0;
                    foreach (ref readonly byte value in view.AsSpan()) sum += value;
                    return sum;
                }));
                Assert.Null(ThreadAccounting());
            }
            // Activation consumes the nullable ref permission on success/failure.
#pragma warning disable CA1508
            finally { permission?.Dispose(); }
#pragma warning restore CA1508
            Assert.Null(ThreadAccounting());
            Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
            Assert.Equal(0, budget.CaptureAdmissionStatistics().OutstandingReservationCount);
        });
    }

    [Fact]
    public void FirstRealCopyClaimsAccountingAndPreservesCompleteCopyHistory()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            OnFreshThread(static () =>
            {
                Assert.Null(ThreadAccounting());
                NativeMemoryBudget budget = new(16);
                Assert.True(budget.TryReserve<int>(4, out NativeMemoryReservation<int>? permission, out _));
                try
                {
                    Assert.Null(ThreadAccounting());
                    using NativeTransfer<int> owner = NativeMemoryReservation<int>.Activate(ref permission,
                        static writer => writer.Write(Input.AsSpan()));
                    Assert.NotNull(ThreadAccounting());
                    Assert.Equal(16, NativeMemoryDiagnostics.Snapshot().CopiedBytes);
                    owner.Access(static view =>
                    {
                        Span<int> copy = stackalloc int[4];
                        view.CopyTo(copy);
                        Assert.True(copy.SequenceEqual(Input));
                    });
                    Assert.Equal(32, NativeMemoryDiagnostics.Snapshot().CopiedBytes);
                }
#pragma warning disable CA1508
                finally { permission?.Dispose(); }
#pragma warning restore CA1508
                Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
                Assert.Equal(32, NativeMemoryDiagnostics.Snapshot().CopiedBytes);
            });
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    private static object? ThreadAccounting() => typeof(NativeMemoryAccounting)
        .GetField("_threadHotMetrics", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);

    private static void OnFreshThread(Action action)
    {
        Exception? failure = null;
        Thread thread = new(() => failure = Record.Exception(action));
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(failure);
    }
}
