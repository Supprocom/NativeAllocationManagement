namespace Supprocom.NativeAllocationManagement;

/// <summary>Runs one synchronous operation over two bounded pooled native views.</summary>
public delegate void NativeLeasePairAction<TFirst, TSecond>(
    scoped NativeLeaseView<TFirst> first,
    scoped NativeLeaseView<TSecond> second);

/// <summary>Runs one synchronous operation over three bounded pooled native views.</summary>
public delegate void NativeLeaseTripleAction<TFirst, TSecond, TThird>(
    scoped NativeLeaseView<TFirst> first,
    scoped NativeLeaseView<TSecond> second,
    scoped NativeLeaseView<TThird> third);

/// <summary>Runs one synchronous operation over a pooled view and an arena view.</summary>
public delegate void NativeLeasePooledArenaAction<TPooled, TArena>(
    scoped NativeLeaseView<TPooled> pooled,
    scoped NativeLeaseView<TArena> arena);

/// <summary>Runs one bounded operation over five direct native views.</summary>
public delegate void NativeLeaseQuintupleAction<TFirst, TSecond, TThird, TFourth, TFifth>(
    scoped NativeLeaseView<TFirst> first,
    scoped NativeLeaseView<TSecond> second,
    scoped NativeLeaseView<TThird> third,
    scoped NativeLeaseView<TFourth> fourth,
    scoped NativeLeaseView<TFifth> fifth);

/// <summary>Provides bounded multi-buffer operations without managed mirror copies.</summary>
public static class NativeLeaseOperations
{
    /// <summary>
    /// Enters both pooled leases for the duration of one callback. Both spans are scoped
    /// to the callback and no handle or view can be retained by the API.
    /// </summary>
    public static void Access<TFirst, TSecond>(
        scoped Pooled<TFirst> first,
        scoped Pooled<TSecond> second,
        NativeLeasePairAction<TFirst, TSecond> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        NativeOwnerKernel firstKernel = first.KernelForComposite;
        NativeOwnerKernel secondKernel = second.KernelForComposite;
        NativeOperationToken firstToken = firstKernel.EnterOperation(
            first.GenerationForComposite,
            first.AllocationIdForComposite,
            nameof(Access));
        try
        {
            NativeOperationToken secondToken = secondKernel.EnterOperation(
                second.GenerationForComposite,
                second.AllocationIdForComposite,
                nameof(Access));
            try
            {
                action(firstToken.GetView<TFirst>(), secondToken.GetView<TSecond>());
            }
            finally
            {
                secondToken.Dispose();
            }
        }
        finally
        {
            firstToken.Dispose();
        }
    }

    /// <summary>
    /// Enters three pooled leases for the duration of one callback. The callback is
    /// the only place where the three bounded native spans can be observed.
    /// </summary>
    public static void Access<TFirst, TSecond, TThird>(
        scoped Pooled<TFirst> first,
        scoped Pooled<TSecond> second,
        scoped Pooled<TThird> third,
        NativeLeaseTripleAction<TFirst, TSecond, TThird> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        NativeOperationToken firstToken = first.KernelForComposite.EnterOperation(
            first.GenerationForComposite,
            first.AllocationIdForComposite,
            nameof(Access));
        try
        {
            NativeOperationToken secondToken = second.KernelForComposite.EnterOperation(
                second.GenerationForComposite,
                second.AllocationIdForComposite,
                nameof(Access));
            try
            {
                NativeOperationToken thirdToken = third.KernelForComposite.EnterOperation(
                    third.GenerationForComposite,
                    third.AllocationIdForComposite,
                    nameof(Access));
                try
                {
                    action(
                        firstToken.GetView<TFirst>(),
                        secondToken.GetView<TSecond>(),
                        thirdToken.GetView<TThird>());
                }
                finally
                {
                    thirdToken.Dispose();
                }
            }
            finally
            {
                secondToken.Dispose();
            }
        }
        finally
        {
            firstToken.Dispose();
        }
    }

    /// <summary>
    /// Enters one typed pool lease and one arena lease for a single bounded callback.
    /// Both views are direct native storage and cannot outlive the callback.
    /// </summary>
    public static void Access<TPooled, TArena>(
        scoped Pooled<TPooled> pooled,
        scoped ArenaLease<TArena> arena,
        NativeLeasePooledArenaAction<TPooled, TArena> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        NativeOperationToken pooledToken = pooled.KernelForComposite.EnterOperation(
            pooled.GenerationForComposite,
            pooled.AllocationIdForComposite,
            nameof(Access));
        try
        {
            NativeOperationToken arenaToken = arena.KernelForComposite.EnterOperation(
                arena.GenerationForComposite,
                arena.AllocationIdForComposite,
                nameof(Access));
            try
            {
                action(pooledToken.GetView<TPooled>(), arenaToken.GetView<TArena>());
            }
            finally
            {
                arenaToken.Dispose();
            }
        }
        finally
        {
            pooledToken.Dispose();
        }
    }

    /// <summary>
    /// Enters five leases for one bounded callback. The callback receives only direct
    /// native views, so it can compose heterogeneous typed storage without managed
    /// mirror copies or a retained handle. Each lease may be backed by a typed pool or
    /// an arena, and all operation tokens are released in reverse entry order.
    /// </summary>
    public static void Access<TFirst, TSecond, TThird, TFourth, TFifth>(
        scoped Pooled<TFirst> first,
        scoped Pooled<TSecond> second,
        scoped Pooled<TThird> third,
        scoped ArenaLease<TFourth> fourth,
        scoped ArenaLease<TFifth> fifth,
        NativeLeaseQuintupleAction<TFirst, TSecond, TThird, TFourth, TFifth> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        NativeOperationToken firstToken = first.KernelForComposite.EnterOperation(
            first.GenerationForComposite,
            first.AllocationIdForComposite,
            nameof(Access));
        try
        {
            NativeOperationToken secondToken = second.KernelForComposite.EnterOperation(
                second.GenerationForComposite,
                second.AllocationIdForComposite,
                nameof(Access));
            try
            {
                NativeOperationToken thirdToken = third.KernelForComposite.EnterOperation(
                    third.GenerationForComposite,
                    third.AllocationIdForComposite,
                    nameof(Access));
                try
                {
                    NativeOperationToken fourthToken = fourth.KernelForComposite.EnterOperation(
                        fourth.GenerationForComposite,
                        fourth.AllocationIdForComposite,
                        nameof(Access));
                    try
                    {
                        NativeOperationToken fifthToken = fifth.KernelForComposite.EnterOperation(
                            fifth.GenerationForComposite,
                            fifth.AllocationIdForComposite,
                            nameof(Access));
                        try
                        {
                            action(
                                firstToken.GetView<TFirst>(),
                                secondToken.GetView<TSecond>(),
                                thirdToken.GetView<TThird>(),
                                fourthToken.GetView<TFourth>(),
                                fifthToken.GetView<TFifth>());
                        }
                        finally
                        {
                            fifthToken.Dispose();
                        }
                    }
                    finally
                    {
                        fourthToken.Dispose();
                    }
                }
                finally
                {
                    thirdToken.Dispose();
                }
            }
            finally
            {
                secondToken.Dispose();
            }
        }
        finally
        {
            firstToken.Dispose();
        }
    }
}
