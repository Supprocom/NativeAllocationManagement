using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeLeaseOperationsTests
{
    [Fact]
    public void EveryCompositeOverloadUsesDirectBoundedStorage()
    {
        NativeMemoryTestHooks.Reset();
        using NativePool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativePool<int> secondPool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);

        Pooled<int> first = pool.Rent(2, static writer => writer.Fill(default!));
        Pooled<int> second = pool.Rent(2, static writer => writer.Fill(default!));
        Pooled<int> third = secondPool.Rent(2, static writer => writer.Fill(default!));
        ArenaLease<int> arenaFirst = arena.Scratch<int>(2, static writer => writer.Fill(default!));
        ArenaLease<int> arenaSecond = arena.Scratch<int>(2, static writer => writer.Fill(default!));
        ArenaLease<long> arenaThird = arena.Scratch<long>(2, static writer => writer.Fill(default!));
        ArenaLease<byte> arenaFourth = arena.Scratch<byte>(2, static writer => writer.Fill(default!));

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
        using NativePool<int> goodPool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativePool<int> stalePool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Pooled<int> good = goodPool.Rent(1, static writer => writer.Fill(default!));
        Pooled<int> stale = stalePool.Rent(1, static writer => writer.Fill(default!));
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
        using NativePool<int> tripleFirstPool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativePool<int> tripleSecondPool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Pooled<int> tripleFirst = tripleFirstPool.Rent(1, static writer => writer.Fill(default!));
        Pooled<int> tripleStale = tripleSecondPool.Rent(1, static writer => writer.Fill(default!));
        Pooled<int> tripleThird = tripleSecondPool.Rent(1, static writer => writer.Fill(default!));
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

        using NativePool<int> pooledFirstPool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeArena staleArena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Pooled<int> pooledFirst = pooledFirstPool.Rent(1, static writer => writer.Fill(default!));
        ArenaLease<int> staleArenaLease = staleArena.Scratch<int>(1, static writer => writer.Fill(default!));
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

        using NativePool<int> quintuplePool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeArena goodArena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeArena lateArena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Pooled<int> first = quintuplePool.Rent(1, static writer => writer.Fill(default!));
        Pooled<int> second = quintuplePool.Rent(1, static writer => writer.Fill(default!));
        Pooled<int> third = quintuplePool.Rent(1, static writer => writer.Fill(default!));
        ArenaLease<int> fourth = goodArena.Scratch<int>(1, static writer => writer.Fill(default!));
        ArenaLease<int> fifth = lateArena.Scratch<int>(1, static writer => writer.Fill(default!));
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
        using NativePool<int> firstPool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativePool<int> secondPool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Pooled<int> first = firstPool.Rent(1, static writer => writer.Fill(default!));
        Pooled<int> second = firstPool.Rent(1, static writer => writer.Fill(default!));
        Pooled<int> third = secondPool.Rent(1, static writer => writer.Fill(default!));
        ArenaLease<int> fourth = arena.Scratch<int>(1, static writer => writer.Fill(default!));
        ArenaLease<int> fifth = arena.Scratch<int>(1, static writer => writer.Fill(default!));
        ArenaLease<long> sixth = arena.Scratch<long>(1, static writer => writer.Fill(default!));
        ArenaLease<byte> seventh = arena.Scratch<byte>(1, static writer => writer.Fill(default!));

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
        using NativePool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Pooled<int> lease = pool.Rent(1, static writer => writer.Fill(default!));

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
        Pooled<int> fresh = pool.Rent(1, static writer => writer.Fill(default!));
        fresh[0] = 19;
        fresh.Dispose();
    }

    [Fact]
    public void SameOwnerCompositeValidationIsFailureAtomic()
    {
        NativeMemoryTestHooks.Reset();
        using NativePool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Pooled<int> first = pool.Rent(1, static writer => writer.Fill(default!));
        Pooled<int> ended = pool.Rent(1, static writer => writer.Fill(default!));
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
        using NativePool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Pooled<int> first = pool.Rent(1, static writer => writer.Fill(default!));
        Pooled<int> second = pool.Rent(1, static writer => writer.Fill(default!));
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
        using NativePool<string> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Pooled<string> values = pool.Rent(2, static writer => writer.Fill(default!));
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
        Pooled<string> fresh = pool.Rent(1, static writer => writer.Fill(default!));
        Assert.Null(fresh[0]);
        fresh.Dispose();
    }

    [Fact]
    public void ScopedGroupInitializationPublishesOnlyCompleteHeterogeneousRanges()
    {
        NativeMemoryTestHooks.Reset();
        using NativePool<int> pool = new(
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeArena arena = new(
            preAllocateBytes: 4096,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Pooled<int> source = pool.Rent(
            4,
            static writer =>
            {
                writer.Write(10);
                writer.Write(20);
                writer.Write(30);
                writer.Write(40);
            });

        {
            scoped ArenaLease<int> first;
            scoped ArenaLease<long> second;
            scoped ArenaLease<byte> third;
            scoped ArenaLease<string> fourth;
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
        using NativeArena arena = new(
            preAllocateBytes: 4096,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ArenaLease<int> source = arena.Scratch<int>(
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
            scoped ArenaLease<int> source,
            NativeArena arena,
            NativeLeaseSourceQuadSpanInitializer<
                int,
                int,
                long,
                byte,
                uint> initializer)
        {
            scoped ArenaLease<int> first;
            scoped ArenaLease<long> second;
            scoped ArenaLease<byte> third;
            scoped ArenaLease<uint> fourth;
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
    public void UnmanagedSpanGroupFailureRestoresAllReservations()
    {
        NativeMemoryTestHooks.Reset();
        using NativeArena arena = new(
            preAllocateBytes: 4096,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ArenaLease<int> source = arena.Scratch<int>(
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
            scoped ArenaLease<int> first;
            scoped ArenaLease<long> second;
            scoped ArenaLease<byte> third;
            scoped ArenaLease<uint> fourth;
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
            scoped ArenaLease<int> first;
            scoped ArenaLease<long> second;
            scoped ArenaLease<byte> third;
            scoped ArenaLease<uint> fourth;
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
        ArenaLease<int> next = arena.Scratch<int>(
            1,
            static writer => writer.Write(15));
        Assert.Equal(15, next[0]);
        Assert.Equal(9, source[0]);
    }

    [Fact]
    public void SameOwnerSpanGroupRejectsARecycledSourceBeforeReservation()
    {
        NativeMemoryTestHooks.Reset();
        using NativeArena arena = new(
            preAllocateBytes: 4096,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ArenaLease<int> source = arena.ScratchScoped<int>(
            1,
            static writer => writer.Write(9));
        arena.RecycleScoped();
        NativeAllocationException? failure = null;
        try
        {
            scoped ArenaLease<int> first;
            scoped ArenaLease<long> second;
            scoped ArenaLease<byte> third;
            scoped ArenaLease<uint> fourth;
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
        using NativeArena arena = new(
            preAllocateBytes: 4096,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ArenaLease<int> source = arena.Scratch<int>(
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
            scoped ArenaLease<int> source,
            NativeArena arena,
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
            scoped ArenaLease<int> first;
            scoped ArenaLease<long> second;
            scoped ArenaLease<byte> third;
            scoped ArenaLease<uint> fourth;
            scoped ArenaLease<short> fifth;
            scoped ArenaLease<ushort> sixth;
            scoped ArenaLease<float> seventh;
            scoped ArenaLease<double> eighth;
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
        using NativeArena arena = new(
            preAllocateBytes: 4096,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ArenaLease<int> source = arena.Scratch<int>(
            1,
            static writer => writer.Write(21));

        bool failed = false;
        try
        {
            scoped ArenaLease<int> first;
            scoped ArenaLease<long> second;
            scoped ArenaLease<byte> third;
            scoped ArenaLease<uint> fourth;
            scoped ArenaLease<short> fifth;
            scoped ArenaLease<ushort> sixth;
            scoped ArenaLease<float> seventh;
            scoped ArenaLease<double> eighth;
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
        ArenaLease<int> next = arena.Scratch<int>(
            1,
            static writer => writer.Write(34));
        Assert.Equal(34, next[0]);
    }

    [Fact]
    public void ScopedGroupInitializationFailureRestoresAllReservations()
    {
        NativeMemoryTestHooks.Reset();
        using NativePool<int> pool = new(
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeArena arena = new(
            preAllocateBytes: 4096,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Pooled<int> source = pool.Rent(
            1,
            static writer => writer.Write(7));
        bool failed = false;
        try
        {
            scoped ArenaLease<int> first;
            scoped ArenaLease<int> second;
            scoped ArenaLease<int> third;
            scoped ArenaLease<string> fourth;
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
            scoped ArenaLease<int> first;
            scoped ArenaLease<int> second;
            scoped ArenaLease<int> third;
            scoped ArenaLease<string> fourth;
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
        using NativeArena arena = new(
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ArenaLease<int> first = arena.Scratch<int>(
            1,
            static writer => writer.Write(1));
        ArenaLease<int> second = arena.Scratch<int>(
            1,
            static writer => writer.Write(2));
        ArenaLease<long> third = arena.Scratch<long>(
            1,
            static writer => writer.Write(3));
        ArenaLease<short> fourth = arena.Scratch<short>(
            1,
            static writer => writer.Write(4));
        ArenaLease<byte> fifth = arena.Scratch<byte>(
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

        Assert.Equal(3, admissions);
        Assert.Equal(5, first[0]);
        Assert.Equal(17, third[0]);

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
        using NativeArena arena = new(
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ArenaLease<int> first = arena.Scratch<int>(
            1,
            static writer => writer.Write(1));
        ArenaLease<int> second = arena.Scratch<int>(
            1,
            static writer => writer.Write(2));
        ArenaLease<int> third = arena.Scratch<int>(
            1,
            static writer => writer.Write(3));
        ArenaLease<int> fourth = arena.Scratch<int>(
            1,
            static writer => writer.Write(4));
        ArenaLease<int> fifth = arena.Scratch<int>(
            1,
            static writer => writer.Write(5));
        ArenaLease<int> sixth = arena.Scratch<int>(
            1,
            static writer => writer.Write(6));
        ArenaLease<int> seventh = arena.Scratch<int>(
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
        using NativeArena arena = new(
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ArenaLease<int> first = arena.Scratch<int>(
            1,
            static writer => writer.Write(1));
        ArenaLease<int> second = arena.Scratch<int>(
            1,
            static writer => writer.Write(2));
        ArenaLease<int> third = arena.Scratch<int>(
            1,
            static writer => writer.Write(3));
        ArenaLease<int> fourth = arena.Scratch<int>(
            1,
            static writer => writer.Write(4));
        ArenaLease<int> fifth = arena.Scratch<int>(
            1,
            static writer => writer.Write(5));
        ArenaLease<int> sixth = arena.Scratch<int>(
            1,
            static writer => writer.Write(6));
        ArenaLease<int> seventh = arena.Scratch<int>(
            1,
            static writer => writer.Write(7));
        ArenaLease<int> eighth = arena.Scratch<int>(
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
        using NativeArena arena = new(
            preAllocateBytes: 4096,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ArenaLease<int> source = arena.Scratch<int>(
            2,
            static writer =>
            {
                writer.Write(7);
                writer.Write(11);
            });

        bool initializationFailed = false;
        try
        {
            scoped ArenaLease<int> first;
            scoped ArenaLease<long> second;
            scoped ArenaLease<byte> third;
            scoped ArenaLease<string> fourth;
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
            scoped ArenaLease<int> first;
            scoped ArenaLease<long> second;
            scoped ArenaLease<byte> third;
            scoped ArenaLease<string> fourth;
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
