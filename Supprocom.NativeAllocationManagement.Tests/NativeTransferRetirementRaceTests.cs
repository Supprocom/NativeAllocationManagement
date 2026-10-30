namespace Supprocom.NativeAllocationManagement.Tests;

// A transfer's borrows race its move and disposal. These tests hold a borrow,
// a move or a disposal at the point where each race happens
// (NativeTransferRacesForTest) and check that the storage is returned once,
// and that the transfer stays usable. The hooks are process-wide, so they run
// alone.
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

    // A borrow admitted just before a move began finds the transfer retiring.
    // It leaves after the borrow the move waited for, so it completes the
    // retirement.
    [Fact]
    public void AnAdmittedBorrowThatFindsTheTransferRetiringCompletesTheRetirement()
    {
        Assert.True(NativeTransferRacesForTest.Enabled);
        NativeTransfer<int>? source = NativeTransfer<int>.Create(8, 7);
        NativeTransfer<int> transfer = source.Value;
        using ManualResetEventSlim holderInside = new(false);
        using ManualResetEventSlim releaseHolder = new(false);
        using ManualResetEventSlim readerAdmitted = new(false);
        using ManualResetEventSlim releaseReader = new(false);
        Thread? reader = null;
        Exception? readerFailure = null;
        NativeTransferRacesForTest.AfterAdmission = () =>
        {
            if (ReferenceEquals(Thread.CurrentThread, reader))
            {
                readerAdmitted.Set();
                Assert.True(releaseReader.Wait(Timeout));
            }
        };
        Exception? holderFailure = null;
        Thread holder = new(() => holderFailure = Capture(() => transfer.Access(_ =>
        {
            holderInside.Set();
            Assert.True(releaseHolder.Wait(Timeout));
        })));
        try
        {
            holder.Start();
            Assert.True(holderInside.Wait(Timeout));
            reader = new(() => readerFailure = Capture(() => transfer.Read(static view => view[0])));
            reader.Start();
            Assert.True(readerAdmitted.Wait(Timeout));

            // Two borrows are counted, so the move retires the transfer.
            Assert.Throws<InvalidOperationException>(() => NativeTransfer<int>.Move(ref source));
            releaseHolder.Set();
            Assert.True(holder.Join(Timeout));
            Assert.Null(holderFailure);
            Assert.Equal(NativeTransferLifecycle.Retiring, transfer.CaptureSnapshot().Lifecycle);

            releaseReader.Set();
            Assert.True(reader.Join(Timeout));
            Assert.IsType<InvalidOperationException>(readerFailure);
            AssertReturned(transfer);
        }
        finally
        {
            NativeTransferRacesForTest.Reset();
            releaseHolder.Set();
            releaseReader.Set();
        }
    }

    // A borrow that passed its active check before a move began is refused
    // by the closed admission. The last borrow left while the refused one's
    // increment was still counted, so the refused borrow completes the
    // retirement when it takes its increment back.
    [Fact]
    public void ARefusedBorrowCompletesTheRetirementItsIncrementDelayed()
    {
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
            AssertReturned(transfer);
        }
        finally
        {
            NativeTransferRacesForTest.Reset();
            releaseHolder.Set();
            releaseReader.Set();
        }
    }

    // A borrow refused while a move publishes the new version still holds its
    // increment. The publication keeps it, so taking it back leaves the count
    // at zero instead of wrapping it, and the moved transfer stays usable.
    [Fact]
    public void AMoveKeepsTheIncrementOfABorrowItRefused()
    {
        NativeTransfer<int>? source = NativeTransfer<int>.Create(8, 7);
        NativeTransfer<int> alias = source.Value;
        using ManualResetEventSlim readerChecked = new(false);
        using ManualResetEventSlim releaseReader = new(false);
        using ManualResetEventSlim readerRefused = new(false);
        using ManualResetEventSlim releaseRefused = new(false);
        Thread? reader = null;
        Exception? readerFailure = null;
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
                readerRefused.Set();
                Assert.True(releaseRefused.Wait(Timeout));
            }
        };
        NativeTransferRacesForTest.BeforeMovePublication = () =>
        {
            releaseReader.Set();
            Assert.True(readerRefused.Wait(Timeout));
        };
        try
        {
            reader = new(() => readerFailure = Capture(() => alias.Read(static view => view[0])));
            reader.Start();
            Assert.True(readerChecked.Wait(Timeout));
            NativeTransfer<int> moved = NativeTransfer<int>.Move(ref source);
            releaseRefused.Set();
            Assert.True(reader.Join(Timeout));
            Assert.IsType<InvalidOperationException>(readerFailure);

            Assert.Equal(0, moved.CaptureSnapshot().ActiveBorrowCount);
            Assert.Equal(7, moved.Read(static view => view[0]));
            moved.Dispose();
            AssertReturned(moved);
        }
        finally
        {
            NativeTransferRacesForTest.Reset();
            releaseReader.Set();
            releaseRefused.Set();
        }
    }

    // The same when a disposed builder control is reused for a new transfer
    // while a refused borrow of its earlier lifetime holds its increment.
    [Fact]
    public void AReusedControlKeepsTheIncrementOfABorrowItsEarlierLifetimeRefused()
    {
        NativeTransfer<int> first;
        using (NativeBuilder<int> builder = new(4))
        {
            builder.Append(7);
            first = builder.Complete();
        }

        using ManualResetEventSlim readerChecked = new(false);
        using ManualResetEventSlim releaseReader = new(false);
        using ManualResetEventSlim readerRefused = new(false);
        using ManualResetEventSlim releaseRefused = new(false);
        Thread? reader = null;
        Exception? readerFailure = null;
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
                readerRefused.Set();
                Assert.True(releaseRefused.Wait(Timeout));
            }
        };
        try
        {
            reader = new(() => readerFailure = Capture(() => first.Read(static view => view[0])));
            reader.Start();
            Assert.True(readerChecked.Wait(Timeout));
            first.Dispose();
            releaseReader.Set();
            Assert.True(readerRefused.Wait(Timeout));

            // This thread parked the disposed control; the next builder reuses it.
            NativeTransfer<int> second;
            using (NativeBuilder<int> builder = new(4))
            {
                builder.Append(9);
                second = builder.Complete();
            }

            Assert.Same(first.ControlForTest, second.ControlForTest);
            releaseRefused.Set();
            Assert.True(reader.Join(Timeout));
            Assert.NotNull(readerFailure);

            Assert.Equal(0, second.CaptureSnapshot().ActiveBorrowCount);
            Assert.Equal(9, second.Read(static view => view[0]));
            second.Dispose();
        }
        finally
        {
            NativeTransferRacesForTest.Reset();
            releaseReader.Set();
            releaseRefused.Set();
        }
    }

    // An alias of the earlier version, admitted after a move, is about to
    // leave when the new owner disposes. Disposal waits for it instead of
    // reporting an active callback.
    [Fact]
    public void DisposalWaitsForAnAliasOfTheEarlierVersionToLeave()
    {
        NativeTransfer<int>? source = NativeTransfer<int>.Create(8, 7);
        NativeTransfer<int> alias = source.Value;
        using ManualResetEventSlim readerChecked = new(false);
        using ManualResetEventSlim releaseReader = new(false);
        using ManualResetEventSlim readerAdmitted = new(false);
        using ManualResetEventSlim releaseAdmitted = new(false);
        Thread? reader = null;
        Exception? readerFailure = null;
        NativeTransferRacesForTest.AfterActiveCheck = () =>
        {
            if (ReferenceEquals(Thread.CurrentThread, reader))
            {
                readerChecked.Set();
                Assert.True(releaseReader.Wait(Timeout));
            }
        };
        NativeTransferRacesForTest.AfterAdmission = () =>
        {
            if (ReferenceEquals(Thread.CurrentThread, reader))
            {
                readerAdmitted.Set();
                Assert.True(releaseAdmitted.Wait(Timeout));
            }
        };
        try
        {
            reader = new(() => readerFailure = Capture(() => alias.Read(static view => view[0])));
            reader.Start();
            Assert.True(readerChecked.Wait(Timeout));
            NativeTransfer<int> moved = NativeTransfer<int>.Move(ref source);
            releaseReader.Set();
            Assert.True(readerAdmitted.Wait(Timeout));

            // The alias leaves once disposal starts waiting.
            NativeTransferRacesForTest.BeforeBorrowWait = () =>
            {
                releaseAdmitted.Set();
                Assert.True(reader.Join(Timeout));
            };
            moved.Dispose();
            Assert.IsType<InvalidOperationException>(readerFailure);
            AssertReturned(moved);
        }
        finally
        {
            NativeTransferRacesForTest.Reset();
            releaseReader.Set();
            releaseAdmitted.Set();
        }
    }

    private static void AssertReturned(NativeTransfer<int> transfer)
    {
        NativeTransferStatistics statistics = transfer.CaptureSnapshot();
        Assert.Equal(NativeTransferLifecycle.Returned, statistics.Lifecycle);
        Assert.Equal(1, statistics.PayloadReturnCount);
        Assert.Equal(0, statistics.ActiveBorrowCount);
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
