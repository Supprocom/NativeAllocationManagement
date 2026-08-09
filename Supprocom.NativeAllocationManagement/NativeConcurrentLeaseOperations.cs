namespace Supprocom.NativeAllocationManagement;

/// <summary>Provides bounded operations for synchronized pooled leases.</summary>
public static partial class NativeLeaseOperations
{
    /// <summary>Initializes four scoped ranges from one synchronized source.</summary>
    public static void InitializeScoped<
        TSource,
        TFirst,
        TSecond,
        TThird,
        TFourth>(
        scoped ConcurrentPooled<TSource> source,
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
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(initializer);
        first = default;
        second = default;
        third = default;
        fourth = default;

        NativeOperationToken sourceToken =
            source.EnterForComposite(nameof(InitializeScoped));
        try
        {
            InitializeScopedCore(
                sourceToken.GetView<TSource>(),
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

    /// <summary>Runs one callback over two synchronized pooled views.</summary>
    public static void Access<TFirst, TSecond>(
        scoped ConcurrentPooled<TFirst> first,
        scoped ConcurrentPooled<TSecond> second,
        NativeLeasePairAction<TFirst, TSecond> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        NativeOperationToken firstToken =
            first.EnterForComposite(nameof(Access));
        try
        {
            NativeOperationToken secondToken =
                second.EnterForComposite(nameof(Access));
            try
            {
                action(
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

    /// <summary>Runs one callback over three synchronized pooled views.</summary>
    public static void Access<TFirst, TSecond, TThird>(
        scoped ConcurrentPooled<TFirst> first,
        scoped ConcurrentPooled<TSecond> second,
        scoped ConcurrentPooled<TThird> third,
        NativeLeaseTripleAction<TFirst, TSecond, TThird> action)
    {
        ArgumentNullException.ThrowIfNull(action);
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

    /// <summary>Runs one callback over one pooled and one Arena view.</summary>
    public static void Access<TPooled, TArena>(
        scoped ConcurrentPooled<TPooled> pooled,
        scoped ConcurrentArenaLease<TArena> arena,
        NativeLeasePooledArenaAction<TPooled, TArena> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        NativeOperationToken pooledToken =
            pooled.EnterForComposite(nameof(Access));
        try
        {
            NativeOperationToken arenaToken =
                arena.EnterForComposite(nameof(Access));
            try
            {
                action(
                    pooledToken.GetView<TPooled>(),
                    arenaToken.GetView<TArena>());
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

    /// <summary>Runs one callback over one pooled and two Arena views.</summary>
    public static void Access<TPooled, TFirst, TSecond>(
        scoped ConcurrentPooled<TPooled> pooled,
        scoped ConcurrentArenaLease<TFirst> first,
        scoped ConcurrentArenaLease<TSecond> second,
        NativeLeasePooledArenaPairAction<
            TPooled,
            TFirst,
            TSecond> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        NativeOperationToken pooledToken =
            pooled.EnterForComposite(nameof(Access));
        try
        {
            NativeOperationToken firstToken =
                first.EnterForComposite(nameof(Access));
            try
            {
                NativeOperationToken secondToken =
                    second.EnterForComposite(nameof(Access));
                try
                {
                    action(
                        pooledToken.GetView<TPooled>(),
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

    /// <summary>Runs one callback over one pooled and four Arena views.</summary>
    public static void Access<
        TPooled,
        TFirst,
        TSecond,
        TThird,
        TFourth>(
        scoped ConcurrentPooled<TPooled> pooled,
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
    {
        ArgumentNullException.ThrowIfNull(action);
        NativeOperationToken pooledToken =
            pooled.EnterForComposite(nameof(Access));
        try
        {
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
                                pooledToken.GetView<TPooled>(),
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

    /// <summary>Runs one callback over three pooled and one Arena view.</summary>
    public static void Access<TFirst, TSecond, TThird, TFourth>(
        scoped ConcurrentPooled<TFirst> first,
        scoped ConcurrentPooled<TSecond> second,
        scoped ConcurrentPooled<TThird> third,
        scoped ConcurrentArenaLease<TFourth> fourth,
        NativeLeaseQuadrupleAction<
            TFirst,
            TSecond,
            TThird,
            TFourth> action)
    {
        ArgumentNullException.ThrowIfNull(action);
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

    /// <summary>Runs one callback over three pooled and two Arena views.</summary>
    public static void Access<
        TFirst,
        TSecond,
        TThird,
        TFourth,
        TFifth>(
        scoped ConcurrentPooled<TFirst> first,
        scoped ConcurrentPooled<TSecond> second,
        scoped ConcurrentPooled<TThird> third,
        scoped ConcurrentArenaLease<TFourth> fourth,
        scoped ConcurrentArenaLease<TFifth> fifth,
        NativeLeaseQuintupleAction<
            TFirst,
            TSecond,
            TThird,
            TFourth,
            TFifth> action)
    {
        ArgumentNullException.ThrowIfNull(action);
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
                        NativeOperationToken fifthToken =
                            fifth.EnterForComposite(nameof(Access));
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
