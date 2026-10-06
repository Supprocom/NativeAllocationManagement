using System;
using System.Runtime.InteropServices;
using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Tests;

internal static partial class NativeGeneratedScenarios
{
    internal static void RunMappedCompositeFailures(int seed, int iterations, int traceCapacity, Action<string> trace)
    {
        SeededRandom random = new(seed);
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            bool failProducer = iteration % 2 != 0;
            int payload = random.Next(1, 1000);
            trace($"mapped-group seed={seed} iteration={iteration} producerFailure={failProducer} payload={payload} borrowed=128 ownedHeaders=128 tracing={traceCapacity}");
            using GeneratedAlignedBuffer provider = new(128);
            NativeMemoryBudget budget = new(128, traceCapacity);
            NativeArena arena = new(provider, 0, new NativeArenaPreparation(64, 64), budget);
            try
            {
                ArenaLease<int> source = arena.Scratch<int>(2, writer => writer.Fill(payload));
                provider.Dispose();
                Require(provider.ReleaseCount == 0 && arena.GetStatistics().BorrowedBytes == 128
                    && arena.GetStatistics().OutstandingNativeBytes == 128 && budget.CaptureStatistics().CommittedBytes == 128,
                    "closed provider lost registered hold or was charged as invented NAM payload");
                bool failed = false;
                bool resetRejected = false;
                bool outputsPublished = false;
                try
                {
                    scoped ArenaLease<int> first;
                    scoped ArenaLease<long> second;
                    scoped ArenaLease<byte> third;
                    scoped ArenaLease<int> fourth;
                    NativeLeaseOperations.InitializeScoped<int, int, long, byte, int>(source, arena, 1, 1, 1, 1,
                        (input, one, two, three, four) =>
                        {
                            Require(input[0] == payload && provider.ReleaseCount == 0,
                                "entered composite source lost its provider-backed payload");
                            try { arena.Reset(); }
                            catch (NativeAllocationInUseException) { resetRejected = true; }
                            one.Fill(input[0]);
                            if (failProducer) throw new InvalidOperationException("Generated mapped producer failure.");
                            two.Fill(42);
                            three.Fill(3);
                            four.Fill(7);
                        }, out first, out second, out third, out fourth);
                    outputsPublished = true;
                    NativeLeaseOperations.Access(source, first, second, third, fourth, (input, one, two, three, four) =>
                        Require(input[0] == payload && one[0] == payload && two[0] == 42 && three[0] == 3 && four[0] == 7,
                            "generated mapped composite output differs"));
                }
                catch (InvalidOperationException failure) when (string.Equals(failure.Message,
                    "Generated mapped producer failure.", StringComparison.Ordinal))
                { failed = true; }
                Require(resetRejected && failed == failProducer && outputsPublished == !failProducer,
                    "entered reset guard or all-or-nothing publication differs");
                NativePreparedArenaStatistics after = arena.CapturePreparedSnapshot();
                Require(after.ScopedUsedBytes == (failProducer ? 0 : 24)
                    && after.InitializerFailureCount == (failProducer ? 1 : 0)
                    && provider.ReleaseCount == 0 && budget.CaptureStatistics().CommittedBytes == 128,
                    "group rollback changed physical headers/provider obligation or invented publication");
                Require(source.Read(static view => view[0] + view[1]) == payload * 2,
                    "failed composite damaged previously published source");
                arena.RecycleScoped();
                arena.Reset();
                Require(arena.TrimRetainedMemory() == 128 && provider.ReleaseCount == 1
                    && budget.CaptureStatistics().CommittedBytes == 0 && budget.CaptureStatistics().FreeCount == 1
                    && arena.CapturePreparedSnapshot().RetainedBorrowedBytes == 0,
                    "mapped terminal maintenance did not return exact headers and provider hold once");
            }
            finally { arena.Dispose(); }
            trace($"mapped-group cleanup seed={seed} iteration={iteration} providerReturns=1 actualHeaderFrees=1 committed=0");
        }
    }

    private sealed unsafe class GeneratedAlignedBuffer : SafeBuffer
    {
        internal GeneratedAlignedBuffer(nuint length) : base(ownsHandle: true)
        {
            void* pointer = NativeMemory.AlignedAlloc(length, 64);
            if (pointer == null) throw new InvalidOperationException("Generated provider allocation failed.");
            SetHandle((IntPtr)pointer);
            Initialize(checked((ulong)length));
        }
        internal int ReleaseCount { get; private set; }
        protected override bool ReleaseHandle()
        {
            NativeMemory.AlignedFree((void*)handle);
            ReleaseCount++;
            return true;
        }
    }
}
