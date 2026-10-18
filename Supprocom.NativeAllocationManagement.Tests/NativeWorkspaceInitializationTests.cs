namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeWorkspaceInitializationTests
{
    [Fact]
    public void CheckedInitializationDoesNotClearTheRangeBeforeOverwritingIt()
    {
        NativeMemoryStatistics before = NativeMemoryDiagnostics.Snapshot();
        using NativeWorkspace<int> workspace = new(64);
        workspace.Initialize(16, static writer => writer.Fill(7));
        Assert.Equal(112, workspace.Read(static view =>
        {
            int sum = 0;
            foreach (ref readonly int value in view.AsSpan()) sum += value;
            return sum;
        }));
        NativeMemoryStatistics after = NativeMemoryDiagnostics.Snapshot();
        Assert.Equal(before.StorageClearCount, after.StorageClearCount);
        Assert.Equal(before.StorageClearBytes, after.StorageClearBytes);
        Assert.Equal(64, workspace.GetStatistics().PeakInitializedPayloadBytes);
    }

    [Fact]
    public void DirectProcessingClearsOnlyNewSuffixesAndKeepsEarlierContents()
    {
        NativeMemoryStatistics before = NativeMemoryDiagnostics.Snapshot();
        using NativeWorkspace<int> workspace = new(8);
        Assert.Equal(0, workspace.Process(2, 0, static (values, _) =>
        {
            int sum = values[0] + values[1];
            values.Fill(7);
            return sum;
        }));
        Assert.Equal(14, workspace.Process(4, 0, static (values, _) =>
        {
            Assert.Equal(0, values[2]);
            Assert.Equal(0, values[3]);
            return values[0] + values[1];
        }));
        workspace.Reset();
        Assert.Equal(14, workspace.Process(2, 0, static (values, _) => values[0] + values[1]));
        Assert.Equal(0, workspace.Process(0, 0, static (values, _) => values.Length));
        NativeMemoryStatistics after = NativeMemoryDiagnostics.Snapshot();
        Assert.Equal(before.StorageClearCount + 2, after.StorageClearCount);
        Assert.Equal(before.StorageClearBytes + 16, after.StorageClearBytes);
        Assert.Equal(before.WrittenClearBytes + 16, after.WrittenClearBytes);
        Assert.Equal(before.AllocationCount + 1, after.AllocationCount);
        Assert.Equal(16, workspace.GetStatistics().PeakInitializedPayloadBytes);
    }

    [Fact]
    public void CheckedInitializationEstablishesAReusablePrefixWithoutExtraPreparationState()
    {
        NativeMemoryStatistics before = NativeMemoryDiagnostics.Snapshot();
        using NativeWorkspace<int> workspace = new(8);
        workspace.Initialize(2, static writer => writer.Fill(11));
        workspace.Reset();
        Assert.Equal(22, workspace.Process(4, 0, static (values, _) =>
        {
            Assert.Equal(0, values[2]);
            Assert.Equal(0, values[3]);
            return values[0] + values[1];
        }));
        NativeMemoryStatistics after = NativeMemoryDiagnostics.Snapshot();
        Assert.Equal(before.StorageClearCount + 1, after.StorageClearCount);
        Assert.Equal(before.StorageClearBytes + 8, after.StorageClearBytes);
        Assert.Equal(16, workspace.GetStatistics().PeakInitializedPayloadBytes);
    }

    [Fact]
    public void FailedInitializationCannotPromoteAnUnwrittenSuffix()
    {
        using NativeWorkspace<int> workspace = new(8);
        workspace.Initialize(2, static writer => writer.Fill(5));
        workspace.Reset();
        Assert.Throws<InvalidOperationException>(() => workspace.Initialize(8, static writer =>
        {
            writer.Write(77);
            throw new InvalidOperationException("Injected partial initializer failure.");
        }));
        Assert.Equal(8, workspace.GetStatistics().PeakInitializedPayloadBytes);
        Assert.Throws<InvalidOperationException>(() => workspace.Read(static view => view[0]));
        Assert.Equal(0, workspace.Process(4, 0, static (values, _) => values[2] + values[3]));
        Assert.Equal(16, workspace.GetStatistics().PeakInitializedPayloadBytes);
    }

    [Fact]
    public void CancellationBeforeProcessingDoesNotClearOrAdvanceThePrefix()
    {
        NativeMemoryStatistics before = NativeMemoryDiagnostics.Snapshot();
        using NativeWorkspace<int> workspace = new(8);
        Assert.Throws<OperationCanceledException>(() => workspace.Process<int, int>(4, 0,
            static (_, _) => throw new InvalidOperationException("A canceled callback must not run."),
            new CancellationToken(canceled: true)));
        Assert.Equal(0, workspace.GetStatistics().PeakInitializedPayloadBytes);
        Assert.Equal(before.StorageClearCount, NativeMemoryDiagnostics.Snapshot().StorageClearCount);
        Assert.Equal(0, workspace.Process(4, 0, static (values, _) => values[0] + values[3]));
    }

    [Fact]
    public void AFailedDirectProcessorStillLeavesItsEntireEnteredRangeInitialized()
    {
        NativeMemoryStatistics before = NativeMemoryDiagnostics.Snapshot();
        using NativeWorkspace<int> workspace = new(8);
        Assert.Throws<InvalidOperationException>(() => workspace.Process(6, static values =>
        {
            values[0] = 9;
            throw new InvalidOperationException("Injected processor failure.");
        }, static values => values[0]));
        Assert.Equal(9, workspace.Process(6, 0, static (values, _) =>
        {
            foreach (ref readonly int value in values[1..]) Assert.Equal(0, value);
            return values[0];
        }));
        Assert.Equal(24, workspace.GetStatistics().PeakInitializedPayloadBytes);
        Assert.Equal(before.StorageClearCount + 1, NativeMemoryDiagnostics.Snapshot().StorageClearCount);
        Assert.Equal(before.StorageClearBytes + 24, NativeMemoryDiagnostics.Snapshot().StorageClearBytes);
    }
}
