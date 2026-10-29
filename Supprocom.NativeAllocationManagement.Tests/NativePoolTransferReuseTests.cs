using System.Reflection;
using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement.Tests;

// A disposed transferable lease parks its control on the disposing thread for
// that thread's next transferable rent of the same element type.
public sealed class NativePoolTransferReuseTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // Length and Capacity read a transferable lease's shape in its thread's
    // cache without entering the pool; once the pool is disposed, the
    // invalidated transfer is refused as before.
    [Fact]
    public void ATransfersShapeIsReadUntilItsPoolInvalidatesIt()
    {
        NativeTransfer<int> transfer;
        using (NativeConcurrentPool<int> pool = new(preLease: 16))
        {
            transfer = pool.RentTransferable(4, 7);
            Assert.Equal(4, transfer.Length);
            Assert.True(transfer.Capacity >= 4);
        }

        Assert.Equal(NativeTransferLifecycle.Invalidated, transfer.CaptureSnapshot().Lifecycle);
        Assert.ThrowsAny<InvalidOperationException>(() => transfer.Length);
        Assert.ThrowsAny<InvalidOperationException>(() => transfer.Capacity);
        transfer.Dispose();
    }

    [Fact]
    public void ADisposedTransferControlServesTheNextRentAndKeepsStaleValuesReturned()
    {
        using NativeConcurrentPool<int> pool = new();
        NativeTransfer<int> first = pool.RentTransferable(3, static writer => writer.Fill(1));
        object control = first.ControlForTest!;
        first.Dispose();

        NativeTransfer<int> second = pool.RentTransferable(2, static writer => writer.Fill(4));
        Assert.Same(control, second.ControlForTest);
        Assert.Equal(4, second.Read(static view => view[1]));
        Assert.Equal(2, second.Length);
        Assert.Equal(0, second.CaptureSnapshot().MoveCount);
        Assert.True(second.CaptureSnapshot().BindingIsActive);

        Assert.Throws<InvalidOperationException>(() => first.Read(static view => view[0]));
        Assert.Throws<InvalidOperationException>(first.Dispose);
        NativeTransferStatistics stale = first.CaptureSnapshot();
        Assert.Equal(NativeTransferLifecycle.Returned, stale.Lifecycle);
        Assert.False(stale.BindingIsActive);
        Assert.Equal(1, stale.PayloadReturnCount);
        Assert.Equal(4, second.Read(static view => view[0]));

        second.Dispose();
        Assert.Equal(0, pool.GetStatistics().InitializedPayloadBytes);
    }

    [Fact]
    public void AReusedControlServesAnotherPoolAndMovesFromItsNewLifetime()
    {
        using NativeConcurrentPool<int> left = new();
        using NativeConcurrentPool<int> right = new();
        NativeTransfer<int> first = left.RentTransferable(2, static writer => writer.Fill(1));
        object control = first.ControlForTest!;
        first.Dispose();

        NativeTransfer<int>? second = right.RentTransferable(2, static writer => writer.Fill(7));
        Assert.Same(control, second.Value.ControlForTest);
        Assert.Equal(right.Id, second.Value.CaptureSnapshot().OwnerId);
        NativeTransfer<int> moved = NativeTransfer<int>.Move(ref second);
        Assert.Equal(1, moved.CaptureSnapshot().MoveCount);
        Assert.Equal(7, moved.Read(static view => view[1]));
        moved.Dispose();
        Assert.Equal(0, left.GetStatistics().InitializedPayloadBytes);
        Assert.Equal(0, right.GetStatistics().InitializedPayloadBytes);
    }

    [Fact]
    public void APoolTransferControlCountsItsDeclaredFields()
    {
        using NativeConcurrentPool<int> pool = new();
        using NativeTransfer<int> owner = pool.RentTransferable(4, static writer => writer.Fill(2));
        long representedBytes = 0;
        for (Type? type = owner.ControlForTest!.GetType(); type is not null && type != typeof(object); type = type.BaseType)
        {
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                Type fieldType = field.FieldType;
                representedBytes += !fieldType.IsValueType ? IntPtr.Size : fieldType == typeof(long) ? sizeof(long)
                    : fieldType == typeof(int) ? sizeof(int) : fieldType == typeof(bool) ? sizeof(bool)
                    : fieldType == typeof(NativeBlock) ? Unsafe.SizeOf<NativeBlock>()
                    : fieldType == typeof(NativeBlockSlot) ? Unsafe.SizeOf<NativeBlockSlot>()
                    : throw new InvalidOperationException($"Unaccounted control field {field.Name}");
            }
        }

        Assert.Equal(representedBytes, owner.CaptureSnapshot().ControlFieldBytes);
    }

    [Fact]
    public void AProcessorOnATransferRunningDuringRevocationExitsThroughTheGeneration()
    {
        using NativeConcurrentPool<int> pool = new();
        BlockingProcessor.Entered.Reset();
        BlockingProcessor.Release.Reset();
        int result = 0;
        Thread owner = new(() =>
        {
            using NativeTransfer<int> transfer = pool.RentTransferable(4, static writer => writer.Fill(5));
            result = transfer.Process<int, int, BlockingProcessor>(1);
        });
        owner.Start();
        Assert.True(BlockingProcessor.Entered.Wait(Timeout));

        Assert.Equal(1, pool.CurrentGenerationActiveOperationsForTest);
        Assert.Throws<NativeAllocationInUseException>(pool.ReleaseLeasesToNativeMemory);
        BlockingProcessor.Release.Set();
        Assert.True(owner.Join(Timeout));

        Assert.Equal(6, result);
        Assert.Equal(0, pool.CurrentGenerationActiveOperationsForTest);
        Assert.Equal(0, pool.GetStatistics().InitializedPayloadBytes);
        pool.ReleaseLeasesToNativeMemory();
    }

    [Fact]
    public void TransfersReturnedByAnotherThreadWhileTheRenterKeepsRentingStayExact()
    {
        using NativeConcurrentPool<int> pool = new();
        using System.Collections.Concurrent.BlockingCollection<NativeTransfer<int>> queue = new(8);
        Exception? failure = null;
        Thread consumer = new(() =>
        {
            try
            {
                foreach (NativeTransfer<int> transfer in queue.GetConsumingEnumerable())
                {
                    if (transfer.Process<int, int, LastPlus>(0) != transfer.Length)
                    {
                        throw new InvalidOperationException("A transfer lost its values.");
                    }

                    transfer.Dispose();
                }
            }
            catch (InvalidOperationException exception)
            {
                failure = exception;
            }
        });
        consumer.Start();
        for (int iteration = 0; iteration < 4000; iteration++)
        {
            int length = 1 + (iteration & 15);
            queue.Add(pool.RentTransferable(length, writer => writer.Fill(length)));
            if ((iteration & 255) == 0)
            {
                _ = pool.GetStatistics();
            }
        }

        queue.CompleteAdding();
        Assert.True(consumer.Join(Timeout));
        Assert.Null(failure);
        Assert.Equal(0, pool.GetStatistics().InitializedPayloadBytes);
        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
    }

    private struct LastPlus : INativeSpanProcessor<int, int, int>
    {
        public static int Invoke(Span<int> span, in int add) => span[^1] + add;
    }

    private struct BlockingProcessor : INativeSpanProcessor<int, int, int>
    {
        internal static readonly ManualResetEventSlim Entered = new(false);
        internal static readonly ManualResetEventSlim Release = new(false);

        public static int Invoke(Span<int> span, in int add)
        {
            Entered.Set();
            Assert.True(Release.Wait(Timeout));
            return span[^1] + add;
        }
    }
}
