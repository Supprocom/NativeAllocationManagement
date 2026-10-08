using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

public static partial class NativeLeaseOperations
{
    /// <summary>Runs one same-owner fast-arena callback after validating all 2 ranges.</summary>
    public static void Access<TFirst, TSecond>(
        scoped ArenaLease<TFirst> first,
        scoped ArenaLease<TSecond> second,
        NativeLeasePairAction<TFirst, TSecond> action)
        where TFirst : unmanaged
        where TSecond : unmanaged
    {
        ArgumentNullException.ThrowIfNull(action);
        NativeArena kernel = first.KernelForComposite;
        kernel.BeginCompositeBorrow();
        try
        {
            NativeLeaseView<TFirst> firstView = first.GetViewForComposite(kernel, nameof(Access));
            NativeLeaseView<TSecond> secondView = second.GetViewForComposite(kernel, nameof(Access));
            action(firstView, secondView);
        }
        finally
        {
            kernel.ExitBorrow();
        }
    }

    /// <summary>Runs one same-owner fast-arena callback after validating all 3 ranges.</summary>
    public static void Access<TFirst, TSecond, TThird>(
        scoped ArenaLease<TFirst> first,
        scoped ArenaLease<TSecond> second,
        scoped ArenaLease<TThird> third,
        NativeLeaseTripleAction<TFirst, TSecond, TThird> action)
        where TFirst : unmanaged
        where TSecond : unmanaged
        where TThird : unmanaged
    {
        ArgumentNullException.ThrowIfNull(action);
        NativeArena kernel = first.KernelForComposite;
        kernel.BeginCompositeBorrow();
        try
        {
            NativeLeaseView<TFirst> firstView = first.GetViewForComposite(kernel, nameof(Access));
            NativeLeaseView<TSecond> secondView = second.GetViewForComposite(kernel, nameof(Access));
            NativeLeaseView<TThird> thirdView = third.GetViewForComposite(kernel, nameof(Access));
            action(firstView, secondView, thirdView);
        }
        finally
        {
            kernel.ExitBorrow();
        }
    }

    /// <summary>Runs one same-owner fast-arena callback after validating all 4 ranges.</summary>
    public static void Access<TFirst, TSecond, TThird, TFourth>(
        scoped ArenaLease<TFirst> first,
        scoped ArenaLease<TSecond> second,
        scoped ArenaLease<TThird> third,
        scoped ArenaLease<TFourth> fourth,
        NativeLeaseQuadrupleAction<TFirst, TSecond, TThird, TFourth> action)
        where TFirst : unmanaged
        where TSecond : unmanaged
        where TThird : unmanaged
        where TFourth : unmanaged
    {
        ArgumentNullException.ThrowIfNull(action);
        NativeArena kernel = first.KernelForComposite;
        kernel.BeginCompositeBorrow();
        try
        {
            NativeLeaseView<TFirst> firstView = first.GetViewForComposite(kernel, nameof(Access));
            NativeLeaseView<TSecond> secondView = second.GetViewForComposite(kernel, nameof(Access));
            NativeLeaseView<TThird> thirdView = third.GetViewForComposite(kernel, nameof(Access));
            NativeLeaseView<TFourth> fourthView = fourth.GetViewForComposite(kernel, nameof(Access));
            action(firstView, secondView, thirdView, fourthView);
        }
        finally
        {
            kernel.ExitBorrow();
        }
    }

    /// <summary>Runs one same-owner fast-arena callback after validating all 5 ranges.</summary>
    public static void Access<TFirst, TSecond, TThird, TFourth, TFifth>(
        scoped ArenaLease<TFirst> first,
        scoped ArenaLease<TSecond> second,
        scoped ArenaLease<TThird> third,
        scoped ArenaLease<TFourth> fourth,
        scoped ArenaLease<TFifth> fifth,
        NativeLeaseQuintupleAction<TFirst, TSecond, TThird, TFourth, TFifth> action)
        where TFirst : unmanaged
        where TSecond : unmanaged
        where TThird : unmanaged
        where TFourth : unmanaged
        where TFifth : unmanaged
    {
        ArgumentNullException.ThrowIfNull(action);
        NativeArena kernel = first.KernelForComposite;
        kernel.BeginCompositeBorrow();
        try
        {
            NativeLeaseView<TFirst> firstView = first.GetViewForComposite(kernel, nameof(Access));
            NativeLeaseView<TSecond> secondView = second.GetViewForComposite(kernel, nameof(Access));
            NativeLeaseView<TThird> thirdView = third.GetViewForComposite(kernel, nameof(Access));
            NativeLeaseView<TFourth> fourthView = fourth.GetViewForComposite(kernel, nameof(Access));
            NativeLeaseView<TFifth> fifthView = fifth.GetViewForComposite(kernel, nameof(Access));
            action(firstView, secondView, thirdView, fourthView, fifthView);
        }
        finally
        {
            kernel.ExitBorrow();
        }
    }

