using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeLeaseOperationsTests
{
    [Fact]
    public void EveryCompositeOverloadUsesDirectBoundedStorage()
    {
        NativeMemoryTestHooks.Reset();
        using NativeConcurrentPool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeConcurrentPool<int> secondPool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeConcurrentArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);

        ConcurrentPooled<int> first = pool.Rent(2, static writer => writer.Fill(default!));
        ConcurrentPooled<int> second = pool.Rent(2, static writer => writer.Fill(default!));
        ConcurrentPooled<int> third = secondPool.Rent(2, static writer => writer.Fill(default!));
        ConcurrentArenaLease<int> arenaFirst = arena.Scratch<int>(2, static writer => writer.Fill(default!));
        ConcurrentArenaLease<int> arenaSecond = arena.Scratch<int>(2, static writer => writer.Fill(default!));
        ConcurrentArenaLease<long> arenaThird = arena.Scratch<long>(2, static writer => writer.Fill(default!));
        ConcurrentArenaLease<byte> arenaFourth = arena.Scratch<byte>(2, static writer => writer.Fill(default!));

        first.Access(value => value[0] = 10);
        NativeLeaseOperations.Access(first, second, (left, right) =>
        {
            left[0] = 11;
            right[0] = 22;
        });
        NativeLeaseOperations.Access(first, second, third, (left, middle, right) =>
        {
            left[1] = middle[0] + right[0];
        });
        NativeLeaseOperations.Access(first, arenaFirst, (pooled, scratch) =>
        {
            scratch[0] = pooled[1];
        });
        NativeLeaseOperations.Access(
            first,
            arenaFirst,
            arenaSecond,
            arenaThird,
            arenaFourth,
            (pooled, one, two, three, four) =>
            {
                pooled[0] = 26;
                one[0] = 27;
                two[0] = 28;
                three[0] = 29;
                four[0] = 30;
            });
        NativeLeaseOperations.Access(
            first,
            second,
            third,
            arenaFirst,
            arenaSecond,
            (faces, vertices, indices, slices, upload) =>
            {
                faces[0] = 31;
                vertices[0] = 32;
                indices[0] = 33;
                slices[0] = 34;
                upload[0] = 35;
            });
        NativeLeaseOperations.Access(
            first,
            second,
            third,
            arenaThird,
            (cells, faces, masks, sections) =>
            {
                cells[0] = 36;
                faces[0] = 37;
                masks[0] = 38;
                sections[0] = 39;
            });

        Assert.Equal(36, first[0]);
        Assert.Equal(37, second[0]);
        Assert.Equal(38, third[0]);
        Assert.Equal(22, first[1]);
        Assert.Equal(34, arenaFirst[0]);
        Assert.Equal(35, arenaSecond[0]);
        Assert.Equal(39, arenaThird[0]);
        Assert.Equal(30, arenaFourth[0]);
    }

    [Fact]
    public void LaterEntryFailureReleasesEarlierTokensAndCallbackFailureCleansUp()
    {
        NativeMemoryTestHooks.Reset();
        using NativeConcurrentPool<int> goodPool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeConcurrentPool<int> stalePool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ConcurrentPooled<int> good = goodPool.Rent(1, static writer => writer.Fill(default!));
        ConcurrentPooled<int> stale = stalePool.Rent(1, static writer => writer.Fill(default!));
        stalePool.ReturnMemoryToNativeMemory();

        good[0] = 40;
        Assert.Equal(40, good[0]);

        NativeAllocationException? entryFailure = null;
        try
        {
            NativeLeaseOperations.Access(good, stale, static (_, _) => { });
        }
        catch (NativeAllocationException exception)
        {
            entryFailure = exception;
        }

        Assert.NotNull(entryFailure);
        good[0] = 41;
        Assert.Equal(41, good[0]);

        bool callbackFailed = false;
        try
        {
            NativeLeaseOperations.Access(good, good, static (_, _) => throw new InvalidOperationException("callback"));
        }
        catch (InvalidOperationException)
        {
            callbackFailed = true;
        }

        Assert.True(callbackFailed);
        good.Dispose();
        stale.Dispose();
        goodPool.ReturnMemoryToNativeMemory();
        stalePool.Dispose();
    }

    [Fact]
    public void EveryCompositeOverloadReleasesEarlierTokensAfterLateEntryFailure()
    {
        NativeMemoryTestHooks.Reset();
        using NativeConcurrentPool<int> tripleFirstPool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeConcurrentPool<int> tripleSecondPool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ConcurrentPooled<int> tripleFirst = tripleFirstPool.Rent(1, static writer => writer.Fill(default!));
        ConcurrentPooled<int> tripleStale = tripleSecondPool.Rent(1, static writer => writer.Fill(default!));
        ConcurrentPooled<int> tripleThird = tripleSecondPool.Rent(1, static writer => writer.Fill(default!));
        tripleSecondPool.ReturnMemoryToNativeMemory();
        NativeAllocationException? tripleFailure = null;
        try
        {
            NativeLeaseOperations.Access(tripleFirst, tripleStale, tripleThird, static (_, _, _) => { });
        }
        catch (NativeAllocationException exception)
        {
            tripleFailure = exception;
        }
        Assert.NotNull(tripleFailure);
        tripleFirst[0] = 11;
        Assert.Equal(11, tripleFirst[0]);

        using NativeConcurrentPool<int> pooledFirstPool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeConcurrentArena staleArena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ConcurrentPooled<int> pooledFirst = pooledFirstPool.Rent(1, static writer => writer.Fill(default!));
        ConcurrentArenaLease<int> staleArenaLease = staleArena.Scratch<int>(1, static writer => writer.Fill(default!));
        staleArena.ReturnMemoryToNativeMemory();
        NativeAllocationException? pooledArenaFailure = null;
        try
        {
            NativeLeaseOperations.Access(pooledFirst, staleArenaLease, static (_, _) => { });
        }
        catch (NativeAllocationException exception)
        {
            pooledArenaFailure = exception;
        }
        Assert.NotNull(pooledArenaFailure);
        pooledFirst[0] = 12;
        Assert.Equal(12, pooledFirst[0]);

        using NativeConcurrentPool<int> quintuplePool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeConcurrentArena goodArena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeConcurrentArena lateArena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ConcurrentPooled<int> first = quintuplePool.Rent(1, static writer => writer.Fill(default!));
        ConcurrentPooled<int> second = quintuplePool.Rent(1, static writer => writer.Fill(default!));
        ConcurrentPooled<int> third = quintuplePool.Rent(1, static writer => writer.Fill(default!));
        ConcurrentArenaLease<int> fourth = goodArena.Scratch<int>(1, static writer => writer.Fill(default!));
        ConcurrentArenaLease<int> fifth = lateArena.Scratch<int>(1, static writer => writer.Fill(default!));
        lateArena.ReturnMemoryToNativeMemory();
        NativeAllocationException? quintupleFailure = null;
        try
        {
            NativeLeaseOperations.Access(first, second, third, fourth, fifth, static (_, _, _, _, _) => { });
        }
        catch (NativeAllocationException exception)
        {
            quintupleFailure = exception;
        }
        Assert.NotNull(quintupleFailure);
        first[0] = 13;
        second[0] = 14;
        third[0] = 15;
        fourth[0] = 16;
        Assert.Equal(13, first[0]);
        Assert.Equal(14, second[0]);
        Assert.Equal(15, third[0]);
        Assert.Equal(16, fourth[0]);
    }

    [Fact]
    public void EveryCompositeOverloadCleansUpAllTokensWhenTheCallbackThrows()
    {
        NativeMemoryTestHooks.Reset();
        using NativeConcurrentPool<int> firstPool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeConcurrentPool<int> secondPool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeConcurrentArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ConcurrentPooled<int> first = firstPool.Rent(1, static writer => writer.Fill(default!));
        ConcurrentPooled<int> second = firstPool.Rent(1, static writer => writer.Fill(default!));
        ConcurrentPooled<int> third = secondPool.Rent(1, static writer => writer.Fill(default!));
        ConcurrentArenaLease<int> fourth = arena.Scratch<int>(1, static writer => writer.Fill(default!));
        ConcurrentArenaLease<int> fifth = arena.Scratch<int>(1, static writer => writer.Fill(default!));
        ConcurrentArenaLease<long> sixth = arena.Scratch<long>(1, static writer => writer.Fill(default!));
        ConcurrentArenaLease<byte> seventh = arena.Scratch<byte>(1, static writer => writer.Fill(default!));

        bool tripleThrown = false;
        try
        {
            NativeLeaseOperations.Access(first, second, third, static (_, _, _) => throw new InvalidOperationException());
        }
        catch (InvalidOperationException)
        {
            tripleThrown = true;
        }

        bool pooledArenaThrown = false;
        try
        {
            NativeLeaseOperations.Access(first, fourth, static (_, _) => throw new InvalidOperationException());
        }
        catch (InvalidOperationException)
        {
            pooledArenaThrown = true;
        }

        bool quintupleThrown = false;
        try
        {
            NativeLeaseOperations.Access(first, second, third, fourth, fifth, static (_, _, _, _, _) => throw new InvalidOperationException());
        }
        catch (InvalidOperationException)
        {
            quintupleThrown = true;
        }

        bool pooledFourArenaThrown = false;
        try
        {
            NativeLeaseOperations.Access(
                first,
                fourth,
                fifth,
                sixth,
                seventh,
                static (_, _, _, _, _) =>
                    throw new InvalidOperationException());
        }
        catch (InvalidOperationException)
        {
            pooledFourArenaThrown = true;
        }

        Assert.True(tripleThrown);
        Assert.True(pooledArenaThrown);
        Assert.True(quintupleThrown);
        Assert.True(pooledFourArenaThrown);

        first[0] = 21;
        second[0] = 22;
        third[0] = 23;
        fourth[0] = 24;
        fifth[0] = 25;
        sixth[0] = 26;
        seventh[0] = 27;
        Assert.Equal(21, first[0]);
        Assert.Equal(22, second[0]);
        Assert.Equal(23, third[0]);
        Assert.Equal(24, fourth[0]);
        Assert.Equal(25, fifth[0]);
        Assert.Equal(26, sixth[0]);
        Assert.Equal(27, seventh[0]);
    }

    [Fact]
    public void SameOwnerAliasAndLifecycleTransitionsAreSafeAroundCompositeEntry()
    {
        NativeMemoryTestHooks.Reset();
        using NativeConcurrentPool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ConcurrentPooled<int> lease = pool.Rent(1, static writer => writer.Fill(default!));

        NativeLeaseOperations.Access(lease, lease, (first, second) =>
        {
            first[0] = 7;
            second[0] += 1;
        });
        Assert.Equal(8, lease[0]);

        NativeAllocationException? strictFailure = null;
        NativeLeaseOperations.Access(lease, lease, (_, _) =>
        {
            try
            {
                pool.ReturnMemoryToNativeMemory();
            }
            catch (NativeAllocationException exception)
            {
                strictFailure = exception;
            }
        });

        Assert.IsType<NativeAllocationInUseException>(strictFailure);
        pool.ReturnMemoryToNativeMemory();
        pool.LeaseFromMemory();
        ConcurrentPooled<int> fresh = pool.Rent(1, static writer => writer.Fill(default!));
        fresh[0] = 19;
        fresh.Dispose();
    }

    [Fact]
    public void SameOwnerCompositeValidationIsFailureAtomic()
    {
        NativeMemoryTestHooks.Reset();
        using NativeConcurrentPool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ConcurrentPooled<int> first = pool.Rent(1, static writer => writer.Fill(default!));
        ConcurrentPooled<int> ended = pool.Rent(1, static writer => writer.Fill(default!));
        ended.Dispose();

        NativeAllocationException? failure = null;
        try
        {
            NativeLeaseOperations.Access(first, ended, static (_, _) => { });
        }
        catch (NativeAllocationException exception)
        {
            failure = exception;
        }

        Assert.NotNull(failure);
        first[0] = 73;
        Assert.Equal(73, first[0]);
        first.Dispose();
        pool.ReturnMemoryToNativeMemory();
    }

    [Fact]
    public void SameOwnerCompositeAdmissionProtectsEveryViewBeforeNotification()
    {
        NativeMemoryTestHooks.Reset();
        using NativeConcurrentPool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ConcurrentPooled<int> first = pool.Rent(1, static writer => writer.Fill(default!));
        ConcurrentPooled<int> second = pool.Rent(1, static writer => writer.Fill(default!));
        NativeOwnerKernel expectedKernel = first.KernelForComposite;
        int notifications = 0;
        NativeAllocationException? transitionFailure = null;
        NativeMemoryTestHooks.SetOperationEnteredWithAllocation((operation, kernel, _, _) =>
        {
            if (operation != nameof(NativeLeaseOperations.Access)
                || !ReferenceEquals(kernel, expectedKernel)
                || Interlocked.Increment(ref notifications) != 1)
            {
                return;
            }

            try
            {
                pool.ReturnMemoryToNativeMemory();
            }
            catch (NativeAllocationException exception)
            {
                transitionFailure = exception;
            }
        });

        try
        {
            NativeLeaseOperations.Access(first, second, (left, right) =>
            {
                left[0] = 81;
                right[0] = 82;
            });
        }
        finally
        {
            NativeMemoryTestHooks.SetOperationEnteredWithAllocation(null);
        }

        Assert.Equal(2, notifications);
        Assert.IsType<NativeAllocationInUseException>(transitionFailure);
        Assert.Equal(81, first[0]);
        Assert.Equal(82, second[0]);
    }

    [Fact]
    public void ReferenceSlotsAndStaleHandlesRemainCorrectAfterTolerantTransition()
    {
        NativeMemoryTestHooks.Reset();
        using NativeConcurrentPool<string> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ConcurrentPooled<string> values = pool.Rent(2, static writer => writer.Fill(default!));
        values[0] = "first";
        NativeLeaseOperations.Access(values, values, (first, second) =>
        {
            second[1] = first[0] + "-second";
        });

        Assert.Equal("first-second", values[1]);
        pool.ReleaseLeasesToGarbageCollector();
        NativeAllocationException? staleFailure = null;
        try
        {
            _ = values.Length;
        }
        catch (NativeAllocationException exception)
        {
            staleFailure = exception;
        }

        Assert.NotNull(staleFailure);
        ConcurrentPooled<string> fresh = pool.Rent(1, static writer => writer.Fill(default!));
        Assert.Null(fresh[0]);
        fresh.Dispose();
    }

    [Fact]
    public void ScopedGroupInitializationPublishesOnlyCompleteHeterogeneousRanges()
    {
        NativeMemoryTestHooks.Reset();
        using NativeConcurrentPool<int> pool = new(
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeConcurrentArena arena = new(
            preAllocateBytes: 4096,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ConcurrentPooled<int> source = pool.Rent(
            4,
            static writer =>
            {
                writer.Write(10);
                writer.Write(20);
                writer.Write(30);
                writer.Write(40);
            });

        {
            scoped ConcurrentArenaLease<int> first;
            scoped ConcurrentArenaLease<long> second;
            scoped ConcurrentArenaLease<byte> third;
            scoped ConcurrentArenaLease<string> fourth;
            NativeLeaseOperations.InitializeScoped(
                source,
                arena,
                2,
                2,
                2,
                2,
                static (input, one, two, three, four) =>
                {
                    one.Write(input[0]);
                    one.Write(input[1]);
                    two.Write(input[1]);
                    two.Write(input[2]);
                    three.Write(checked((byte)input[2]));
                    three.Write(checked((byte)input[3]));
                    four.Write(input[0].ToString());
                    four.Write(input[3].ToString());
                },
                out first,
                out second,
                out third,
                out fourth);

            Assert.Equal(10, first[0]);
            Assert.Equal(20, first[1]);
            Assert.Equal(20, second[0]);
            Assert.Equal(30, second[1]);
            Assert.Equal(30, third[0]);
            Assert.Equal(40, third[1]);
            Assert.Equal("10", fourth[0]);
            Assert.Equal("40", fourth[1]);
        }

        arena.RecycleScoped();
        source.Dispose();
    }

    [Fact]
    public void UnmanagedSpanGroupInitializationPublishesCompleteRanges()
    {
        NativeMemoryTestHooks.Reset();
        using NativeConcurrentArena arena = new(
            preAllocateBytes: 4096,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ConcurrentArenaLease<int> source = arena.Scratch<int>(
            2,
            static writer =>
            {
                writer.Write(12);
                writer.Write(34);
            });
        NativeLeaseSourceQuadSpanInitializer<
            int,
            int,
            long,
            byte,
            uint> initializer =
            static (input, one, two, three, four) =>
            {
                one[0] = input[0];
                one[1] = input[1];
                two[0] = input[1];
                three.Fill(checked((byte)input[0]));
                four[0] = checked((uint)(input[0] + input[1]));
            };

        InitializeAndVerify(source, arena, initializer);
        arena.RecycleScoped();
        Assert.Equal(1, arena.CurrentAllocationRecordCountForTest);
        Assert.Equal(46, source.Read(static values => values[0] + values[1]));
        long reuseBefore =
            NativeMemoryTestHooks.Snapshot().ReclaimedRangeReuseCount;
        InitializeAndVerify(source, arena, initializer);
        NativeMemoryTestMetrics metrics =
            NativeMemoryTestHooks.Snapshot();
        Assert.True(metrics.ReclaimedRangeReuseCount > reuseBefore);
        arena.RecycleScoped();
        Assert.Equal(1, arena.CurrentAllocationRecordCountForTest);

        static void InitializeAndVerify(
            scoped ConcurrentArenaLease<int> source,
            NativeConcurrentArena arena,
            NativeLeaseSourceQuadSpanInitializer<
                int,
                int,
                long,
                byte,
                uint> initializer)
        {
            scoped ConcurrentArenaLease<int> first;
            scoped ConcurrentArenaLease<long> second;
            scoped ConcurrentArenaLease<byte> third;
            scoped ConcurrentArenaLease<uint> fourth;
            NativeLeaseOperations.InitializeScoped(
                source,
                arena,
                2,
                1,
                3,
                1,
                initializer,
                out first,
                out second,
                out third,
                out fourth);

            Assert.Equal(12, first[0]);
            Assert.Equal(34, first[1]);
            Assert.Equal(34, second[0]);
            Assert.Equal(12, third[0]);
            Assert.Equal(12, third[1]);
            Assert.Equal(12, third[2]);
            Assert.Equal(46u, fourth[0]);
        }
    }

    [Fact]
    public void UnmanagedSpanGroupBatchesAnEmptyRange()
    {
        NativeMemoryTestHooks.Reset();
        using NativeConcurrentArena arena = new(
            preAllocateBytes: 4096,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ConcurrentArenaLease<int> source = arena.Scratch<int>(
            1,
            static writer => writer.Write(12));

        InitializeAndVerify(source, arena);
        arena.RecycleScoped();
        long visitsBefore =
            NativeMemoryTestHooks.Snapshot().BumpTraversalVisitCount;
        InitializeAndVerify(source, arena);
        long visitsAfter =
            NativeMemoryTestHooks.Snapshot().BumpTraversalVisitCount;

        Assert.Equal(1L, visitsAfter - visitsBefore);
        arena.RecycleScoped();
        Assert.Equal(1, arena.CurrentAllocationRecordCountForTest);

        bool failed = false;
        try
        {
            scoped ConcurrentArenaLease<int> first;
            scoped ConcurrentArenaLease<long> second;
            scoped ConcurrentArenaLease<uint> third;
            scoped ConcurrentArenaLease<byte> empty;
            NativeLeaseOperations.InitializeScoped(
                source,
                arena,
                1,
                1,
                1,
                0,
                static (input, one, two, three, four) =>
                {
                    one[0] = input[0];
                    two[0] = input[0];
                    three[0] = checked((uint)input[0]);
                    Assert.Equal(0, four.Length);
                    throw new InvalidOperationException(
                        "Expected failure.");
                },
                out first,
                out second,
                out third,
                out empty);
        }
        catch (InvalidOperationException)
        {
            failed = true;
        }

        Assert.True(failed);
        Assert.Equal(1, arena.CurrentAllocationRecordCountForTest);
        InitializeAndVerify(source, arena);
        arena.RecycleScoped();
        Assert.Equal(1, arena.CurrentAllocationRecordCountForTest);

        static void InitializeAndVerify(
            scoped ConcurrentArenaLease<int> source,
            NativeConcurrentArena arena)
        {
            scoped ConcurrentArenaLease<int> first;
            scoped ConcurrentArenaLease<long> second;
            scoped ConcurrentArenaLease<uint> third;
            scoped ConcurrentArenaLease<byte> empty;
            NativeLeaseOperations.InitializeScoped(
                source,
                arena,
                2,
                1,
                1,
                0,
                static (input, one, two, three, four) =>
                {
                    one.Fill(input[0]);
                    two[0] = input[0] + 1;
                    three[0] = checked((uint)(input[0] + 2));
                    Assert.Equal(0, four.Length);
                },
                out first,
                out second,
                out third,
                out empty);

            Assert.Equal(12, first[0]);
            Assert.Equal(12, first[1]);
            Assert.Equal(13, second[0]);
            Assert.Equal(14u, third[0]);
            Assert.Equal(0, empty.Length);
        }
    }

    [Fact]
    public void UnmanagedSpanGroupFailureRestoresAllReservations()
    {
        NativeMemoryTestHooks.Reset();
        using NativeConcurrentArena arena = new(
            preAllocateBytes: 4096,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ConcurrentArenaLease<int> source = arena.Scratch<int>(
            1,
            static writer => writer.Write(9));
        NativeLeaseSourceQuadSpanInitializer<
            int,
            int,
            long,
            byte,
            uint> initializer =
            static (input, one, two, three, four) =>
            {
                one[0] = input[0];
                two[0] = input[0];
                three[0] = checked((byte)input[0]);
                four[0] = checked((uint)input[0]);
                throw new InvalidOperationException("Expected failure.");
            };

        {
            scoped ConcurrentArenaLease<int> first;
            scoped ConcurrentArenaLease<long> second;
            scoped ConcurrentArenaLease<byte> third;
            scoped ConcurrentArenaLease<uint> fourth;
            NativeLeaseOperations.InitializeScoped(
                source,
                arena,
                1,
                1,
                1,
                1,
                static (input, one, two, three, four) =>
                {
                    one[0] = input[0];
                    two[0] = input[0];
                    three[0] = checked((byte)input[0]);
                    four[0] = checked((uint)input[0]);
                },
                out first,
                out second,
                out third,
                out fourth);
        }

        arena.RecycleScoped();
        Assert.Equal(1, arena.CurrentAllocationRecordCountForTest);
        bool failed = false;
        try
        {
            scoped ConcurrentArenaLease<int> first;
            scoped ConcurrentArenaLease<long> second;
            scoped ConcurrentArenaLease<byte> third;
            scoped ConcurrentArenaLease<uint> fourth;
            NativeLeaseOperations.InitializeScoped(
                source,
                arena,
                1,
                1,
                1,
                1,
                initializer,
                out first,
                out second,
                out third,
                out fourth);
        }
        catch (InvalidOperationException)
        {
            failed = true;
        }

        Assert.True(failed);
        Assert.Equal(1, arena.CurrentAllocationRecordCountForTest);
        ConcurrentArenaLease<int> next = arena.Scratch<int>(
            1,
            static writer => writer.Write(15));
        Assert.Equal(15, next[0]);
        Assert.Equal(9, source[0]);
    }

    [Fact]
    public void SameOwnerSpanGroupRejectsARecycledSourceBeforeReservation()
    {
        NativeMemoryTestHooks.Reset();
        using NativeConcurrentArena arena = new(
            preAllocateBytes: 4096,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ConcurrentArenaLease<int> source = arena.ScratchScoped<int>(
            1,
            static writer => writer.Write(9));
        arena.RecycleScoped();
        NativeAllocationException? failure = null;
        try
        {
            scoped ConcurrentArenaLease<int> first;
            scoped ConcurrentArenaLease<long> second;
            scoped ConcurrentArenaLease<byte> third;
            scoped ConcurrentArenaLease<uint> fourth;
            NativeLeaseOperations.InitializeScoped(
                source,
                arena,
                1,
                1,
                1,
                1,
                static (input, one, two, three, four) =>
                {
                    one[0] = input[0];
                    two[0] = input[0];
                    three[0] = checked((byte)input[0]);
                    four[0] = checked((uint)input[0]);
                },
                out first,
                out second,
                out third,
                out fourth);
        }
        catch (NativeAllocationException exception)
        {
            failure = exception;
        }

        Assert.NotNull(failure);
        Assert.Equal(0, arena.CurrentAllocationRecordCountForTest);
    }

    [Fact]
    public void UnmanagedSpanOctetPublishesAndReusesAllRanges()
    {
        NativeMemoryTestHooks.Reset();
        using NativeConcurrentArena arena = new(
            preAllocateBytes: 4096,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ConcurrentArenaLease<int> source = arena.Scratch<int>(
            2,
            static writer =>
            {
                writer.Write(5);
                writer.Write(8);
            });
        NativeLeaseSourceOctupleSpanInitializer<
            int,
            int,
            long,
            byte,
            uint,
            short,
            ushort,
            float,
            double> initializer =
            static (input, one, two, three, four, five, six, seven, eight) =>
            {
                one[0] = input[0];
                one[1] = input[1];
                two[0] = input[0] + input[1];
                three.Fill(checked((byte)input[1]));
                four[0] = checked((uint)(input[0] * input[1]));
                five[0] = checked((short)input[0]);
                five[1] = checked((short)input[1]);
                six[0] = checked((ushort)(input[0] + input[1]));
                seven[0] = input[0] / 2f;
                eight[0] = input[0] / 2d;
                eight[1] = input[1] / 2d;
            };

        InitializeAndVerify(source, arena, initializer);
        arena.RecycleScoped();
        long reuseBefore =
            NativeMemoryTestHooks.Snapshot().ReclaimedRangeReuseCount;
        InitializeAndVerify(source, arena, initializer);
        NativeMemoryTestMetrics metrics =
            NativeMemoryTestHooks.Snapshot();
        Assert.True(metrics.ReclaimedRangeReuseCount > reuseBefore);
        arena.RecycleScoped();
        Assert.Equal(1, arena.CurrentAllocationRecordCountForTest);

        static void InitializeAndVerify(
            scoped ConcurrentArenaLease<int> source,
            NativeConcurrentArena arena,
            NativeLeaseSourceOctupleSpanInitializer<
                int,
                int,
                long,
                byte,
                uint,
                short,
                ushort,
                float,
                double> initializer)
        {
            scoped ConcurrentArenaLease<int> first;
            scoped ConcurrentArenaLease<long> second;
            scoped ConcurrentArenaLease<byte> third;
            scoped ConcurrentArenaLease<uint> fourth;
            scoped ConcurrentArenaLease<short> fifth;
            scoped ConcurrentArenaLease<ushort> sixth;
            scoped ConcurrentArenaLease<float> seventh;
            scoped ConcurrentArenaLease<double> eighth;
            NativeLeaseOperations.InitializeScoped(
                source,
                arena,
                2,
                1,
                3,
                1,
                2,
                1,
                1,
                2,
                initializer,
                out first,
                out second,
                out third,
                out fourth,
                out fifth,
                out sixth,
                out seventh,
                out eighth);

            Assert.Equal(5, first[0]);
            Assert.Equal(8, first[1]);
            Assert.Equal(13, second[0]);
            Assert.Equal(8, third[0]);
            Assert.Equal(8, third[1]);
            Assert.Equal(8, third[2]);
            Assert.Equal(40u, fourth[0]);
            Assert.Equal(5, fifth[0]);
            Assert.Equal(8, fifth[1]);
            Assert.Equal(13, sixth[0]);
            Assert.Equal(2.5f, seventh[0]);
            Assert.Equal(2.5d, eighth[0]);
            Assert.Equal(4d, eighth[1]);
        }
    }

    [Fact]
    public void UnmanagedSpanOctetFailureRestoresAllReservations()
    {
        NativeMemoryTestHooks.Reset();
        using NativeConcurrentArena arena = new(
            preAllocateBytes: 4096,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ConcurrentArenaLease<int> source = arena.Scratch<int>(
            1,
            static writer => writer.Write(21));

        bool failed = false;
        try
        {
            scoped ConcurrentArenaLease<int> first;
            scoped ConcurrentArenaLease<long> second;
            scoped ConcurrentArenaLease<byte> third;
            scoped ConcurrentArenaLease<uint> fourth;
            scoped ConcurrentArenaLease<short> fifth;
            scoped ConcurrentArenaLease<ushort> sixth;
            scoped ConcurrentArenaLease<float> seventh;
            scoped ConcurrentArenaLease<double> eighth;
            NativeLeaseOperations.InitializeScoped(
                source,
                arena,
                1,
                1,
                1,
                1,
                1,
                1,
                1,
                1,
                static (input, one, two, three, four, five, six, seven, eight) =>
                {
                    one[0] = input[0];
                    two[0] = input[0];
                    three[0] = checked((byte)input[0]);
                    four[0] = checked((uint)input[0]);
                    five[0] = checked((short)input[0]);
                    six[0] = checked((ushort)input[0]);
                    seven[0] = input[0];
                    eight[0] = input[0];
                    throw new InvalidOperationException(
                        "Expected failure.");
                },
                out first,
                out second,
                out third,
                out fourth,
                out fifth,
                out sixth,
                out seventh,
                out eighth);
        }
        catch (InvalidOperationException)
        {
            failed = true;
        }

        Assert.True(failed);
        Assert.Equal(1, arena.CurrentAllocationRecordCountForTest);
        Assert.Equal(21, source[0]);
        ConcurrentArenaLease<int> next = arena.Scratch<int>(
            1,
            static writer => writer.Write(34));
        Assert.Equal(34, next[0]);
    }

    [Fact]
    public void ScopedGroupInitializationFailureRestoresAllReservations()
    {
        NativeMemoryTestHooks.Reset();
        using NativeConcurrentPool<int> pool = new(
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeConcurrentArena arena = new(
            preAllocateBytes: 4096,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ConcurrentPooled<int> source = pool.Rent(
            1,
            static writer => writer.Write(7));
        bool failed = false;
        try
        {
            scoped ConcurrentArenaLease<int> first;
            scoped ConcurrentArenaLease<int> second;
            scoped ConcurrentArenaLease<int> third;
            scoped ConcurrentArenaLease<string> fourth;
            NativeLeaseOperations.InitializeScoped(
                source,
                arena,
                1,
                1,
                1,
                2,
                static (input, one, two, three, four) =>
                {
                    one.Write(input[0]);
                    one.WriteAt(
                        0,
                        one.ReadInitialized(0) + 10);
                    two.Write(input[0] + 1);
                    three.Write(input[0] + 2);
                    four.Write("partial");
                    four.WriteAt(
                        0,
                        four.ReadInitialized(0) + "-replaced");
                },
                out first,
                out second,
                out third,
                out fourth);
        }
        catch (InvalidOperationException)
        {
            failed = true;
        }

        Assert.True(failed);
        Assert.Equal(0, arena.CurrentAllocationRecordCountForTest);
        Assert.Equal(0, arena.CurrentReferenceRootCountForTest);
        arena.RecycleScoped();

        {
            scoped ConcurrentArenaLease<int> first;
            scoped ConcurrentArenaLease<int> second;
            scoped ConcurrentArenaLease<int> third;
            scoped ConcurrentArenaLease<string> fourth;
            NativeLeaseOperations.InitializeScoped(
                source,
                arena,
                1,
                1,
                1,
                2,
                static (input, one, two, three, four) =>
                {
                    one.Write(input[0]);
                    two.Write(input[0] + 1);
                    three.Write(input[0] + 2);
                    four.Write("complete");
                    four.Write("published");
                },
                out first,
                out second,
                out third,
                out fourth);
            Assert.Equal(7, first[0]);
            Assert.Equal(8, second[0]);
            Assert.Equal(9, third[0]);
            Assert.Equal("complete", fourth[0]);
            Assert.Equal("published", fourth[1]);
        }

        arena.RecycleScoped();
        source.Dispose();
    }

    [Fact]
    public void SameOwnerArenaCompositesUseOneFailureAtomicAdmission()
    {
        NativeMemoryTestHooks.Reset();
        using NativeConcurrentArena arena = new(
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ConcurrentArenaLease<int> first = arena.Scratch<int>(
            1,
            static writer => writer.Write(1));
        ConcurrentArenaLease<int> second = arena.Scratch<int>(
            1,
            static writer => writer.Write(2));
        ConcurrentArenaLease<long> third = arena.Scratch<long>(
            1,
            static writer => writer.Write(3));
        ConcurrentArenaLease<short> fourth = arena.Scratch<short>(
            1,
            static writer => writer.Write(4));
        ConcurrentArenaLease<byte> fifth = arena.Scratch<byte>(
            1,
            static writer => writer.Write(5));
        int admissions = 0;
        NativeMemoryTestHooks.SetBeforeOperationEntry(operation =>
        {
            if (operation == nameof(NativeLeaseOperations.Access))
            {
                admissions++;
            }
        });

        try
        {
            NativeLeaseOperations.Access(
                first,
                second,
                static (left, right) => left[0] += right[0]);
            NativeLeaseOperations.Access(
                first,
                second,
                third,
                static (one, two, three) => three[0] += one[0] + two[0]);
            NativeLeaseOperations.Access(
                first,
                second,
                third,
                fourth,
                static (_, _, _, four) => four[0]++);
            NativeLeaseOperations.Access(
                first,
                second,
                third,
                fourth,
                fifth,
                static (one, two, three, four, five) =>
                {
                    one[0] += two[0];
                    three[0] += four[0] + five[0];
                });
        }
        finally
        {
            NativeMemoryTestHooks.SetBeforeOperationEntry(null);
        }

        Assert.Equal(4, admissions);
        Assert.Equal(5, first[0]);
        Assert.Equal(18, third[0]);
        Assert.Equal(5, fourth[0]);

        bool callbackFailed = false;
        try
        {
            NativeLeaseOperations.Access(
                first,
                second,
                third,
                fourth,
                fifth,
                static (_, _, _, _, _) =>
                    throw new InvalidOperationException());
        }
        catch (InvalidOperationException)
        {
            callbackFailed = true;
        }

        Assert.True(callbackFailed);
        first[0] = 21;
        Assert.Equal(21, first[0]);
    }

    [Fact]
    public void SevenArenaViewsUseOneFailureAtomicAdmission()
    {
        NativeMemoryTestHooks.Reset();
        using NativeConcurrentArena arena = new(
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ConcurrentArenaLease<int> first = arena.Scratch<int>(
            1,
            static writer => writer.Write(1));
        ConcurrentArenaLease<int> second = arena.Scratch<int>(
            1,
            static writer => writer.Write(2));
        ConcurrentArenaLease<int> third = arena.Scratch<int>(
            1,
            static writer => writer.Write(3));
        ConcurrentArenaLease<int> fourth = arena.Scratch<int>(
            1,
            static writer => writer.Write(4));
        ConcurrentArenaLease<int> fifth = arena.Scratch<int>(
            1,
            static writer => writer.Write(5));
        ConcurrentArenaLease<int> sixth = arena.Scratch<int>(
            1,
            static writer => writer.Write(6));
        ConcurrentArenaLease<int> seventh = arena.Scratch<int>(
            1,
            static writer => writer.Write(7));
        int admissions = 0;
        NativeMemoryTestHooks.SetBeforeOperationEntry(operation =>
        {
            if (operation == nameof(NativeLeaseOperations.Access))
            {
                admissions++;
            }
        });

        try
        {
            NativeLeaseOperations.Access(
                first,
                second,
                third,
                fourth,
                fifth,
                sixth,
                seventh,
                static (one, two, three, four, five, six, seven) =>
                {
                    one[0] = two[0]
                        + three[0]
                        + four[0]
                        + five[0]
                        + six[0]
                        + seven[0];
                });
        }
        finally
        {
            NativeMemoryTestHooks.SetBeforeOperationEntry(null);
        }

        Assert.Equal(1, admissions);
        Assert.Equal(27, first[0]);

        bool failed = false;
        try
        {
            NativeLeaseOperations.Access(
                first,
                second,
                third,
                fourth,
                fifth,
                sixth,
                seventh,
                static (_, _, _, _, _, _, _) =>
                    throw new InvalidOperationException());
        }
        catch (InvalidOperationException)
        {
            failed = true;
        }

        Assert.True(failed);
        first[0] = 34;
        Assert.Equal(34, first[0]);
    }

    [Fact]
    public void EightArenaViewsUseOneFailureAtomicAdmission()
    {
        NativeMemoryTestHooks.Reset();
        using NativeConcurrentArena arena = new(
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ConcurrentArenaLease<int> first = arena.Scratch<int>(
            1,
            static writer => writer.Write(1));
        ConcurrentArenaLease<int> second = arena.Scratch<int>(
            1,
            static writer => writer.Write(2));
        ConcurrentArenaLease<int> third = arena.Scratch<int>(
            1,
            static writer => writer.Write(3));
        ConcurrentArenaLease<int> fourth = arena.Scratch<int>(
            1,
            static writer => writer.Write(4));
        ConcurrentArenaLease<int> fifth = arena.Scratch<int>(
            1,
            static writer => writer.Write(5));
        ConcurrentArenaLease<int> sixth = arena.Scratch<int>(
            1,
            static writer => writer.Write(6));
        ConcurrentArenaLease<int> seventh = arena.Scratch<int>(
            1,
            static writer => writer.Write(7));
        ConcurrentArenaLease<int> eighth = arena.Scratch<int>(
            1,
            static writer => writer.Write(8));
        int admissions = 0;
        NativeMemoryTestHooks.SetBeforeOperationEntry(operation =>
        {
            if (operation == nameof(NativeLeaseOperations.Access))
            {
                admissions++;
            }
        });

        try
        {
            NativeLeaseOperations.Access(
                first,
                second,
                third,
                fourth,
                fifth,
                sixth,
                seventh,
                eighth,
                static (one, two, three, four, five, six, seven, eight) =>
                {
                    one[0] = two[0]
                        + three[0]
                        + four[0]
                        + five[0]
                        + six[0]
                        + seven[0]
                        + eight[0];
                });
        }
        finally
        {
            NativeMemoryTestHooks.SetBeforeOperationEntry(null);
        }

        Assert.Equal(1, admissions);
        Assert.Equal(35, first[0]);

        bool failed = false;
        try
        {
            NativeLeaseOperations.Access(
                first,
                second,
                third,
                fourth,
                fifth,
                sixth,
                seventh,
                eighth,
                static (_, _, _, _, _, _, _, _) =>
                    throw new InvalidOperationException());
        }
        catch (InvalidOperationException)
        {
            failed = true;
        }

        Assert.True(failed);
        first[0] = 43;
        Assert.Equal(43, first[0]);
    }

    [Fact]
    public void ArenaSourceGroupInitializationKeepsTheSourceAndRollsBackTheTail()
    {
        NativeMemoryTestHooks.Reset();
        using NativeConcurrentArena arena = new(
            preAllocateBytes: 4096,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ConcurrentArenaLease<int> source = arena.Scratch<int>(
            2,
            static writer =>
            {
                writer.Write(7);
                writer.Write(11);
            });

        bool initializationFailed = false;
        try
        {
            scoped ConcurrentArenaLease<int> first;
            scoped ConcurrentArenaLease<long> second;
            scoped ConcurrentArenaLease<byte> third;
            scoped ConcurrentArenaLease<string> fourth;
            NativeLeaseOperations.InitializeScoped(
                source,
                arena,
                1,
                1,
                1,
                2,
                static (input, one, two, three, four) =>
                {
                    one.Write(input[0]);
                    two.Write(input[1]);
                    three.Write(checked((byte)input[0]));
                    four.Write("partial");
                },
                out first,
                out second,
                out third,
                out fourth);
        }
        catch (InvalidOperationException)
        {
            initializationFailed = true;
        }

        Assert.True(initializationFailed);
        Assert.Equal(1, arena.CurrentAllocationRecordCountForTest);
        Assert.Equal(0, arena.CurrentReferenceRootCountForTest);
        Assert.Equal(18, source.Read(static values => values[0] + values[1]));

        {
            scoped ConcurrentArenaLease<int> first;
            scoped ConcurrentArenaLease<long> second;
            scoped ConcurrentArenaLease<byte> third;
            scoped ConcurrentArenaLease<string> fourth;
            NativeLeaseOperations.InitializeScoped(
                source,
                arena,
                1,
                1,
                1,
                1,
                static (input, one, two, three, four) =>
                {
                    one.Write(input[0]);
                    two.Write(input[1]);
                    three.Write(checked((byte)input[0]));
                    four.Write("complete");
                },
                out first,
                out second,
                out third,
                out fourth);

            Assert.Equal(7, first[0]);
            Assert.Equal(11, second[0]);
            Assert.Equal(7, third[0]);
            Assert.Equal("complete", fourth[0]);
        }

        arena.RecycleScoped();
        Assert.Equal(1, arena.CurrentAllocationRecordCountForTest);
        Assert.Equal(18, source.Read(static values => values[0] + values[1]));
    }
}
