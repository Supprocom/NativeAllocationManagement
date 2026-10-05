using System.Runtime.CompilerServices;
using Xunit.Abstractions;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeGeneratedOwnershipModelTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(17)]
    [InlineData(101)]
    [InlineData(379)]
    public void SeededPublicProgramsMatchIndependentByteAndOwnershipModels(int seed)
    {
        List<string> trace = [];
        try
        {
            NativeGeneratedScenarios.RunAdmission(seed, 2048, trace.Add);
            NativeGeneratedScenarios.RunSharing(seed, 512, trace.Add);
            NativeGeneratedScenarios.RunPreparedPool(seed, 512, trace.Add);
            NativeGeneratedScenarios.RunPreparedArena(seed, 512, trace.Add);
            NativeGeneratedScenarios.RunLayouts(seed, 64, trace.Add);
            NativeGeneratedScenarios.RunOutliers(seed, 128, trace.Add);
        }
        finally { SaveTrace(seed, "models", trace); }
    }

    [Theory]
    [InlineData(17)]
    [InlineData(379)]
    public async Task SeededSchedulesRespectLegalWeakAndEnteredBorrowLinearization(int seed)
    {
        List<string> trace = [];
        try
        {
            await NativeGeneratedScenarios.RunSharingSchedulesAsync(seed, 64, trace.Add);
            await NativeGeneratedScenarios.RunBorrowReturnSchedulesAsync(seed, 32, trace.Add);
        }
        finally { SaveTrace(seed, "schedules", trace); }
    }

    [Theory]
    [InlineData(17)]
    [InlineData(379)]
    public void FailedAcquisitionAndConsumedReturnPreserveIndependentCharges(int seed)
    {
        NativeGeneratedScenarios.SeededRandom random = new(seed);
        List<string> trace = [];
        try
        {
            for (int step = 0; step < 64; step++)
            {
                int length = random.Next(1, 17);
                NativeMemoryBudget budget = new(length * 4L);
                Assert.True(budget.TryReserve<int>(length, out NativeMemoryReservation<int>? permission, out _));
                NativeMemoryTestHooks.FailNextAllocation();
                Assert.Throws<NativeAllocationFailedException>(() => permission.Value.PrepareBacking());
                Assert.Equal(length * 4L, budget.CaptureStatistics().ReservedBytes);
                Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
                NativeMemoryReservation<int> witness = permission.Value;
                Assert.Throws<AggregateException>(() => NativeMemoryReservation<int>.Activate(ref permission, static _ =>
                {
                    NativeMemoryTestHooks.FailAtManagedPublicationBoundary(4);
                    throw new InvalidOperationException("generated producer failure");
                }));
                Assert.Null(permission);
                Assert.Equal(length * 4L, budget.CaptureStatistics().CommittedBytes);
                Assert.False(witness.CaptureSnapshot().BindingIsActive);
                Assert.True(witness.CaptureSnapshot().HasReservationReturnObligation);
                Assert.Equal(1, budget.CaptureAdmissionStatistics().OutstandingReservationCount);
                Assert.True(witness.TryCompletePayloadReturn());
                Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
                Assert.Equal(0, budget.CaptureAdmissionStatistics().OutstandingReservationCount);
                Assert.Throws<InvalidOperationException>(witness.Dispose);
                trace.Add($"fault seed={seed} step={step} length={length} failedAcquisitionReserved={length * 4} failedReturnCommitted={length * 4} finalCommitted=0");
            }
        }
        finally { SaveTrace(seed, "faults", trace); }
    }

    [Fact]
    public void BoundedReuseSoakReleasesPayloadAndItsControlMetadata()
    {
        List<WeakReference> controls = [];
        for (int iteration = 0; iteration < 64; iteration++) controls.Add(CreateAndReleaseControl(iteration));
        for (int attempt = 0; attempt < 8; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (controls.TrueForAll(static control => !control.IsAlive)) break;
        }
        Assert.All(controls, static control => Assert.False(control.IsAlive));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateAndReleaseControl(int value)
    {
        NativeMemoryBudget budget = new(4096);
        Assert.True(budget.TryReserve<int>(1024, out NativeMemoryReservation<int>? permission, out _));
        NativeTransfer<int>? unique = NativeMemoryReservation<int>.Activate(ref permission, writer => writer.Fill(value));
        WeakReference control = new(unique.Value.ControlForTest!);
        NativeShared<int> owner = NativeShared<int>.Create(ref unique, new(4, 2));
        Assert.True(owner.TryDowngrade(out NativeWeak<int> weak, out _));
        owner.Dispose();
        Assert.True(weak.IsExpired);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.False(weak.TryUpgrade(out _, out _));
        weak.Dispose();
        Assert.Equal(0, weak.CaptureSnapshot().ManagedBankBytes);
        return control;
    }

    private void SaveTrace(int seed, string category, List<string> trace)
    {
        output.WriteLine($"seed={seed};category={category};operations={trace.Count}");
        if (!PackageFixtureEvidence.IsEnabled(Environment.GetEnvironmentVariable("NAM_RETAIN_PACKAGE_EVIDENCE"))) return;
        string directory = Path.Combine(Path.GetTempPath(), "nam-generated-runtime", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllLines(Path.Combine(directory, "operations.log"), trace);
        output.WriteLine($"generatedRuntimeEvidence={directory}");
    }
}