    /// <summary>Runs one same-owner fast-arena callback after validating all 7 ranges.</summary>
    public static void Access<TFirst, TSecond, TThird, TFourth, TFifth, TSixth, TSeventh>(
        scoped ArenaLease<TFirst> first,
        scoped ArenaLease<TSecond> second,
        scoped ArenaLease<TThird> third,
        scoped ArenaLease<TFourth> fourth,
        scoped ArenaLease<TFifth> fifth,
        scoped ArenaLease<TSixth> sixth,
        scoped ArenaLease<TSeventh> seventh,
        NativeLeaseSeptupleAction<TFirst, TSecond, TThird, TFourth, TFifth, TSixth, TSeventh> action)
        where TFirst : unmanaged
        where TSecond : unmanaged
        where TThird : unmanaged
        where TFourth : unmanaged
        where TFifth : unmanaged
        where TSixth : unmanaged
        where TSeventh : unmanaged
    {
        ArgumentNullException.ThrowIfNull(action);
        NativeArena kernel = first.KernelForComposite;
        kernel.BeginCompositeBorrow();
        try
        {
            NativeLeaseView<TFirst> firstView = first.GetViewForComposite(kernel, nameof(Access));
            NativeLeaseView<TSecond> secondView = second.GetViewForComposite(kernel, nameof(Access));
            NativeLeaseView<TThird> thirdView = third.GetViewForComposite(kernel, nameof(Access));
            NativeLeaseView<TFourth> fourthView = fourth.GetViewForComposite(kernel, nameof(Access));
            NativeLeaseView<TFifth> fifthView = fifth.GetViewForComposite(kernel, nameof(Access));
            NativeLeaseView<TSixth> sixthView = sixth.GetViewForComposite(kernel, nameof(Access));
            NativeLeaseView<TSeventh> seventhView = seventh.GetViewForComposite(kernel, nameof(Access));
            action(firstView, secondView, thirdView, fourthView, fifthView, sixthView, seventhView);
        }
        finally
        {
            kernel.ExitBorrow();
        }
    }

    /// <summary>Runs one same-owner fast-arena callback after validating all 8 ranges.</summary>
    public static void Access<TFirst, TSecond, TThird, TFourth, TFifth, TSixth, TSeventh, TEighth>(
        scoped ArenaLease<TFirst> first,
        scoped ArenaLease<TSecond> second,
        scoped ArenaLease<TThird> third,
        scoped ArenaLease<TFourth> fourth,
        scoped ArenaLease<TFifth> fifth,
        scoped ArenaLease<TSixth> sixth,
        scoped ArenaLease<TSeventh> seventh,
        scoped ArenaLease<TEighth> eighth,
        NativeLeaseOctupleAction<TFirst, TSecond, TThird, TFourth, TFifth, TSixth, TSeventh, TEighth> action)
        where TFirst : unmanaged
        where TSecond : unmanaged
        where TThird : unmanaged
        where TFourth : unmanaged
        where TFifth : unmanaged
        where TSixth : unmanaged
        where TSeventh : unmanaged
        where TEighth : unmanaged
    {
        ArgumentNullException.ThrowIfNull(action);
        NativeArena kernel = first.KernelForComposite;
        kernel.BeginCompositeBorrow();
        try
        {
            NativeLeaseView<TFirst> firstView = first.GetViewForComposite(kernel, nameof(Access));
            NativeLeaseView<TSecond> secondView = second.GetViewForComposite(kernel, nameof(Access));
            NativeLeaseView<TThird> thirdView = third.GetViewForComposite(kernel, nameof(Access));
            NativeLeaseView<TFourth> fourthView = fourth.GetViewForComposite(kernel, nameof(Access));
            NativeLeaseView<TFifth> fifthView = fifth.GetViewForComposite(kernel, nameof(Access));
            NativeLeaseView<TSixth> sixthView = sixth.GetViewForComposite(kernel, nameof(Access));
            NativeLeaseView<TSeventh> seventhView = seventh.GetViewForComposite(kernel, nameof(Access));
            NativeLeaseView<TEighth> eighthView = eighth.GetViewForComposite(kernel, nameof(Access));
            action(firstView, secondView, thirdView, fourthView, fifthView, sixthView, seventhView, eighthView);
        }
        finally
        {
            kernel.ExitBorrow();
        }
    }

