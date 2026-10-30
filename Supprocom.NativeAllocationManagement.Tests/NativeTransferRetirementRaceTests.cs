namespace Supprocom.NativeAllocationManagement.Tests;

// A transfer's borrows race its move. These tests hold a borrow at the point
// where it races (NativeTransferRacesForTest) and check that the storage is
// returned once. The hooks are process-wide, so they run alone.
[CollectionDefinition(Name, DisableParallelization = true)]
#pragma warning disable CA1515 // xUnit requires collection-definition classes to be public.
public sealed class TransferRetirementRaceGroup
#pragma warning restore CA1515
{
    public const string Name = "Transfer retirement races";
}

[Collection(TransferRetirementRaceGroup.Name)]
public sealed class NativeTransferRetirementRaceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // A borrow that passed its active check before a move began finds the
    // lone slot taken and is refused by the closed admission. The holder of
    // the lone slot leaves while the refused borrow's increment is counted, so
    // the refused borrow completes the retirement when it takes the increment
    // back.
    [Fact]
    public void ARefusedBorrowCompletesTheRetirementItsIncrementDelayed()
    {
        Assert.True(NativeTransferRacesForTest.Enabled);
        NativeTransfer<int>? source = NativeTransfer<int>.Create(8, 7);
        NativeTransfer<int> transfer = source.Value;
        using ManualResetEventSlim holderInside = new(false);
        using ManualResetEventSlim releaseHolder = new(false);
        using ManualResetEventSlim readerChecked = new(false);
        using ManualResetEventSlim releaseReader = new(false);
        Thread? reader = null;
        Exception? readerFailure = null;
        Exception? holderFailure = null;
        Thread holder = new(() => holderFailure = Capture(() => transfer.Access(_ =>
        {
            holderInside.Set();
            Assert.True(releaseHolder.Wait(Timeout));
        })));
        NativeTransferRacesForTest.AfterActiveCheck = () =>
        {
            if (ReferenceEquals(Thread.CurrentThread, reader))
            {
                readerChecked.Set();
                Assert.True(releaseReader.Wait(Timeout));
            }
        };
        NativeTransferRacesForTest.BeforeRefusedExit = () =>
        {
            if (ReferenceEquals(Thread.CurrentThread, reader))
            {
                // The holder leaves while this increment is counted: its exit
                // does not see the last borrow leave.
                releaseHolder.Set();
                Assert.True(holder.Join(Timeout));
            }
        };
        try
        {
            holder.Start();
            Assert.True(holderInside.Wait(Timeout));
            reader = new(() => readerFailure = Capture(() => transfer.Read(static view => view[0])));
            reader.Start();
            Assert.True(readerChecked.Wait(Timeout));

            Assert.Throws<InvalidOperationException>(() => NativeTransfer<int>.Move(ref source));
            Assert.Equal(NativeTransferLifecycle.Retiring, transfer.CaptureSnapshot().Lifecycle);

            releaseReader.Set();
            Assert.True(reader.Join(Timeout));
            Assert.False(holder.IsAlive);
            Assert.Null(holderFailure);
            Assert.IsType<InvalidOperationException>(readerFailure);
            NativeTransferStatistics statistics = transfer.CaptureSnapshot();
            Assert.Equal(NativeTransferLifecycle.Returned, statistics.Lifecycle);
            Assert.Equal(1, statistics.PayloadReturnCount);
            Assert.Equal(0, statistics.ActiveBorrowCount);
        }
        finally
        {
            NativeTransferRacesForTest.Reset();
            releaseHolder.Set();
            releaseReader.Set();
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "A worker's failure is asserted on the test thread.")]
    private static Exception? Capture(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
