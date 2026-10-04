namespace Supprocom.NativeAllocationManagement;

public static partial class NativeLeaseOperations
{
    /// <summary>Tries to publish 4 fully initialized scoped ranges without prepared backing growth.</summary>
    /// <returns>False only for capacity exhaustion, before invoking the initializer; invalid authority and initializer failures throw.</returns>
    public static bool TryInitializeScoped<TSource, TFirst, TSecond, TThird, TFourth>(
        scoped ArenaLease<TSource> source,
        NativeArena arena,
        int firstLength,
        int secondLength,
        int thirdLength,
        int fourthLength,
        NativeLeaseSourceQuadSpanInitializer<TSource, TFirst, TSecond, TThird, TFourth> initializer,
        out ArenaLease<TFirst> first,
        out ArenaLease<TSecond> second,
        out ArenaLease<TThird> third,
        out ArenaLease<TFourth> fourth)
        where TSource : unmanaged
        where TFirst : unmanaged
        where TSecond : unmanaged
        where TThird : unmanaged
        where TFourth : unmanaged
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(initializer);
        first = default;
        second = default;
        third = default;
        fourth = default;
        ArgumentOutOfRangeException.ThrowIfNegative(firstLength);
        ArgumentOutOfRangeException.ThrowIfNegative(secondLength);
        ArgumentOutOfRangeException.ThrowIfNegative(thirdLength);
        ArgumentOutOfRangeException.ThrowIfNegative(fourthLength);
        NativeArenaKernel kernel = arena.KernelForInitialization;
        ArenaGroupCheckpoint checkpoint = kernel.BeginScopedGroup(requirePrepared: true);
        bool completed = false;
        bool initializerEntered = false;
        try
        {
            NativeLeaseView<TSource> sourceView = source.GetViewForComposite(kernel, nameof(TryInitializeScoped));
            if (!kernel.TryReserveScopedGroupRange<TFirst>(firstLength, out Span<TFirst> firstSpan)
                || !kernel.TryReserveScopedGroupRange<TSecond>(secondLength, out Span<TSecond> secondSpan)
                || !kernel.TryReserveScopedGroupRange<TThird>(thirdLength, out Span<TThird> thirdSpan)
                || !kernel.TryReserveScopedGroupRange<TFourth>(fourthLength, out Span<TFourth> fourthSpan))
            {
                return false;
            }
            initializerEntered = true;
            initializer(sourceView, firstSpan, secondSpan, thirdSpan, fourthSpan);
            initializerEntered = false;
            first = kernel.PublishScopedGroupRange(firstSpan);
            second = kernel.PublishScopedGroupRange(secondSpan);
            third = kernel.PublishScopedGroupRange(thirdSpan);
            fourth = kernel.PublishScopedGroupRange(fourthSpan);
            completed = true;
            return true;
        }
        catch
        {
            if (initializerEntered)
            {
                kernel.RecordScopedGroupInitializerFailure();
            }
            throw;
        }
        finally
        {
            kernel.EndScopedGroup(checkpoint, completed);
        }
    }

    /// <summary>Tries to publish 8 fully initialized scoped ranges without prepared backing growth.</summary>
    /// <returns>False only for capacity exhaustion, before invoking the initializer; invalid authority and initializer failures throw.</returns>
    public static bool TryInitializeScoped<TSource, TFirst, TSecond, TThird, TFourth, TFifth, TSixth, TSeventh, TEighth>(
        scoped ArenaLease<TSource> source,
        NativeArena arena,
        int firstLength,
        int secondLength,
        int thirdLength,
        int fourthLength,
        int fifthLength,
        int sixthLength,
        int seventhLength,
        int eighthLength,
        NativeLeaseSourceOctupleSpanInitializer<TSource, TFirst, TSecond, TThird, TFourth, TFifth, TSixth, TSeventh, TEighth> initializer,
        out ArenaLease<TFirst> first,
        out ArenaLease<TSecond> second,
        out ArenaLease<TThird> third,
        out ArenaLease<TFourth> fourth,
        out ArenaLease<TFifth> fifth,
        out ArenaLease<TSixth> sixth,
        out ArenaLease<TSeventh> seventh,
        out ArenaLease<TEighth> eighth)
        where TSource : unmanaged
        where TFirst : unmanaged
        where TSecond : unmanaged
        where TThird : unmanaged
        where TFourth : unmanaged
        where TFifth : unmanaged
        where TSixth : unmanaged
        where TSeventh : unmanaged
        where TEighth : unmanaged
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(initializer);
        first = default;
        second = default;
        third = default;
        fourth = default;
        fifth = default;
        sixth = default;
        seventh = default;
        eighth = default;
        ArgumentOutOfRangeException.ThrowIfNegative(firstLength);
        ArgumentOutOfRangeException.ThrowIfNegative(secondLength);
        ArgumentOutOfRangeException.ThrowIfNegative(thirdLength);
        ArgumentOutOfRangeException.ThrowIfNegative(fourthLength);
        ArgumentOutOfRangeException.ThrowIfNegative(fifthLength);
        ArgumentOutOfRangeException.ThrowIfNegative(sixthLength);
        ArgumentOutOfRangeException.ThrowIfNegative(seventhLength);
        ArgumentOutOfRangeException.ThrowIfNegative(eighthLength);
        NativeArenaKernel kernel = arena.KernelForInitialization;
        ArenaGroupCheckpoint checkpoint = kernel.BeginScopedGroup(requirePrepared: true);
        bool completed = false;
        bool initializerEntered = false;
        try
        {
            NativeLeaseView<TSource> sourceView = source.GetViewForComposite(kernel, nameof(TryInitializeScoped));
            if (!kernel.TryReserveScopedGroupRange<TFirst>(firstLength, out Span<TFirst> firstSpan)
                || !kernel.TryReserveScopedGroupRange<TSecond>(secondLength, out Span<TSecond> secondSpan)
                || !kernel.TryReserveScopedGroupRange<TThird>(thirdLength, out Span<TThird> thirdSpan)
                || !kernel.TryReserveScopedGroupRange<TFourth>(fourthLength, out Span<TFourth> fourthSpan)
                || !kernel.TryReserveScopedGroupRange<TFifth>(fifthLength, out Span<TFifth> fifthSpan)
                || !kernel.TryReserveScopedGroupRange<TSixth>(sixthLength, out Span<TSixth> sixthSpan)
                || !kernel.TryReserveScopedGroupRange<TSeventh>(seventhLength, out Span<TSeventh> seventhSpan)
                || !kernel.TryReserveScopedGroupRange<TEighth>(eighthLength, out Span<TEighth> eighthSpan))
            {
                return false;
            }
            initializerEntered = true;
            initializer(sourceView, firstSpan, secondSpan, thirdSpan, fourthSpan, fifthSpan, sixthSpan, seventhSpan, eighthSpan);
            initializerEntered = false;
            first = kernel.PublishScopedGroupRange(firstSpan);
            second = kernel.PublishScopedGroupRange(secondSpan);
            third = kernel.PublishScopedGroupRange(thirdSpan);
            fourth = kernel.PublishScopedGroupRange(fourthSpan);
            fifth = kernel.PublishScopedGroupRange(fifthSpan);
            sixth = kernel.PublishScopedGroupRange(sixthSpan);
            seventh = kernel.PublishScopedGroupRange(seventhSpan);
            eighth = kernel.PublishScopedGroupRange(eighthSpan);
            completed = true;
            return true;
        }
        catch
        {
            if (initializerEntered)
            {
                kernel.RecordScopedGroupInitializerFailure();
            }
            throw;
        }
        finally
        {
            kernel.EndScopedGroup(checkpoint, completed);
        }
    }
}