    /// <summary>Initializes 4 same-owner fast scoped ranges with failure-atomic publication.</summary>
    public static void InitializeScoped<TSource, TFirst, TSecond, TThird, TFourth>(
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
        NativeArena kernel = arena.KernelForInitialization;
        long payloadBytes = checked((long)firstLength * Unsafe.SizeOf<TFirst>()
            + (long)secondLength * Unsafe.SizeOf<TSecond>()
            + (long)thirdLength * Unsafe.SizeOf<TThird>()
            + (long)fourthLength * Unsafe.SizeOf<TFourth>());
        ArenaGroupCheckpoint checkpoint = kernel.BeginScopedGroup(payloadBytes);
        bool completed = false;
        bool initializerEntered = false;
        try
        {
            NativeLeaseView<TSource> sourceView = source.GetViewForComposite(kernel, nameof(InitializeScoped));
            Span<TFirst> firstSpan = kernel.ReserveScopedGroupRange<TFirst>(firstLength);
            Span<TSecond> secondSpan = kernel.ReserveScopedGroupRange<TSecond>(secondLength);
            Span<TThird> thirdSpan = kernel.ReserveScopedGroupRange<TThird>(thirdLength);
            Span<TFourth> fourthSpan = kernel.ReserveScopedGroupRange<TFourth>(fourthLength);
            initializerEntered = true;
            initializer(sourceView, firstSpan, secondSpan, thirdSpan, fourthSpan);
            initializerEntered = false;
            first = kernel.PublishScopedGroupRange(firstSpan);
            second = kernel.PublishScopedGroupRange(secondSpan);
            third = kernel.PublishScopedGroupRange(thirdSpan);
            fourth = kernel.PublishScopedGroupRange(fourthSpan);
            completed = true;
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
            kernel.EndScopedGroup(checkpoint, completed, payloadBytes);
        }
    }

    /// <summary>Initializes 8 same-owner fast scoped ranges with failure-atomic publication.</summary>
    public static void InitializeScoped<TSource, TFirst, TSecond, TThird, TFourth, TFifth, TSixth, TSeventh, TEighth>(
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
        NativeArena kernel = arena.KernelForInitialization;
        long payloadBytes = checked((long)firstLength * Unsafe.SizeOf<TFirst>()
            + (long)secondLength * Unsafe.SizeOf<TSecond>()
            + (long)thirdLength * Unsafe.SizeOf<TThird>()
            + (long)fourthLength * Unsafe.SizeOf<TFourth>()
            + (long)fifthLength * Unsafe.SizeOf<TFifth>()
            + (long)sixthLength * Unsafe.SizeOf<TSixth>()
            + (long)seventhLength * Unsafe.SizeOf<TSeventh>()
            + (long)eighthLength * Unsafe.SizeOf<TEighth>());
        ArenaGroupCheckpoint checkpoint = kernel.BeginScopedGroup(payloadBytes);
        bool completed = false;
        bool initializerEntered = false;
        try
        {
            NativeLeaseView<TSource> sourceView = source.GetViewForComposite(kernel, nameof(InitializeScoped));
            Span<TFirst> firstSpan = kernel.ReserveScopedGroupRange<TFirst>(firstLength);
            Span<TSecond> secondSpan = kernel.ReserveScopedGroupRange<TSecond>(secondLength);
            Span<TThird> thirdSpan = kernel.ReserveScopedGroupRange<TThird>(thirdLength);
            Span<TFourth> fourthSpan = kernel.ReserveScopedGroupRange<TFourth>(fourthLength);
            Span<TFifth> fifthSpan = kernel.ReserveScopedGroupRange<TFifth>(fifthLength);
            Span<TSixth> sixthSpan = kernel.ReserveScopedGroupRange<TSixth>(sixthLength);
            Span<TSeventh> seventhSpan = kernel.ReserveScopedGroupRange<TSeventh>(seventhLength);
            Span<TEighth> eighthSpan = kernel.ReserveScopedGroupRange<TEighth>(eighthLength);
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
            kernel.EndScopedGroup(checkpoint, completed, payloadBytes);
        }
    }
}
