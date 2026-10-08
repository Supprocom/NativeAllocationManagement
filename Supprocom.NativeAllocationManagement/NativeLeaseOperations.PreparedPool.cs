namespace Supprocom.NativeAllocationManagement;

public static partial class NativeLeaseOperations
{
    /// <summary>
    /// Initializes four scoped arena ranges from one readable pooled source.
    /// The method publishes all four output handles after complete initialization.
    /// </summary>
    public static void InitializeScoped<
        TSource,
        TFirst,
        TSecond,
        TThird,
        TFourth>(
        scoped PreparedPooled<TSource> source,
        NativeConcurrentArena arena,
        int firstLength,
        int secondLength,
        int thirdLength,
        int fourthLength,
        NativeLeaseSourceQuadInitializer<
            TSource,
            TFirst,
            TSecond,
            TThird,
            TFourth> initializer,
        out ConcurrentArenaLease<TFirst> first,
        out ConcurrentArenaLease<TSecond> second,
        out ConcurrentArenaLease<TThird> third,
        out ConcurrentArenaLease<TFourth> fourth)
        where TSource : unmanaged
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(initializer);
        first = default;
        second = default;
        third = default;
        fourth = default;

        PreparedPooledBorrow<TSource> sourceToken =
            source.EnterBorrow(nameof(InitializeScoped));
        try
        {
            InitializeScopedCore(
                sourceToken.View,
                arena,
                firstLength,
                secondLength,
                thirdLength,
                fourthLength,
                initializer,
                out first,
                out second,
                out third,
                out fourth);
        }
        finally
        {
            sourceToken.Dispose();
        }
    }

    /// <summary>
    /// Enters both pooled leases for the duration of one callback. Both spans are scoped
    /// to the callback and no handle or view can be retained by the API.
    /// </summary>
    public static void Access<TFirst, TSecond>(
        scoped PreparedPooled<TFirst> first,
        scoped PreparedPooled<TSecond> second,
        NativeLeasePairAction<TFirst, TSecond> action)
        where TFirst : unmanaged
        where TSecond : unmanaged
    {
        ArgumentNullException.ThrowIfNull(action);
        PreparedPooledBorrow<TFirst> firstToken =
            first.EnterBorrow(nameof(Access));
        try
        {
            PreparedPooledBorrow<TSecond> secondToken =
                second.EnterBorrow(nameof(Access));
            try
            {
                action(firstToken.View, secondToken.View);
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
        scoped PreparedPooled<TFirst> first,
        scoped PreparedPooled<TSecond> second,
        scoped PreparedPooled<TThird> third,
        NativeLeaseTripleAction<TFirst, TSecond, TThird> action)
        where TFirst : unmanaged
        where TSecond : unmanaged
        where TThird : unmanaged
    {
        ArgumentNullException.ThrowIfNull(action);
        PreparedPooledBorrow<TFirst> firstToken =
            first.EnterBorrow(nameof(Access));
        try
        {
            PreparedPooledBorrow<TSecond> secondToken =
                second.EnterBorrow(nameof(Access));
            try
            {
                PreparedPooledBorrow<TThird> thirdToken =
                    third.EnterBorrow(nameof(Access));
                try
                {
                    action(
                        firstToken.View,
                        secondToken.View,
                        thirdToken.View);
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
        scoped PreparedPooled<TPooled> pooled,
        scoped ConcurrentArenaLease<TArena> arena,
        NativeLeasePooledArenaAction<TPooled, TArena> action)
        where TPooled : unmanaged
    {
        ArgumentNullException.ThrowIfNull(action);
        PreparedPooledBorrow<TPooled> pooledToken =
            pooled.EnterBorrow(nameof(Access));
        try
        {
            NativeOperationToken arenaToken =
                arena.EnterForComposite(nameof(Access));
            try
            {
                action(pooledToken.View, arenaToken.GetView<TArena>());
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

    /// <summary>Enters one pooled lease and two same-owner arena leases.</summary>
    public static void Access<TPooled, TFirst, TSecond>(
        scoped PreparedPooled<TPooled> pooled,
        scoped ConcurrentArenaLease<TFirst> first,
        scoped ConcurrentArenaLease<TSecond> second,
        NativeLeasePooledArenaPairAction<
            TPooled,
            TFirst,
            TSecond> action)
        where TPooled : unmanaged
    {
        ArgumentNullException.ThrowIfNull(action);
        PreparedPooledBorrow<TPooled> pooledToken =
            pooled.EnterBorrow(nameof(Access));
        try
        {
            NativeOwnerKernel firstKernel = first.KernelForComposite;
            NativeOwnerKernel secondKernel = second.KernelForComposite;
            if (ReferenceEquals(firstKernel, secondKernel)
                && first.GenerationForComposite
                    == second.GenerationForComposite
                && ReferenceEquals(
                    first.GenerationStateForComposite,
                    second.GenerationStateForComposite))
            {
                NativeCompositeAllocationBuffer allocations = default;
                allocations[0] = first.AllocationStateForComposite;
                allocations[1] = second.AllocationStateForComposite;
                Span<long> allocationIds = stackalloc long[2]
                {
                    first.AllocationIdForComposite,
                    second.AllocationIdForComposite
                };
                NativeCompositeOperationToken arenaToken =
                    firstKernel.EnterCompositeOperation(
                        first.GenerationStateForComposite,
                        allocations,
                        first.GenerationForComposite,
                        allocationIds,
                        nameof(Access));
                try
                {
                    action(
                        pooledToken.View,
                        arenaToken.GetView<TFirst>(0),
                        arenaToken.GetView<TSecond>(1));
                }
                finally
                {
                    arenaToken.Dispose();
                }

                return;
            }

            NativeOperationToken firstToken =
                first.EnterForComposite(nameof(Access));
            try
            {
                NativeOperationToken secondToken =
                    second.EnterForComposite(nameof(Access));
                try
                {
                    action(
                        pooledToken.View,
                        firstToken.GetView<TFirst>(),
                        secondToken.GetView<TSecond>());
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
        finally
        {
            pooledToken.Dispose();
        }
    }

    /// <summary>
    /// Enters one typed-pool lease and four heterogeneous arena leases for one
    /// bounded callback. When all arena leases share an owner generation, their
    /// admission is failure-atomic and uses one composite owner entry.
    /// </summary>
    public static void Access<TPooled, TFirst, TSecond, TThird, TFourth>(
        scoped PreparedPooled<TPooled> pooled,
        scoped ConcurrentArenaLease<TFirst> first,
        scoped ConcurrentArenaLease<TSecond> second,
        scoped ConcurrentArenaLease<TThird> third,
        scoped ConcurrentArenaLease<TFourth> fourth,
        NativeLeaseQuintupleAction<
            TPooled,
            TFirst,
            TSecond,
            TThird,
            TFourth> action)
        where TPooled : unmanaged
    {
        ArgumentNullException.ThrowIfNull(action);
        NativeOwnerKernel firstKernel = first.KernelForComposite;
        NativeOwnerKernel secondKernel = second.KernelForComposite;
        NativeOwnerKernel thirdKernel = third.KernelForComposite;
        NativeOwnerKernel fourthKernel = fourth.KernelForComposite;
        bool compositeArenaEntry = ReferenceEquals(firstKernel, secondKernel)
            && ReferenceEquals(firstKernel, thirdKernel)
            && ReferenceEquals(firstKernel, fourthKernel)
            && first.GenerationForComposite == second.GenerationForComposite
            && first.GenerationForComposite == third.GenerationForComposite
            && first.GenerationForComposite == fourth.GenerationForComposite
            && ReferenceEquals(
                first.GenerationStateForComposite,
                second.GenerationStateForComposite)
            && ReferenceEquals(
                first.GenerationStateForComposite,
                third.GenerationStateForComposite)
            && ReferenceEquals(
                first.GenerationStateForComposite,
                fourth.GenerationStateForComposite);
        PreparedPooledBorrow<TPooled> pooledToken =
            pooled.EnterBorrow(nameof(Access));
        try
        {
            if (compositeArenaEntry)
            {
                NativeCompositeAllocationBuffer allocations = default;
                allocations[0] = first.AllocationStateForComposite;
                allocations[1] = second.AllocationStateForComposite;
                allocations[2] = third.AllocationStateForComposite;
                allocations[3] = fourth.AllocationStateForComposite;
                Span<long> allocationIds = stackalloc long[4]
                {
                    first.AllocationIdForComposite,
                    second.AllocationIdForComposite,
                    third.AllocationIdForComposite,
                    fourth.AllocationIdForComposite
                };
                NativeCompositeOperationToken arenaToken =
                    firstKernel.EnterCompositeOperation(
                        first.GenerationStateForComposite,
                        allocations,
                        first.GenerationForComposite,
                        allocationIds,
                        nameof(Access));
                try
                {
                    action(
                        pooledToken.View,
                        arenaToken.GetView<TFirst>(0),
                        arenaToken.GetView<TSecond>(1),
                        arenaToken.GetView<TThird>(2),
                        arenaToken.GetView<TFourth>(3));
                }
                finally
                {
                    arenaToken.Dispose();
                }

                return;
            }

            NativeOperationToken firstToken =
                first.EnterForComposite(nameof(Access));
            try
            {
                NativeOperationToken secondToken =
                    second.EnterForComposite(nameof(Access));
                try
                {
                    NativeOperationToken thirdToken =
                        third.EnterForComposite(nameof(Access));
                    try
                    {
                        NativeOperationToken fourthToken =
                            fourth.EnterForComposite(nameof(Access));
                        try
                        {
                            action(
                                pooledToken.View,
                                firstToken.GetView<TFirst>(),
                                secondToken.GetView<TSecond>(),
                                thirdToken.GetView<TThird>(),
                                fourthToken.GetView<TFourth>());
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
        finally
        {
            pooledToken.Dispose();
        }
    }

    /// <summary>Enters three typed leases and one arena lease for one callback.</summary>
    public static void Access<TFirst, TSecond, TThird, TFourth>(
        scoped PreparedPooled<TFirst> first,
        scoped PreparedPooled<TSecond> second,
        scoped PreparedPooled<TThird> third,
        scoped ConcurrentArenaLease<TFourth> fourth,
        NativeLeaseQuadrupleAction<TFirst, TSecond, TThird, TFourth> action)
        where TFirst : unmanaged
        where TSecond : unmanaged
        where TThird : unmanaged
    {
        ArgumentNullException.ThrowIfNull(action);
        PreparedPooledBorrow<TFirst> firstToken =
            first.EnterBorrow(nameof(Access));
        try
        {
            PreparedPooledBorrow<TSecond> secondToken =
                second.EnterBorrow(nameof(Access));
            try
            {
                PreparedPooledBorrow<TThird> thirdToken =
                    third.EnterBorrow(nameof(Access));
                try
                {
                    NativeOperationToken fourthToken =
                        fourth.EnterForComposite(nameof(Access));
                    try
                    {
                        action(
                            firstToken.View,
                            secondToken.View,
                            thirdToken.View,
                            fourthToken.GetView<TFourth>());
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

    /// <summary>
    /// Enters one fixed five-view composition for a bounded callback. The first three
    /// parameters are typed-pool leases and the fourth and fifth parameters are arena
    /// leases. This reusable shape is intended for a stage with three stable repeated
    /// buffers and two heterogeneous ranges; use the pair, triple, or pooled-arena
    /// overloads when a different backing composition is required. The callback receives
    /// only direct native views and all operation tokens are released in reverse order.
    /// </summary>
    public static void Access<TFirst, TSecond, TThird, TFourth, TFifth>(
        scoped PreparedPooled<TFirst> first,
        scoped PreparedPooled<TSecond> second,
        scoped PreparedPooled<TThird> third,
        scoped ConcurrentArenaLease<TFourth> fourth,
        scoped ConcurrentArenaLease<TFifth> fifth,
        NativeLeaseQuintupleAction<TFirst, TSecond, TThird, TFourth, TFifth> action)
        where TFirst : unmanaged
        where TSecond : unmanaged
        where TThird : unmanaged
    {
        ArgumentNullException.ThrowIfNull(action);
        NativeOwnerKernel fourthKernel = fourth.KernelForComposite;
        NativeOwnerKernel fifthKernel = fifth.KernelForComposite;
        bool compositeArenaEntry = ReferenceEquals(fourthKernel, fifthKernel)
            && fourth.GenerationForComposite == fifth.GenerationForComposite
            && ReferenceEquals(
                fourth.GenerationStateForComposite,
                fifth.GenerationStateForComposite);
        PreparedPooledBorrow<TFirst> firstToken =
            first.EnterBorrow(nameof(Access));
        try
        {
            PreparedPooledBorrow<TSecond> secondToken =
                second.EnterBorrow(nameof(Access));
            try
            {
                PreparedPooledBorrow<TThird> thirdToken =
                    third.EnterBorrow(nameof(Access));
                try
                {
                    if (compositeArenaEntry)
                    {
                        NativeCompositeAllocationBuffer allocations = default;
                        allocations[0] = fourth.AllocationStateForComposite;
                        allocations[1] = fifth.AllocationStateForComposite;
                        Span<long> allocationIds = stackalloc long[2]
                        {
                            fourth.AllocationIdForComposite,
                            fifth.AllocationIdForComposite
                        };
                        NativeCompositeOperationToken arenaToken = fourthKernel.EnterCompositeOperation(
                            fourth.GenerationStateForComposite,
                            allocations,
                            fourth.GenerationForComposite,
                            allocationIds,
                            nameof(Access));
                        try
                        {
                            action(
                                firstToken.View,
                                secondToken.View,
                                thirdToken.View,
                                arenaToken.GetView<TFourth>(0),
                                arenaToken.GetView<TFifth>(1));
                        }
                        finally
                        {
                            arenaToken.Dispose();
                        }

                        return;
                    }

                    NativeOperationToken fourthToken =
                        fourth.EnterForComposite(nameof(Access));
                    try
                    {
                        NativeOperationToken fifthToken =
                            fifth.EnterForComposite(nameof(Access));
                        try
                        {
                            action(
                                firstToken.View,
                                secondToken.View,
                                thirdToken.View,
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
