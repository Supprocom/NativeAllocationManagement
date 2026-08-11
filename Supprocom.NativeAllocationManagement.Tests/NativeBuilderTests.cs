using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeBuilderTests
{
    [Fact]
    public void DirectBuilderGrowsAndPublishesExactLogicalLength()
    {
        using NativeBuilder<int> builder =
            new NativeBuilder<int>(preLease: 1);

        for (int value = 0; value < 100; value++)
        {
            builder.Append(value * 3);
        }

        Assert.Equal(100, builder.Count);
        Assert.True(builder.Capacity >= 100);
        NativeTransfer<int> transfer = builder.Complete();

        Assert.Equal(100, transfer.Length);
        Assert.True(transfer.Capacity >= transfer.Length);
        Assert.Equal(
            Enumerable.Range(0, 100)
                .Select(value => value * 3)
                .ToArray(),
            transfer.Read(static view => view.AsSpan().ToArray()));

        transfer.Dispose();
    }

    [Fact]
    public void RangeAppendWritesDirectlyIntoTheInitializedPrefix()
    {
        using NativeBuilder<uint> builder =
            new NativeBuilder<uint>(preLease: 2);

        builder.Append([2U, 3U]);
        builder.Append(5U);
        builder.Append([7U, 11U, 13U]);
        NativeTransfer<uint> transfer = builder.Complete();

        Assert.Equal(
            new uint[] { 2, 3, 5, 7, 11, 13 },
            transfer.Read(static view => view.AsSpan().ToArray()));
        transfer.Dispose();
    }

    [Fact]
    public void BoundedWriteCommitsZeroElements()
    {
        using NativeBuilder<int> builder =
            new NativeBuilder<int>(preLease: 4);

        builder.Write(
            0,
            static writer => writer.Commit(0));
        NativeTransfer<int> transfer = builder.Complete();

        Assert.Equal(0, transfer.Length);
        transfer.Dispose();
    }

    [Fact]
    public void BoundedWritePublishesOnlyTheCommittedPrefix()
    {
        using NativeBuilder<int> builder =
            new NativeBuilder<int>(preLease: 2);

        builder.Append(2);
        builder.Write(
            6,
            static writer =>
            {
                Span<int> values = writer.AsSpan();
                values[0] = 3;
                values[1] = 5;
                values[2] = 7;
                values[3] = 11;
                writer.Commit(3);
            });
        builder.Append(13);
        NativeTransfer<int> transfer = builder.Complete();

        Assert.Equal(5, transfer.Length);
        Assert.Equal(
            new[] { 2, 3, 5, 7, 13 },
            transfer.Read(
                static view => view.AsSpan().ToArray()));
        transfer.Dispose();
    }

    [Fact]
    public void BoundedWriteGrowsOnceAndCommitsTheFullRange()
    {
        using NativeBuilder<int> builder =
            new NativeBuilder<int>(preLease: 1);

        builder.Write(
            8,
            static writer =>
            {
                Span<int> values = writer.AsSpan();
                for (int index = 0; index < values.Length; index++)
                {
                    values[index] = index * 2;
                }

                writer.Commit(values.Length);
            });
        NativeTransfer<int> transfer = builder.Complete();

        Assert.Equal(
            new[] { 0, 2, 4, 6, 8, 10, 12, 14 },
            transfer.Read(
                static view => view.AsSpan().ToArray()));
        transfer.Dispose();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void InvalidCommitCountAbortsTheCompleteBuilder(
        int committedCount)
    {
        using NativeMetricsScope metrics = new();
        NativeBuilder<int> builder =
            new NativeBuilder<int>(preLease: 2);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => builder.Write(
                2,
                writer => writer.Commit(committedCount)));

        metrics.AssertBalanced();
        Assert.Throws<ObjectDisposedException>(
            () => builder.Append(1));
        builder.Dispose();
    }

    [Fact]
    public void MissingCommitAbortsTheCompleteBuilder()
    {
        using NativeMetricsScope metrics = new();
        NativeBuilder<int> builder =
            new NativeBuilder<int>(preLease: 2);

        Assert.Throws<InvalidOperationException>(
            () => builder.Write(
                2,
                static writer => writer.AsSpan().Fill(17)));

        metrics.AssertBalanced();
        Assert.Throws<ObjectDisposedException>(
            () => builder.Complete());
        builder.Dispose();
    }

    [Fact]
    public void DoubleCommitAbortsTheCompleteBuilder()
    {
        using NativeMetricsScope metrics = new();
        NativeBuilder<int> builder =
            new NativeBuilder<int>(preLease: 2);

        Assert.Throws<InvalidOperationException>(
            () => builder.Write(
                2,
                static writer =>
                {
                    writer.AsSpan()[0] = 19;
                    writer.Commit(1);
                    writer.Commit(1);
                }));

        metrics.AssertBalanced();
        builder.Dispose();
    }

    [Fact]
    public void CallbackFailureAbortsTheCompleteBuilder()
    {
        using NativeMetricsScope metrics = new();
        NativeBuilder<int> builder =
            new NativeBuilder<int>(preLease: 2);

        Assert.Throws<FormatException>(
            () => builder.Write(
                2,
                static writer =>
                {
                    writer.AsSpan()[0] = 23;
                    throw new FormatException("Expected test failure.");
                }));

        metrics.AssertBalanced();
        builder.Dispose();
    }

    [Fact]
    public void CallbackCancellationAbortsTheCompleteBuilder()
    {
        using NativeMetricsScope metrics = new();
        NativeBuilder<int> builder =
            new NativeBuilder<int>(preLease: 2);
        using CancellationTokenSource cancellation = new();

        Assert.Throws<OperationCanceledException>(
            () => builder.Write(
                2,
                writer =>
                {
                    writer.AsSpan()[0] = 29;
                    writer.Commit(1);
                    cancellation.Cancel();
                },
                cancellation.Token));

        metrics.AssertBalanced();
        builder.Dispose();
    }

    [Fact]
    public void BoundedWriteGrowthFailureReturnsStorageExactlyOnce()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            NativeBuilder<int> builder =
                new NativeBuilder<int>(preLease: 1);
            NativeMemoryTestHooks.FailNextAllocation();

            Assert.Throws<NativeAllocationFailedException>(
                () => builder.Write(
                    4,
                    static writer =>
                    {
                        writer.AsSpan().Fill(31);
                        writer.Commit(writer.Length);
                    }));

            builder.Dispose();
            builder.Dispose();
            NativeMemoryTestMetrics metrics =
                NativeMemoryTestHooks.Snapshot();
            Assert.Equal(
                metrics.AllocationCount,
                metrics.FreeCount);
            Assert.Equal(0, metrics.OutstandingNativeBytes);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public void ExclusiveBorrowPassesThroughNestedHelpers()
    {
        using NativeBuilder<int> builder =
            new NativeBuilder<int>(preLease: 2);

        builder.Borrow(WriteNestedValues);
        builder.Append(11);
        NativeTransfer<int> transfer = builder.Complete();

        Assert.Equal(
            new[] { 2, 3, 5, 7, 11 },
            transfer.Read(
                static view => view.AsSpan().ToArray()));
        transfer.Dispose();
    }

    [Fact]
    public void BorrowCallbackFailureReturnsStorageExactlyOnce()
    {
        using NativeMetricsScope metrics = new();
        NativeBuilder<int> builder =
            new NativeBuilder<int>(preLease: 2);

        Assert.Throws<FormatException>(
            () => builder.Borrow(WriteThenFail));

        builder.Dispose();
        builder.Dispose();
        metrics.AssertBalanced();
    }

    [Fact]
    public void BorrowCallbackCancellationReturnsStorageExactlyOnce()
    {
        using NativeMetricsScope metrics = new();
        NativeBuilder<int> builder =
            new NativeBuilder<int>(preLease: 2);
        using CancellationTokenSource cancellation = new();

        void Cancel(
            scoped ref NativeBuilderBorrow<int> borrow)
        {
            borrow.Append(13);
            cancellation.Cancel();
        }

        Assert.Throws<OperationCanceledException>(
            () => builder.Borrow(Cancel, cancellation.Token));

        builder.Dispose();
        metrics.AssertBalanced();
    }

    [Fact]
    public void OwnerUseDuringBorrowFailsClosed()
    {
        using NativeMetricsScope metrics = new();
        NativeBuilder<int> builder =
            new NativeBuilder<int>(preLease: 2);

        void UseOwner(
            scoped ref NativeBuilderBorrow<int> borrow)
        {
            _ = borrow.Count;
            builder.Append(17);
        }

        Assert.Throws<InvalidOperationException>(
            () => builder.Borrow(UseOwner));
        metrics.AssertBalanced();
        builder.Dispose();
    }

    [Fact]
    public async Task ConcurrentBorrowIsRejected()
    {
        using NativeBuilder<int> builder =
            new NativeBuilder<int>(preLease: 2);
        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim release = new(false);

        void HoldBorrow(
            scoped ref NativeBuilderBorrow<int> borrow)
        {
            borrow.Append(19);
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        }

        Task active = Task.Run(
            () => builder.Borrow(HoldBorrow));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Assert.Throws<InvalidOperationException>(
            () => builder.Borrow(
                static (scoped ref NativeBuilderBorrow<int> borrow) =>
                    borrow.Append(23)));
        release.Set();
        await active.WaitAsync(TimeSpan.FromSeconds(5));

        NativeTransfer<int> transfer = builder.Complete();
        Assert.Equal(
            new[] { 19 },
            transfer.Read(
                static view => view.AsSpan().ToArray()));
        transfer.Dispose();
    }

    [Fact]
    public void GeometricReallocationCreatesNoManagedIntermediateArray()
    {
        Type? resolvedAllocatorType =
            typeof(NativeBuilder<int>).Assembly.GetType(
                "Supprocom.NativeAllocationManagement.NativeBlockAllocator");
        Assert.NotNull(resolvedAllocatorType);
        Type allocatorType = resolvedAllocatorType;
        MethodInfo resize = Assert.Single(
            allocatorType.GetMethods(
                BindingFlags.Static | BindingFlags.NonPublic),
            static method =>
                method.Name == "Resize"
                && method.IsGenericMethodDefinition);

        OpCode[] instructions = ReadOpCodes(resize).ToArray();

        Assert.NotEmpty(instructions);
        Assert.DoesNotContain(
            instructions,
            static instruction =>
                instruction.Value == OpCodes.Newarr.Value);
    }

    [Fact]
    public void DirectBuilderGrowthPreservesEveryInitializedValue()
    {
        using NativeBuilder<long> builder =
            new NativeBuilder<long>(preLease: 1);

        for (long value = 0; value < 100; value++)
        {
            builder.Append(value * 5);
        }

        NativeTransfer<long> transfer = builder.Complete();

        Assert.Equal(100, transfer.Length);
        Assert.Equal(
            Enumerable.Range(0, 100)
                .Select(value => (long)value * 5)
                .ToArray(),
            transfer.Read(static view => view.AsSpan().ToArray()));
        transfer.Dispose();
    }

    [Fact]
    public void EmptyBuilderCompletesAsAnEmptyTransfer()
    {
        using NativeBuilder<long> builder = new NativeBuilder<long>();

        NativeTransfer<long> transfer = builder.Complete();

        Assert.Equal(0, transfer.Length);
        Assert.Equal(0, transfer.Capacity);
        transfer.Dispose();
    }

    [Fact]
    public void CompletionInvalidatesEveryBuilderOperation()
    {
        NativeBuilder<int> builder = new NativeBuilder<int>();
        builder.Append(17);
        NativeTransfer<int> transfer = builder.Complete();

        Assert.Throws<InvalidOperationException>(
            () => builder.Append(19));
        Assert.Throws<InvalidOperationException>(
            () => _ = builder.Count);
        Assert.Throws<InvalidOperationException>(
            () => builder.Complete());

        builder.Dispose();
        transfer.Dispose();
    }

    [Fact]
    public void DisposalReturnsBuilderStorageOnlyOnce()
    {
        using NativeMetricsScope metrics = new();
        NativeBuilder<int> builder =
            new NativeBuilder<int>(preLease: 4);
        builder.Append([1, 2, 3]);

        builder.Dispose();
        builder.Dispose();

        metrics.AssertBalanced();
        Assert.Throws<ObjectDisposedException>(
            () => builder.Append(4));
    }

    [Fact]
    public void GrowthFailureReleasesAllBuilderAllocations()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            NativeBuilder<int> builder =
                new NativeBuilder<int>(preLease: 1);
            builder.Append(1);
            NativeMemoryTestHooks.FailNextAllocation();

            Assert.Throws<NativeAllocationFailedException>(
                () => builder.Append(2));

            NativeMemoryTestMetrics metrics =
                NativeMemoryTestHooks.Snapshot();
            Assert.Equal(
                metrics.AllocationCount,
                metrics.FreeCount);
            Assert.Equal(0, metrics.OutstandingNativeBytes);
            Assert.Throws<ObjectDisposedException>(
                () => builder.Append(3));
            builder.Dispose();
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public void AppendCancellationReleasesBuilderStorage()
    {
        using NativeMetricsScope metrics = new();
        NativeBuilder<int> builder =
            new NativeBuilder<int>(preLease: 4);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => builder.Append(1, cancellation.Token));

        metrics.AssertBalanced();
        Assert.Throws<ObjectDisposedException>(
            () => builder.Append(2));
    }

    [Fact]
    public void CompletionCancellationReleasesBuilderStorage()
    {
        using NativeMetricsScope metrics = new();
        NativeBuilder<int> builder =
            new NativeBuilder<int>(preLease: 4);
        builder.Append([1, 2, 3]);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => builder.Complete(cancellation.Token));

        metrics.AssertBalanced();
        Assert.Throws<ObjectDisposedException>(
            () => builder.Append(4));
    }

    [Fact]
    public void DirectBuilderOwnsAndReleasesItsNativeBlock()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            using NativeBuilder<long> builder =
                new NativeBuilder<long>(preLease: 8);
            builder.Append([2L, 3L, 5L, 7L]);

            NativeTransfer<long> transfer = builder.Complete();

            Assert.Equal(
                17,
                transfer.Read(
                    static view =>
                    {
                        long total = 0;
                        foreach (long value in view.AsSpan())
                        {
                            total += value;
                        }

                        return total;
                    }));
            transfer.Dispose();
            NativeMemoryTestMetrics metrics =
                NativeMemoryTestHooks.Snapshot();
            Assert.Equal(1, metrics.AllocationCount);
            Assert.Equal(1, metrics.FreeCount);
            Assert.Equal(0, metrics.OutstandingNativeBytes);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public void BuilderDoesNotRequireAnAllocatorOwner()
    {
        using NativeBuilder<int> builder =
            new NativeBuilder<int>(preLease: 4);
        builder.Append([31, 37]);
        NativeTransfer<int> transfer = builder.Complete();
        Assert.Equal(
            new[] { 31, 37 },
            transfer.Read(static view => view.AsSpan().ToArray()));
        transfer.Dispose();
    }

    [Fact]
    public void AbandonedBuilderFinalizerReturnsItsAllocation()
    {
        NativeMemoryTestHooks.Reset();
        WeakReference abandoned = CreateAbandonedBuilder();

        for (int attempt = 0;
            attempt < 10 && abandoned.IsAlive;
            attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(abandoned.IsAlive);
        Assert.Equal(
            0,
            NativeMemoryTestHooks.Snapshot().OutstandingNativeBytes);
        NativeMemoryTestHooks.Reset();
    }

    [Fact]
    public async Task ConcurrentBuilderOperationsFailClosed()
    {
        NativeMemoryTestHooks.Reset();
        using NativeBuilder<int> builder = new NativeBuilder<int>();
        using ManualResetEventSlim appendEntered = new(false);
        using ManualResetEventSlim releaseAppend = new(false);
        NativeMemoryTestHooks.SetBeforeOperationEntry(
            operation =>
            {
                if (operation != "NativeBuilder.Append")
                {
                    return;
                }

                appendEntered.Set();
                releaseAppend.Wait();
            });

        try
        {
            Task first = Task.Run(() => builder.Append(41));
            Assert.True(
                appendEntered.Wait(TimeSpan.FromSeconds(5)));

            Assert.Throws<InvalidOperationException>(
                () => builder.Append(43));
            Assert.Throws<InvalidOperationException>(
                () => builder.Complete());

            releaseAppend.Set();
            await first;
        }
        finally
        {
            releaseAppend.Set();
            NativeMemoryTestHooks.Reset();
        }

        NativeTransfer<int> transfer = builder.Complete();
        Assert.Equal(
            41,
            transfer.Read(static view => view[0]));
        transfer.Dispose();
    }

    [Fact]
    public async Task ConcurrentCompletionAndDisposalReturnStorageOnce()
    {
        using NativeMetricsScope metrics = new();

        for (int attempt = 0; attempt < 64; attempt++)
        {
            NativeBuilder<int> builder =
                new NativeBuilder<int>(preLease: 4);
            builder.Append([47, 53, 59, 61]);
            using ManualResetEventSlim start = new(false);
            NativeTransfer<int>? transfer = null;
            Exception? completionFailure = null;
            Exception? disposalFailure = null;

            Task completion = Task.Run(
                () =>
                {
                    start.Wait();
                    try
                    {
                        transfer = builder.Complete();
                    }
                    catch (Exception failure)
                    {
                        completionFailure = failure;
                    }
                });
            Task disposal = Task.Run(
                () =>
                {
                    start.Wait();
                    try
                    {
                        builder.Dispose();
                    }
                    catch (Exception failure)
                    {
                        disposalFailure = failure;
                    }
                });

            start.Set();
            await Task.WhenAll(completion, disposal);

            if (transfer is not null)
            {
                Assert.Null(completionFailure);
                Assert.True(
                    disposalFailure is null
                    or InvalidOperationException);
                Assert.Equal(
                    new[] { 47, 53, 59, 61 },
                    transfer.Read(
                        static view => view.AsSpan().ToArray()));
                transfer.Dispose();
                Assert.Throws<ObjectDisposedException>(
                    transfer.Dispose);
            }
            else
            {
                Assert.Null(disposalFailure);
                Assert.True(
                    completionFailure is InvalidOperationException
                    or ObjectDisposedException);
            }

            builder.Dispose();
        }

        metrics.AssertBalanced();
    }

    [Fact]
    public void ConstructorAllocationFailurePublishesNoOwnership()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            NativeMemoryTestHooks.FailNextAllocation();

            Assert.Throws<NativeAllocationFailedException>(
                () => new NativeBuilder<int>(preLease: 4));

            NativeMemoryTestMetrics metrics =
                NativeMemoryTestHooks.Snapshot();
            Assert.Equal(0, metrics.AllocationCount);
            Assert.Equal(0, metrics.FreeCount);
            Assert.Equal(0, metrics.OutstandingNativeBytes);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public void GeometricReallocationReleasesEveryPhysicalBlock()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            using (NativeBuilder<int> builder =
                new NativeBuilder<int>(preLease: 1))
            {
                for (int value = 0; value < 100; value++)
                {
                    builder.Append(value);
                }

                NativeTransfer<int> transfer = builder.Complete();
                Assert.Equal(
                    Enumerable.Range(0, 100),
                    transfer.Read(
                        static view => view.AsSpan().ToArray()));
                transfer.Dispose();
            }

            NativeMemoryTestMetrics metrics =
                NativeMemoryTestHooks.Snapshot();
            Assert.True(metrics.AllocationCount > 1);
            Assert.Equal(
                metrics.AllocationCount,
                metrics.FreeCount);
            Assert.Equal(0, metrics.OutstandingNativeBytes);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateAbandonedBuilder()
    {
        NativeBuilder<int> builder =
            new NativeBuilder<int>(preLease: 8);
        builder.Append([1, 2, 3, 4]);
        return new WeakReference(builder);
    }

    private static void WriteNestedValues(
        scoped ref NativeBuilderBorrow<int> borrow)
    {
        AppendNestedValues(ref borrow);
        borrow.Write(
            2,
            static writer =>
            {
                Span<int> values = writer.AsSpan();
                values[0] = 5;
                values[1] = 7;
                writer.Commit(2);
            });
    }

    private static void AppendNestedValues(
        scoped ref NativeBuilderBorrow<int> borrow)
    {
        borrow.Append(2);
        borrow.Append(3);
    }

    private static void WriteThenFail(
        scoped ref NativeBuilderBorrow<int> borrow)
    {
        borrow.Append(13);
        throw new FormatException("Expected test failure.");
    }

    private static IEnumerable<OpCode> ReadOpCodes(MethodInfo method)
    {
        MethodBody? body = method.GetMethodBody();
        Assert.NotNull(body);
        byte[]? methodCode = body.GetILAsByteArray();
        Assert.NotNull(methodCode);
        byte[] code = methodCode;
        Dictionary<short, OpCode> opCodes = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static field => field.FieldType == typeof(OpCode))
            .Select(static field => (OpCode)field.GetValue(null)!)
            .ToDictionary(static opCode => opCode.Value);

        for (int offset = 0; offset < code.Length;)
        {
            short value = code[offset++] == 0xfe
                ? unchecked((short)(0xfe00 | code[offset++]))
                : code[offset - 1];
            OpCode opCode = opCodes[value];
            yield return opCode;
            offset += GetOperandSize(opCode, code, offset);
        }
    }

    private static int GetOperandSize(
        OpCode opCode,
        byte[] code,
        int operandOffset) => opCode.OperandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget
                or OperandType.ShortInlineI
                or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget
                or OperandType.InlineField
                or OperandType.InlineI
                or OperandType.InlineMethod
                or OperandType.InlineSig
                or OperandType.InlineString
                or OperandType.InlineTok
                or OperandType.InlineType
                or OperandType.ShortInlineR => 4,
            OperandType.InlineI8
                or OperandType.InlineR => 8,
            OperandType.InlineSwitch => checked(
                4 + (4 * BitConverter.ToInt32(code, operandOffset))),
            _ => throw new InvalidOperationException(
                $"Unsupported IL operand type {opCode.OperandType}.")
        };

    private sealed class NativeMetricsScope : IDisposable
    {
        internal NativeMetricsScope()
        {
            NativeMemoryTestHooks.Reset();
        }

        internal void AssertBalanced()
        {
            NativeMemoryTestMetrics metrics =
                NativeMemoryTestHooks.Snapshot();
            Assert.Equal(
                metrics.AllocationCount,
                metrics.FreeCount);
            Assert.Equal(0, metrics.OutstandingNativeBytes);
        }

        public void Dispose() => NativeMemoryTestHooks.Reset();
    }
}
