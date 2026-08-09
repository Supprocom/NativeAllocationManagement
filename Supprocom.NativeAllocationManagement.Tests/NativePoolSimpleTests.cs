using System.Reflection;
using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativePoolSimpleTests
{
    [Fact]
    public void RentPublishesOnlyCompleteInitialization()
    {
        using NativePool<int> pool = new(
            preLease: 8,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);

        Pooled<int> lease = pool.Rent(
            8,
            static writer => writer.Fill(17));
        try
        {
            Assert.Equal(8, lease.Length);
            Assert.Equal(8, lease.Capacity);
            Assert.Equal(
                136,
                lease.Read(static view =>
                {
                    int sum = 0;
                    foreach (int value in view.AsSpan())
                    {
                        sum += value;
                    }

                    return sum;
                }));
        }
        finally
        {
            lease.Dispose();
        }
    }

    [Fact]
    public void FailedInitializerReturnsTheSlabOnce()
    {
        using NativePool<int> pool = new(
            preLease: 4,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);

        Assert.Throws<MarkerException>(() => pool.Rent(
            4,
            static writer =>
            {
                writer.Write(1);
                throw new MarkerException();
            }));

        NativeOwnerStatistics afterFailure = pool.GetStatistics();
        Assert.Equal(0, afterFailure.RequestedBytes);
        Assert.Equal(1, afterFailure.AvailableSegmentCount);

        Pooled<int> lease = pool.Rent(
            4,
            static writer => writer.Fill(9));
        lease.Dispose();
        Assert.Equal(0, pool.GetStatistics().RequestedBytes);
    }

    [Fact]
    public void StaleCopyCannotAccessOrReturnAReusedSlab()
    {
        using NativePool<int> pool = new(
            preLease: 4,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Pooled<int> first = pool.Rent(
            4,
            static writer => writer.Fill(1));
        Pooled<int> stale = first;
        first.Dispose();

        Pooled<int> second = pool.Rent(
            4,
            static writer => writer.Fill(2));
        try
        {
            AssertAccessIsReturned(stale);
            AssertDisposeIsReturned(stale);
            Assert.Equal(
                8,
                second.Read(static view =>
                {
                    int sum = 0;
                    foreach (int value in view.AsSpan())
                    {
                        sum += value;
                    }

                    return sum;
                }));
        }
        finally
        {
            second.Dispose();
        }
    }

    [Fact]
    public void LeaseTokenExhaustionFailsBeforeSlabStateChanges()
    {
        using NativePool<int> pool = new(
            preLease: 1,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        FieldInfo kernelField = typeof(NativePool<int>).GetField(
            "_kernel",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        object kernel = kernelField.GetValue(pool)!;
        FieldInfo tokenField = kernel.GetType().GetField(
            "_leaseTokenCounter",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        tokenField.SetValue(kernel, long.MaxValue - 1);

        Pooled<int> finalLease = pool.Rent(
            1,
            static writer => writer.Write(1));
        finalLease.Dispose();

        Assert.Throws<InvalidOperationException>(() => pool.Rent(
            1,
            static writer => writer.Write(2)));
        NativeOwnerStatistics statistics = pool.GetStatistics();
        Assert.Equal(0, statistics.RequestedBytes);
        Assert.Equal(1, statistics.AvailableSegmentCount);
    }

    [Fact]
    public void ActiveBorrowRejectsOwnerDisposal()
    {
        using NativePool<int> pool = new(
            preLease: 1,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Pooled<int> lease = pool.Rent(
            1,
            static writer => writer.Write(3));

        lease.Access(view =>
        {
            Assert.Equal(3, view[0]);
            Assert.Throws<InvalidOperationException>(
                () => pool.Dispose());
        });

        lease.Dispose();
    }

    [Fact]
    public void PersistentLeaseProcessesOnlyTheSelectedPrefix()
    {
        using NativePool<int> pool = new(
            preLease: 8,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Pooled<int> lease = pool.Rent(
            8,
            static writer => writer.Fill(0));
        try
        {
            int first = lease.Process(
                6,
                4,
                static (values, value) =>
                {
                    values.Fill(value);
                    int sum = 0;
                    foreach (int item in values)
                    {
                        sum += item;
                    }

                    return sum;
                });
            int second = lease.Process(
                2,
                7,
                static (values, value) =>
                {
                    values.Fill(value);
                    return values.Length;
                });

            Assert.Equal(24, first);
            Assert.Equal(2, second);
            Assert.Equal(
                [7, 7, 4, 4, 4, 4, 0, 0],
                lease.Read(static view => view.AsSpan().ToArray()));
        }
        finally
        {
            lease.Dispose();
        }
    }

    [Fact]
    public void FailedPersistentProcessReleasesTheBorrow()
    {
        using NativePool<int> pool = new(
            preLease: 4,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Pooled<int> lease = pool.Rent(
            4,
            static writer => writer.Fill(0));

        try
        {
            lease.Process<int, int>(
                4,
                1,
                static (values, value) =>
                {
                    values.Fill(value);
                    throw new MarkerException();
                });
            Assert.Fail("The processor failure was not returned.");
        }
        catch (MarkerException)
        {
        }

        Assert.Equal(
            12,
            lease.Process(
                4,
                3,
                static (values, value) =>
                {
                    values.Fill(value);
                    return values.ToArray().Sum();
                }));
        lease.Dispose();
    }

    [Fact]
    public void PersistentProcessRejectsAnInvalidLogicalLength()
    {
        using NativePool<int> pool = new(
            preLease: 4,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Pooled<int> lease = pool.Rent(
            4,
            static writer => writer.Fill(0));

        try
        {
            lease.Process(
                -1,
                0,
                static (values, state) => state);
            Assert.Fail("The negative logical length was accepted.");
        }
        catch (ArgumentOutOfRangeException)
        {
        }

        try
        {
            lease.Process(
                5,
                0,
                static (values, state) => state);
            Assert.Fail("The excessive logical length was accepted.");
        }
        catch (ArgumentOutOfRangeException)
        {
        }

        lease.Dispose();
    }

    [Fact]
    public void CrossThreadUseFailsClosed()
    {
        using NativePool<int> pool = new(
            preLease: 2,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                pool.Rent(
                    2,
                    static writer => writer.Fill(4));
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.IsType<NativeAllocationStateException>(failure);
    }

    [Fact]
    public void TrimFreesOnlyIdleSlabs()
    {
        using NativePool<int> pool = new(
            preLease: 4,
            preAllocateBytes: 24,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Pooled<int> lease = pool.Rent(
            4,
            static writer => writer.Fill(1));

        nuint released = pool.TrimRetainedMemory();
        Assert.Equal((nuint)24, released);
        Assert.Equal(16, pool.GetStatistics().RetainedBytes);

        lease.Dispose();
        Assert.Equal((nuint)16, pool.TrimRetainedMemory());
        Assert.Equal(0, pool.GetStatistics().RetainedBytes);
    }

    private sealed class MarkerException : Exception;

    private static void AssertAccessIsReturned(Pooled<int> lease)
    {
        try
        {
            lease.Access(static view => view.Clear());
            Assert.Fail("The stale lease accessed returned storage.");
        }
        catch (NativeAllocationReturnedException)
        {
        }
    }

    private static void AssertDisposeIsReturned(Pooled<int> lease)
    {
        try
        {
            lease.Dispose();
            Assert.Fail("The stale lease returned storage twice.");
        }
        catch (NativeAllocationReturnedException)
        {
        }
    }
}
