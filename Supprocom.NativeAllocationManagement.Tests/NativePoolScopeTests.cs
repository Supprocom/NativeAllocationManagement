namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativePoolScopeTests
{
    private static long Outstanding() => NativeMemoryDiagnostics.Snapshot().OutstandingNativeBytes;

    [Fact]
    public void RentFillsAndRentUninitializedGivesTheLength()
    {
        bool ok = NativePoolScope.Run<int, int, bool>(0, static (scoped ref NativeScopePool<int> pool, int unused) =>
        {
            Pooled<int> old = pool.RentUninitialized(100);
            old.AsSpan().Fill(9);
            old.Dispose();
            using Pooled<int> filled = pool.Rent(100, 3);
            using Pooled<int> raw = pool.RentUninitialized(7);
            return filled.AsSpan().IndexOfAnyExcept(3) < 0 && filled.Length == 100 && raw.AsSpan().Length == 7;
        });
        Assert.True(ok);
    }

    [Fact]
    public void NegativeLengthThrows()
    {
        int seen = NativePoolScope.Run<int, int, int>(0, static (scoped ref NativeScopePool<int> pool, int unused) =>
        {
            int bits = 0;
            try { _ = pool.Rent(-1, 0); } catch (ArgumentOutOfRangeException) { bits |= 1; }
            try { _ = pool.RentUninitialized(-1); } catch (ArgumentOutOfRangeException) { bits |= 2; }
            return bits;
        });
        Assert.Equal(3, seen);
    }

    [Fact]
    public void DoubleReturnThrows()
    {
        bool threw = NativePoolScope.Run<int, int, bool>(0, static (scoped ref NativeScopePool<int> pool, int unused) =>
        {
            Pooled<int> lease = pool.Rent(8, 0);
            lease.Dispose();
            try { lease.Dispose(); return false; }
            catch (NativeAllocationReturnedException) { return true; }
        });
        Assert.True(threw);
    }

    [Fact]
    public void StaleCopyReturnedAfterTheSlabWasRentedAgainThrows()
    {
        bool ok = NativePoolScope.Run<int, int, bool>(0, static (scoped ref NativeScopePool<int> pool, int unused) =>
        {
            Pooled<int> first = pool.Rent(8, 1);
            Pooled<int> copy = first;
            first.Dispose();
            Pooled<int> second = pool.Rent(8, 5);
            try { copy.Dispose(); return false; }
            catch (NativeAllocationReturnedException)
            {
                bool intact = second.AsSpan()[0] == 5;
                second.Dispose();
                return intact;
            }
        });
        Assert.True(ok);
    }

    [Fact]
    public void SpanAfterReturnThrows()
    {
        bool threw = NativePoolScope.Run<int, int, bool>(0, static (scoped ref NativeScopePool<int> pool, int unused) =>
        {
            Pooled<int> lease = pool.Rent(8, 0);
            lease.Dispose();
            try { _ = lease.AsSpan(); return false; }
            catch (NativeAllocationReturnedException) { return true; }
        });
        Assert.True(threw);
    }

    [Fact]
    public void ScopeEndFreesLeasesNeverReturned()
    {
        long baseline = Outstanding();
        long during = NativePoolScope.Run<int, long, long>(0, static (scoped ref NativeScopePool<int> pool, long unused) =>
        {
            _ = pool.Rent(10, 0);
            _ = pool.Rent(10, 0);
            _ = pool.Rent(100000, 0);
            return Outstanding();
        });
        Assert.True(during > baseline);
        Assert.Equal(baseline, Outstanding());
    }

    [Fact]
    public void EveryBlockIsFreedWhenTheBodyThrows()
    {
        long baseline = Outstanding();
        Assert.Throws<InvalidOperationException>(() => NativePoolScope.Run<int, int, int>(0, static (scoped ref NativeScopePool<int> pool, int unused) =>
        {
            Pooled<int> kept = pool.Rent(10, 0);
            Pooled<int> returned = pool.Rent(1000, 0);
            returned.Dispose();
            using Pooled<int> other = pool.Rent(5, 0);
            _ = kept.Length;
            throw new InvalidOperationException();
        }));
        Assert.Equal(baseline, Outstanding());
    }

    [Fact]
    public void AccountingGrowsWithRentsDuringAScopeAndReturnsAfter()
    {
        long baseline = Outstanding();
        long[] seen = NativePoolScope.Run<int, long, long[]>(baseline, static (scoped ref NativeScopePool<int> pool, long start) =>
        {
            long[] deltas = new long[3];
            Pooled<int> a = pool.Rent(1000, 0);
            deltas[0] = Outstanding() - start;
            a.Dispose();
            using Pooled<int> b = pool.Rent(1000, 0);   // reuses the returned slab
            deltas[1] = Outstanding() - start;
            using Pooled<int> c = pool.Rent(1000, 0);   // needs a second slab
            deltas[2] = Outstanding() - start;
            return deltas;
        });
        Assert.True(seen[0] >= 4000);
        Assert.Equal(seen[0], seen[1]);
        Assert.True(seen[2] >= seen[1] + 4000);
        Assert.Equal(baseline, Outstanding());
    }

    [Fact]
    public void NestedScopesAreIndependent()
    {
        long baseline = Outstanding();
        int result = NativePoolScope.Run<int, int, int>(0, static (scoped ref NativeScopePool<int> outer, int unused) =>
        {
            using Pooled<int> a = outer.Rent(8, 1);
            long afterOuter = Outstanding();
            int inner = NativePoolScope.Run<long, int, int>(0, static (scoped ref NativeScopePool<long> pool, int unused) =>
            {
                using Pooled<long> b = pool.Rent(8, 2);
                return (int)b.AsSpan()[3];
            });
            bool freed = Outstanding() == afterOuter;
            bool intact = a.AsSpan()[7] == 1;
            return inner * 100 + (freed ? 10 : 0) + (intact ? 1 : 0);
        });
        Assert.Equal(211, result);
        Assert.Equal(baseline, Outstanding());
    }

    [Fact]
    public void ManyRentsAcrossClassesKeepTheirOwnData()
    {
        long baseline = Outstanding();
        bool ok = NativePoolScope.Run<int, int, bool>(0, static (scoped ref NativeScopePool<int> pool, int unused) =>
        {
            bool ok = true;
            for (int round = 0; round < 3; round++)
            {
                for (int i = 0; i < 400; i++)
                {
                    int la = (i * 37 + 1) % 5000, lb = (i * 91 + 3) % 5000;
                    using Pooled<int> a = pool.Rent(la, i);
                    using Pooled<int> b = pool.Rent(lb, -i);
                    ok &= a.Length == la && a.AsSpan().IndexOfAnyExcept(i) < 0 && b.AsSpan().IndexOfAnyExcept(-i) < 0;
                }
            }

            return ok;
        });
        Assert.True(ok);
        Assert.Equal(baseline, Outstanding());
    }

    [Fact]
    public void VoidOverloadRunsAndFrees()
    {
        long baseline = Outstanding();
        int[] box = new int[1];
        NativePoolScope.Run<int, int[]>(box, static (scoped ref NativeScopePool<int> pool, int[] state) =>
        {
            using Pooled<int> lease = pool.Rent(4, 3);
            state[0] = lease.AsSpan()[2];
        });
        Assert.Equal(3, box[0]);
        Assert.Equal(baseline, Outstanding());
        Assert.Throws<ArgumentNullException>(() => NativePoolScope.Run<int, int>(0, (NativePoolScopeAction<int, int>)null!));
        Assert.Throws<ArgumentNullException>(() => NativePoolScope.Run<int, int, int>(0, null!));
    }
}
