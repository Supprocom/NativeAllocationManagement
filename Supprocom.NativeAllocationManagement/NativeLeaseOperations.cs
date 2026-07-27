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

/// <summary>Runs one synchronous operation over four bounded native views.</summary>
public delegate void NativeLeaseQuadrupleAction<TFirst, TSecond, TThird, TFourth>(
    scoped NativeLeaseView<TFirst> first,
    scoped NativeLeaseView<TSecond> second,
    scoped NativeLeaseView<TThird> third,
    scoped NativeLeaseView<TFourth> fourth);

/// <summary>Runs one synchronous operation over a pooled view and an arena view.</summary>
public delegate void NativeLeasePooledArenaAction<TPooled, TArena>(
    scoped NativeLeaseView<TPooled> pooled,
    scoped NativeLeaseView<TArena> arena);

/// <summary>Runs one operation over one pooled view and two arena views.</summary>
public delegate void NativeLeasePooledArenaPairAction<TPooled, TFirst, TSecond>(
    scoped NativeLeaseView<TPooled> pooled,
    scoped NativeLeaseView<TFirst> first,
    scoped NativeLeaseView<TSecond> second);

/// <summary>Runs one bounded operation over five direct native views.</summary>
public delegate void NativeLeaseQuintupleAction<TFirst, TSecond, TThird, TFourth, TFifth>(
    scoped NativeLeaseView<TFirst> first,
    scoped NativeLeaseView<TSecond> second,
    scoped NativeLeaseView<TThird> third,
    scoped NativeLeaseView<TFourth> fourth,
    scoped NativeLeaseView<TFifth> fifth);

/// <summary>Runs one bounded operation over seven direct native views.</summary>
public delegate void NativeLeaseSeptupleAction<
    TFirst,
    TSecond,
    TThird,
    TFourth,
    TFifth,
    TSixth,
    TSeventh>(
    scoped NativeLeaseView<TFirst> first,
    scoped NativeLeaseView<TSecond> second,
    scoped NativeLeaseView<TThird> third,
    scoped NativeLeaseView<TFourth> fourth,
    scoped NativeLeaseView<TFifth> fifth,
    scoped NativeLeaseView<TSixth> sixth,
    scoped NativeLeaseView<TSeventh> seventh);

/// <summary>Runs one bounded operation over eight direct native views.</summary>
public delegate void NativeLeaseOctupleAction<
    TFirst,
    TSecond,
    TThird,
    TFourth,
    TFifth,
    TSixth,
    TSeventh,
    TEighth>(
    scoped NativeLeaseView<TFirst> first,
    scoped NativeLeaseView<TSecond> second,
    scoped NativeLeaseView<TThird> third,
    scoped NativeLeaseView<TFourth> fourth,
    scoped NativeLeaseView<TFifth> fifth,
    scoped NativeLeaseView<TSixth> sixth,
    scoped NativeLeaseView<TSeventh> seventh,
    scoped NativeLeaseView<TEighth> eighth);

/// <summary>Reads one lease while it initializes four scoped heterogeneous ranges.</summary>
public delegate void NativeLeaseSourceQuadInitializer<
    TSource,
    TFirst,
    TSecond,
    TThird,
    TFourth>(
    scoped NativeLeaseView<TSource> source,
    scoped NativeLeaseWriter<TFirst> first,
    scoped NativeLeaseWriter<TSecond> second,
    scoped NativeLeaseWriter<TThird> third,
    scoped NativeLeaseWriter<TFourth> fourth);

/// <summary>Initializes four unmanaged ranges before atomic publication.</summary>
public delegate void NativeLeaseSourceQuadSpanInitializer<
    TSource,
    TFirst,
    TSecond,
    TThird,
    TFourth>(
    scoped NativeLeaseView<TSource> source,
    scoped Span<TFirst> first,
    scoped Span<TSecond> second,
    scoped Span<TThird> third,
    scoped Span<TFourth> fourth)
    where TFirst : unmanaged
    where TSecond : unmanaged
    where TThird : unmanaged
    where TFourth : unmanaged;

/// <summary>Initializes eight unmanaged ranges before atomic publication.</summary>
public delegate void NativeLeaseSourceOctupleSpanInitializer<
    TSource,
    TFirst,
    TSecond,
    TThird,
    TFourth,
    TFifth,
    TSixth,
    TSeventh,
    TEighth>(
    scoped NativeLeaseView<TSource> source,
    scoped Span<TFirst> first,
    scoped Span<TSecond> second,
    scoped Span<TThird> third,
    scoped Span<TFourth> fourth,
    scoped Span<TFifth> fifth,
    scoped Span<TSixth> sixth,
    scoped Span<TSeventh> seventh,
    scoped Span<TEighth> eighth)
    where TFirst : unmanaged
    where TSecond : unmanaged
    where TThird : unmanaged
    where TFourth : unmanaged
    where TFifth : unmanaged
    where TSixth : unmanaged
    where TSeventh : unmanaged
    where TEighth : unmanaged;

/// <summary>Provides bounded multi-buffer operations without managed mirror copies.</summary>
public static class NativeLeaseOperations
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
        scoped Pooled<TSource> source,
        NativeArena arena,
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
        out ArenaLease<TFirst> first,
        out ArenaLease<TSecond> second,
        out ArenaLease<TThird> third,
        out ArenaLease<TFourth> fourth)
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

    /// <summary>
    /// Initializes four scoped arena ranges from one readable arena source.
    /// The method publishes all four output handles after complete initialization.
    /// </summary>
    public static void InitializeScoped<
        TSource,
        TFirst,
        TSecond,
        TThird,
        TFourth>(
        scoped ArenaLease<TSource> source,
        NativeArena arena,
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
        out ArenaLease<TFirst> first,
        out ArenaLease<TSecond> second,
        out ArenaLease<TThird> third,
        out ArenaLease<TFourth> fourth)
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(initializer);
        first = default;
        second = default;
        third = default;
        fourth = default;

        NativeOwnerKernel arenaKernel =
            arena.KernelForInitialization;
        if (ReferenceEquals(
                source.KernelForComposite,
                arenaKernel))
        {
            NativeGeneration sourceGeneration =
                source.GenerationStateForComposite;
            NativeAllocation sourceAllocation =
                source.AllocationStateForComposite;
            InitializeScopedCore(
                new NativeLeaseView<TSource>(
                    sourceAllocation),
                arena,
                firstLength,
                secondLength,
                thirdLength,
                fourthLength,
                initializer,
                out first,
                out second,
                out third,
                out fourth,
                sourceGeneration,
                sourceAllocation,
                source.GenerationForComposite,
                source.AllocationIdForComposite);
            return;
        }

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

    private static void InitializeScopedCore<
        TSource,
        TFirst,
        TSecond,
        TThird,
        TFourth>(
        scoped NativeLeaseView<TSource> source,
        NativeArena arena,
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
        out ArenaLease<TFirst> first,
        out ArenaLease<TSecond> second,
        out ArenaLease<TThird> third,
        out ArenaLease<TFourth> fourth,
        NativeGeneration? sourceGeneration = null,
        NativeAllocation? sourceAllocation = null,
        long sourceGenerationNumber = 0,
        long sourceAllocationId = 0)
    {
        NativeOwnerKernel arenaKernel = arena.KernelForInitialization;
        NativeBumpInitializationGroupBuffer reservations = default;
        bool usesSingleInitializationAdmission =
            arenaKernel.BeginBumpInitializationGroup(
                ref reservations,
                firstLength,
                NativeTypeLayout.StorageSize<TFirst>(),
                NativeTypeLayout.Alignment<TFirst>(),
                NativeTypeLayout.ContainsReferences<TFirst>(),
                secondLength,
                NativeTypeLayout.StorageSize<TSecond>(),
                NativeTypeLayout.Alignment<TSecond>(),
                NativeTypeLayout.ContainsReferences<TSecond>(),
                thirdLength,
                NativeTypeLayout.StorageSize<TThird>(),
                NativeTypeLayout.Alignment<TThird>(),
                NativeTypeLayout.ContainsReferences<TThird>(),
                fourthLength,
                NativeTypeLayout.StorageSize<TFourth>(),
                NativeTypeLayout.Alignment<TFourth>(),
                NativeTypeLayout.ContainsReferences<TFourth>(),
                sourceGeneration: sourceGeneration,
                sourceAllocation: sourceAllocation,
                sourceGenerationNumber: sourceGenerationNumber,
                sourceAllocationId: sourceAllocationId);
        int firstInitializedLength = 0;
        int secondInitializedLength = 0;
        int thirdInitializedLength = 0;
        int fourthInitializedLength = 0;
        try
        {
            initializer(
                source,
                new NativeLeaseWriter<TFirst>(
                    reservations[0].Allocation,
                    ref firstInitializedLength),
                new NativeLeaseWriter<TSecond>(
                    reservations[1].Allocation,
                    ref secondInitializedLength),
                new NativeLeaseWriter<TThird>(
                    reservations[2].Allocation,
                    ref thirdInitializedLength),
                new NativeLeaseWriter<TFourth>(
                    reservations[3].Allocation,
                    ref fourthInitializedLength));

            reservations[0].Allocation.InitializedLength =
                firstInitializedLength;
            reservations[1].Allocation.InitializedLength =
                secondInitializedLength;
            reservations[2].Allocation.InitializedLength =
                thirdInitializedLength;
            reservations[3].Allocation.InitializedLength =
                fourthInitializedLength;
            arenaKernel.CompleteBumpInitializationGroup(
                ref reservations,
                usesSingleInitializationAdmission);
            first = new ArenaLease<TFirst>(
                arenaKernel,
                reservations[0].Lease);
            second = new ArenaLease<TSecond>(
                arenaKernel,
                reservations[1].Lease);
            third = new ArenaLease<TThird>(
                arenaKernel,
                reservations[2].Lease);
            fourth = new ArenaLease<TFourth>(
                arenaKernel,
                reservations[3].Lease);
        }
        catch
        {
            reservations[0].Allocation.InitializedLength =
                firstInitializedLength;
            reservations[1].Allocation.InitializedLength =
                secondInitializedLength;
            reservations[2].Allocation.InitializedLength =
                thirdInitializedLength;
            reservations[3].Allocation.InitializedLength =
                fourthInitializedLength;
            arenaKernel.AbortBumpInitializationGroup(
                ref reservations,
                usesSingleInitializationAdmission);
            throw;
        }
    }

    /// <summary>Initializes four unmanaged scoped ranges from one arena source.</summary>
    public static void InitializeScoped<
        TSource,
        TFirst,
        TSecond,
        TThird,
        TFourth>(
        scoped ArenaLease<TSource> source,
        NativeArena arena,
        int firstLength,
        int secondLength,
        int thirdLength,
        int fourthLength,
        NativeLeaseSourceQuadSpanInitializer<
            TSource,
            TFirst,
            TSecond,
            TThird,
            TFourth> initializer,
        out ArenaLease<TFirst> first,
        out ArenaLease<TSecond> second,
        out ArenaLease<TThird> third,
        out ArenaLease<TFourth> fourth)
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

        NativeOwnerKernel arenaKernel =
            arena.KernelForInitialization;
        if (ReferenceEquals(
                source.KernelForComposite,
                arenaKernel))
        {
            NativeGeneration sourceGeneration =
                source.GenerationStateForComposite;
            NativeAllocation sourceAllocation =
                source.AllocationStateForComposite;
            InitializeScopedSpanCore(
                new NativeLeaseView<TSource>(
                    sourceAllocation),
                arena,
                firstLength,
                secondLength,
                thirdLength,
                fourthLength,
                initializer,
                out first,
                out second,
                out third,
                out fourth,
                sourceGeneration,
                sourceAllocation,
                source.GenerationForComposite,
                source.AllocationIdForComposite);
            return;
        }

        NativeOperationToken sourceToken =
            source.EnterForComposite(nameof(InitializeScoped));
        try
        {
            InitializeScopedSpanCore(
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

    private static void InitializeScopedSpanCore<
        TSource,
        TFirst,
        TSecond,
        TThird,
        TFourth>(
        scoped NativeLeaseView<TSource> source,
        NativeArena arena,
        int firstLength,
        int secondLength,
        int thirdLength,
        int fourthLength,
        NativeLeaseSourceQuadSpanInitializer<
            TSource,
            TFirst,
            TSecond,
            TThird,
            TFourth> initializer,
        out ArenaLease<TFirst> first,
        out ArenaLease<TSecond> second,
        out ArenaLease<TThird> third,
        out ArenaLease<TFourth> fourth,
        NativeGeneration? sourceGeneration = null,
        NativeAllocation? sourceAllocation = null,
        long sourceGenerationNumber = 0,
        long sourceAllocationId = 0)
        where TFirst : unmanaged
        where TSecond : unmanaged
        where TThird : unmanaged
        where TFourth : unmanaged
    {
        NativeOwnerKernel arenaKernel = arena.KernelForInitialization;
        NativeBumpInitializationGroupBuffer reservations = default;
        bool usesSingleInitializationAdmission =
            arenaKernel.BeginBumpInitializationGroup(
                ref reservations,
                firstLength,
                NativeTypeLayout.StorageSize<TFirst>(),
                NativeTypeLayout.Alignment<TFirst>(),
                firstContainsReferences: false,
                secondLength,
                NativeTypeLayout.StorageSize<TSecond>(),
                NativeTypeLayout.Alignment<TSecond>(),
                secondContainsReferences: false,
                thirdLength,
                NativeTypeLayout.StorageSize<TThird>(),
                NativeTypeLayout.Alignment<TThird>(),
                thirdContainsReferences: false,
                fourthLength,
                NativeTypeLayout.StorageSize<TFourth>(),
                NativeTypeLayout.Alignment<TFourth>(),
                fourthContainsReferences: false,
                sourceGeneration: sourceGeneration,
                sourceAllocation: sourceAllocation,
                sourceGenerationNumber: sourceGenerationNumber,
                sourceAllocationId: sourceAllocationId);
        try
        {
            initializer(
                source,
                reservations[0].Allocation.AsSpan<TFirst>(),
                reservations[1].Allocation.AsSpan<TSecond>(),
                reservations[2].Allocation.AsSpan<TThird>(),
                reservations[3].Allocation.AsSpan<TFourth>());
            arenaKernel.CompleteUnmanagedBumpInitializationGroup(
                ref reservations,
                usesSingleInitializationAdmission);
            first = new ArenaLease<TFirst>(
                arenaKernel,
                reservations[0].Lease);
            second = new ArenaLease<TSecond>(
                arenaKernel,
                reservations[1].Lease);
            third = new ArenaLease<TThird>(
                arenaKernel,
                reservations[2].Lease);
            fourth = new ArenaLease<TFourth>(
                arenaKernel,
                reservations[3].Lease);
        }
        catch
        {
            arenaKernel.AbortBumpInitializationGroup(
                ref reservations,
                usesSingleInitializationAdmission);
            throw;
        }
    }

    /// <summary>Initializes eight unmanaged scoped ranges from one arena source.</summary>
    public static void InitializeScoped<
        TSource,
        TFirst,
        TSecond,
        TThird,
        TFourth,
        TFifth,
        TSixth,
        TSeventh,
        TEighth>(
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
        NativeLeaseSourceOctupleSpanInitializer<
            TSource,
            TFirst,
            TSecond,
            TThird,
            TFourth,
            TFifth,
            TSixth,
            TSeventh,
            TEighth> initializer,
        out ArenaLease<TFirst> first,
        out ArenaLease<TSecond> second,
        out ArenaLease<TThird> third,
        out ArenaLease<TFourth> fourth,
        out ArenaLease<TFifth> fifth,
        out ArenaLease<TSixth> sixth,
        out ArenaLease<TSeventh> seventh,
        out ArenaLease<TEighth> eighth)
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

        NativeOwnerKernel arenaKernel =
            arena.KernelForInitialization;
        if (ReferenceEquals(
                source.KernelForComposite,
                arenaKernel))
        {
            NativeGeneration sourceGeneration =
                source.GenerationStateForComposite;
            NativeAllocation sourceAllocation =
                source.AllocationStateForComposite;
            InitializeScopedOctupleSpanCore(
                new NativeLeaseView<TSource>(
                    sourceAllocation),
                arena,
                firstLength,
                secondLength,
                thirdLength,
                fourthLength,
                fifthLength,
                sixthLength,
                seventhLength,
                eighthLength,
                initializer,
                out first,
                out second,
                out third,
                out fourth,
                out fifth,
                out sixth,
                out seventh,
                out eighth,
                sourceGeneration,
                sourceAllocation,
                source.GenerationForComposite,
                source.AllocationIdForComposite);
            return;
        }

        NativeOperationToken sourceToken =
            source.EnterForComposite(nameof(InitializeScoped));
        try
        {
            InitializeScopedOctupleSpanCore(
                sourceToken.GetView<TSource>(),
                arena,
                firstLength,
                secondLength,
                thirdLength,
                fourthLength,
                fifthLength,
                sixthLength,
                seventhLength,
                eighthLength,
                initializer,
                out first,
                out second,
                out third,
                out fourth,
                out fifth,
                out sixth,
                out seventh,
                out eighth);
        }
        finally
        {
            sourceToken.Dispose();
        }
    }

    private static void InitializeScopedOctupleSpanCore<
        TSource,
        TFirst,
        TSecond,
        TThird,
        TFourth,
        TFifth,
        TSixth,
        TSeventh,
        TEighth>(
        scoped NativeLeaseView<TSource> source,
        NativeArena arena,
        int firstLength,
        int secondLength,
        int thirdLength,
        int fourthLength,
        int fifthLength,
        int sixthLength,
        int seventhLength,
        int eighthLength,
        NativeLeaseSourceOctupleSpanInitializer<
            TSource,
            TFirst,
            TSecond,
            TThird,
            TFourth,
            TFifth,
            TSixth,
            TSeventh,
            TEighth> initializer,
        out ArenaLease<TFirst> first,
        out ArenaLease<TSecond> second,
        out ArenaLease<TThird> third,
        out ArenaLease<TFourth> fourth,
        out ArenaLease<TFifth> fifth,
        out ArenaLease<TSixth> sixth,
        out ArenaLease<TSeventh> seventh,
        out ArenaLease<TEighth> eighth,
        NativeGeneration? sourceGeneration = null,
        NativeAllocation? sourceAllocation = null,
        long sourceGenerationNumber = 0,
        long sourceAllocationId = 0)
        where TFirst : unmanaged
        where TSecond : unmanaged
        where TThird : unmanaged
        where TFourth : unmanaged
        where TFifth : unmanaged
        where TSixth : unmanaged
        where TSeventh : unmanaged
        where TEighth : unmanaged
    {
        NativeOwnerKernel arenaKernel = arena.KernelForInitialization;
        NativeBumpInitializationBuffer reservations = default;
        bool usesSingleInitializationAdmission =
            arenaKernel.BeginBumpInitializationOctet(
                ref reservations,
                firstLength,
                NativeTypeLayout.StorageSize<TFirst>(),
                NativeTypeLayout.Alignment<TFirst>(),
                firstContainsReferences: false,
                secondLength,
                NativeTypeLayout.StorageSize<TSecond>(),
                NativeTypeLayout.Alignment<TSecond>(),
                secondContainsReferences: false,
                thirdLength,
                NativeTypeLayout.StorageSize<TThird>(),
                NativeTypeLayout.Alignment<TThird>(),
                thirdContainsReferences: false,
                fourthLength,
                NativeTypeLayout.StorageSize<TFourth>(),
                NativeTypeLayout.Alignment<TFourth>(),
                fourthContainsReferences: false,
                fifthLength,
                NativeTypeLayout.StorageSize<TFifth>(),
                NativeTypeLayout.Alignment<TFifth>(),
                fifthContainsReferences: false,
                sixthLength,
                NativeTypeLayout.StorageSize<TSixth>(),
                NativeTypeLayout.Alignment<TSixth>(),
                sixthContainsReferences: false,
                seventhLength,
                NativeTypeLayout.StorageSize<TSeventh>(),
                NativeTypeLayout.Alignment<TSeventh>(),
                seventhContainsReferences: false,
                eighthLength,
                NativeTypeLayout.StorageSize<TEighth>(),
                NativeTypeLayout.Alignment<TEighth>(),
                eighthContainsReferences: false,
                sourceGeneration,
                sourceAllocation,
                sourceGenerationNumber,
                sourceAllocationId);
        try
        {
            initializer(
                source,
                reservations[0].Allocation.AsSpan<TFirst>(),
                reservations[1].Allocation.AsSpan<TSecond>(),
                reservations[2].Allocation.AsSpan<TThird>(),
                reservations[3].Allocation.AsSpan<TFourth>(),
                reservations[4].Allocation.AsSpan<TFifth>(),
                reservations[5].Allocation.AsSpan<TSixth>(),
                reservations[6].Allocation.AsSpan<TSeventh>(),
                reservations[7].Allocation.AsSpan<TEighth>());
            arenaKernel.CompleteUnmanagedBumpInitializationOctet(
                ref reservations,
                usesSingleInitializationAdmission);
            first = new ArenaLease<TFirst>(
                arenaKernel,
                reservations[0].Lease);
            second = new ArenaLease<TSecond>(
                arenaKernel,
                reservations[1].Lease);
            third = new ArenaLease<TThird>(
                arenaKernel,
                reservations[2].Lease);
            fourth = new ArenaLease<TFourth>(
                arenaKernel,
                reservations[3].Lease);
            fifth = new ArenaLease<TFifth>(
                arenaKernel,
                reservations[4].Lease);
            sixth = new ArenaLease<TSixth>(
                arenaKernel,
                reservations[5].Lease);
            seventh = new ArenaLease<TSeventh>(
                arenaKernel,
                reservations[6].Lease);
            eighth = new ArenaLease<TEighth>(
                arenaKernel,
                reservations[7].Lease);
        }
        catch
        {
            arenaKernel.AbortBumpInitializationOctet(
                ref reservations,
                usesSingleInitializationAdmission);
            throw;
        }
    }

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
        if (ReferenceEquals(firstKernel, secondKernel)
            && first.GenerationForComposite == second.GenerationForComposite
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
            NativeCompositeOperationToken token = firstKernel.EnterCompositeOperation(
                first.GenerationStateForComposite,
                allocations,
                first.GenerationForComposite,
                allocationIds,
                nameof(Access));
            try
            {
                action(token.GetView<TFirst>(0), token.GetView<TSecond>(1));
            }
            finally
            {
                token.Dispose();
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
    /// Enters two arena leases for one bounded callback. Same-owner leases use one
    /// failure-atomic composite admission.
    /// </summary>
    public static void Access<TFirst, TSecond>(
        scoped ArenaLease<TFirst> first,
        scoped ArenaLease<TSecond> second,
        NativeLeasePairAction<TFirst, TSecond> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        NativeOwnerKernel firstKernel = first.KernelForComposite;
        NativeOwnerKernel secondKernel = second.KernelForComposite;
        if (ReferenceEquals(firstKernel, secondKernel)
            && first.GenerationForComposite == second.GenerationForComposite
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
            NativeCompositeOperationToken token = firstKernel.EnterCompositeOperation(
                first.GenerationStateForComposite,
                allocations,
                first.GenerationForComposite,
                allocationIds,
                nameof(Access));
            try
            {
                action(token.GetView<TFirst>(0), token.GetView<TSecond>(1));
            }
            finally
            {
                token.Dispose();
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
        NativeOwnerKernel firstKernel = first.KernelForComposite;
        NativeOwnerKernel secondKernel = second.KernelForComposite;
        NativeOwnerKernel thirdKernel = third.KernelForComposite;
        if (ReferenceEquals(firstKernel, secondKernel)
            && ReferenceEquals(firstKernel, thirdKernel)
            && first.GenerationForComposite == second.GenerationForComposite
            && first.GenerationForComposite == third.GenerationForComposite
            && ReferenceEquals(
                first.GenerationStateForComposite,
                second.GenerationStateForComposite)
            && ReferenceEquals(
                first.GenerationStateForComposite,
                third.GenerationStateForComposite))
        {
            NativeCompositeAllocationBuffer allocations = default;
            allocations[0] = first.AllocationStateForComposite;
            allocations[1] = second.AllocationStateForComposite;
            allocations[2] = third.AllocationStateForComposite;
            Span<long> allocationIds = stackalloc long[3]
            {
                first.AllocationIdForComposite,
                second.AllocationIdForComposite,
                third.AllocationIdForComposite
            };
            NativeCompositeOperationToken token = firstKernel.EnterCompositeOperation(
                first.GenerationStateForComposite,
                allocations,
                first.GenerationForComposite,
                allocationIds,
                nameof(Access));
            try
            {
                action(
                    token.GetView<TFirst>(0),
                    token.GetView<TSecond>(1),
                    token.GetView<TThird>(2));
            }
            finally
            {
                token.Dispose();
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
    /// Enters three arena leases for one bounded callback. Same-owner leases use one
    /// failure-atomic composite admission.
    /// </summary>
    public static void Access<TFirst, TSecond, TThird>(
        scoped ArenaLease<TFirst> first,
        scoped ArenaLease<TSecond> second,
        scoped ArenaLease<TThird> third,
        NativeLeaseTripleAction<TFirst, TSecond, TThird> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        NativeOwnerKernel firstKernel =
            first.KernelForComposite;
        NativeGeneration firstGeneration =
            first.GenerationStateForComposite;
        long generationNumber =
            first.GenerationForComposite;
        if (ReferenceEquals(
                firstKernel,
                second.KernelForComposite)
            && ReferenceEquals(
                firstKernel,
                third.KernelForComposite)
            && generationNumber
                == second.GenerationForComposite
            && generationNumber
                == third.GenerationForComposite
            && ReferenceEquals(
                firstGeneration,
                second.GenerationStateForComposite)
            && ReferenceEquals(
                firstGeneration,
                third.GenerationStateForComposite))
        {
            NativeCompositeAllocationBuffer allocations =
                default;
            allocations[0] =
                first.AllocationStateForComposite;
            allocations[1] =
                second.AllocationStateForComposite;
            allocations[2] =
                third.AllocationStateForComposite;
            Span<long> allocationIds = stackalloc long[3]
            {
                first.AllocationIdForComposite,
                second.AllocationIdForComposite,
                third.AllocationIdForComposite
            };
            NativeCompositeOperationToken directToken =
                firstKernel.EnterCompositeOperation(
                    firstGeneration,
                    allocations,
                    generationNumber,
                    allocationIds,
                    nameof(Access));
            try
            {
                action(
                    directToken.GetView<TFirst>(0),
                    directToken.GetView<TSecond>(1),
                    directToken.GetView<TThird>(2));
            }
            finally
            {
                directToken.Dispose();
            }

            return;
        }

        NativeMultiOwnerOperationEntryBuffer entries = default;
        entries[0] = new(
            first.KernelForComposite,
            first.GenerationStateForComposite,
            first.AllocationStateForComposite,
            first.GenerationForComposite,
            first.AllocationIdForComposite);
        entries[1] = new(
            second.KernelForComposite,
            second.GenerationStateForComposite,
            second.AllocationStateForComposite,
            second.GenerationForComposite,
            second.AllocationIdForComposite);
        entries[2] = new(
            third.KernelForComposite,
            third.GenerationStateForComposite,
            third.AllocationStateForComposite,
            third.GenerationForComposite,
            third.AllocationIdForComposite);
        NativeMultiOwnerOperationToken token = new(
            ref entries,
            3,
            nameof(Access));
        try
        {
            action(
                token.GetView<TFirst>(0),
                token.GetView<TSecond>(1),
                token.GetView<TThird>(2));
        }
        finally
        {
            token.Dispose();
        }
    }

    /// <summary>Enters four arena leases for one bounded callback.</summary>
    public static void Access<TFirst, TSecond, TThird, TFourth>(
        scoped ArenaLease<TFirst> first,
        scoped ArenaLease<TSecond> second,
        scoped ArenaLease<TThird> third,
        scoped ArenaLease<TFourth> fourth,
        NativeLeaseQuadrupleAction<
            TFirst,
            TSecond,
            TThird,
            TFourth> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        NativeOwnerKernel firstKernel =
            first.KernelForComposite;
        NativeGeneration firstGeneration =
            first.GenerationStateForComposite;
        long generationNumber =
            first.GenerationForComposite;
        if (ReferenceEquals(
                firstKernel,
                second.KernelForComposite)
            && ReferenceEquals(
                firstKernel,
                third.KernelForComposite)
            && ReferenceEquals(
                firstKernel,
                fourth.KernelForComposite)
            && generationNumber
                == second.GenerationForComposite
            && generationNumber
                == third.GenerationForComposite
            && generationNumber
                == fourth.GenerationForComposite
            && ReferenceEquals(
                firstGeneration,
                second.GenerationStateForComposite)
            && ReferenceEquals(
                firstGeneration,
                third.GenerationStateForComposite)
            && ReferenceEquals(
                firstGeneration,
                fourth.GenerationStateForComposite))
        {
            NativeCompositeAllocationBuffer allocations =
                default;
            allocations[0] =
                first.AllocationStateForComposite;
            allocations[1] =
                second.AllocationStateForComposite;
            allocations[2] =
                third.AllocationStateForComposite;
            allocations[3] =
                fourth.AllocationStateForComposite;
            Span<long> allocationIds = stackalloc long[4]
            {
                first.AllocationIdForComposite,
                second.AllocationIdForComposite,
                third.AllocationIdForComposite,
                fourth.AllocationIdForComposite
            };
            NativeCompositeOperationToken directToken =
                firstKernel.EnterCompositeOperation(
                    firstGeneration,
                    allocations,
                    generationNumber,
                    allocationIds,
                    nameof(Access));
            try
            {
                action(
                    directToken.GetView<TFirst>(0),
                    directToken.GetView<TSecond>(1),
                    directToken.GetView<TThird>(2),
                    directToken.GetView<TFourth>(3));
            }
            finally
            {
                directToken.Dispose();
            }

            return;
        }

        NativeMultiOwnerOperationEntryBuffer entries = default;
        entries[0] = new(
            first.KernelForComposite,
            first.GenerationStateForComposite,
            first.AllocationStateForComposite,
            first.GenerationForComposite,
            first.AllocationIdForComposite);
        entries[1] = new(
            second.KernelForComposite,
            second.GenerationStateForComposite,
            second.AllocationStateForComposite,
            second.GenerationForComposite,
            second.AllocationIdForComposite);
        entries[2] = new(
            third.KernelForComposite,
            third.GenerationStateForComposite,
            third.AllocationStateForComposite,
            third.GenerationForComposite,
            third.AllocationIdForComposite);
        entries[3] = new(
            fourth.KernelForComposite,
            fourth.GenerationStateForComposite,
            fourth.AllocationStateForComposite,
            fourth.GenerationForComposite,
            fourth.AllocationIdForComposite);
        NativeMultiOwnerOperationToken token = new(
            ref entries,
            4,
            nameof(Access));
        try
        {
            action(
                token.GetView<TFirst>(0),
                token.GetView<TSecond>(1),
                token.GetView<TThird>(2),
                token.GetView<TFourth>(3));
        }
        finally
        {
            token.Dispose();
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
        NativeOperationToken pooledToken =
            pooled.EnterForComposite(nameof(Access));
        try
        {
            NativeOperationToken arenaToken =
                arena.EnterForComposite(nameof(Access));
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

    /// <summary>Enters one pooled lease and two same-owner arena leases.</summary>
    public static void Access<TPooled, TFirst, TSecond>(
        scoped Pooled<TPooled> pooled,
        scoped ArenaLease<TFirst> first,
        scoped ArenaLease<TSecond> second,
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
                        pooledToken.GetView<TPooled>(),
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

    /// <summary>
    /// Enters five arena leases for one bounded callback. Same-owner leases use one
    /// failure-atomic composite admission.
    /// </summary>
    public static void Access<TFirst, TSecond, TThird, TFourth, TFifth>(
        scoped ArenaLease<TFirst> first,
        scoped ArenaLease<TSecond> second,
        scoped ArenaLease<TThird> third,
        scoped ArenaLease<TFourth> fourth,
        scoped ArenaLease<TFifth> fifth,
        NativeLeaseQuintupleAction<TFirst, TSecond, TThird, TFourth, TFifth> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        NativeMultiOwnerOperationEntryBuffer entries = default;
        entries[0] = new(
            first.KernelForComposite,
            first.GenerationStateForComposite,
            first.AllocationStateForComposite,
            first.GenerationForComposite,
            first.AllocationIdForComposite);
        entries[1] = new(
            second.KernelForComposite,
            second.GenerationStateForComposite,
            second.AllocationStateForComposite,
            second.GenerationForComposite,
            second.AllocationIdForComposite);
        entries[2] = new(
            third.KernelForComposite,
            third.GenerationStateForComposite,
            third.AllocationStateForComposite,
            third.GenerationForComposite,
            third.AllocationIdForComposite);
        entries[3] = new(
            fourth.KernelForComposite,
            fourth.GenerationStateForComposite,
            fourth.AllocationStateForComposite,
            fourth.GenerationForComposite,
            fourth.AllocationIdForComposite);
        entries[4] = new(
            fifth.KernelForComposite,
            fifth.GenerationStateForComposite,
            fifth.AllocationStateForComposite,
            fifth.GenerationForComposite,
            fifth.AllocationIdForComposite);
        NativeMultiOwnerOperationToken token = new(
            ref entries,
            5,
            nameof(Access));
        try
        {
            action(
                token.GetView<TFirst>(0),
                token.GetView<TSecond>(1),
                token.GetView<TThird>(2),
                token.GetView<TFourth>(3),
                token.GetView<TFifth>(4));
        }
        finally
        {
            token.Dispose();
        }
    }

    /// <summary>Enters seven arena leases for one bounded callback.</summary>
    public static void Access<
        TFirst,
        TSecond,
        TThird,
        TFourth,
        TFifth,
        TSixth,
        TSeventh>(
        scoped ArenaLease<TFirst> first,
        scoped ArenaLease<TSecond> second,
        scoped ArenaLease<TThird> third,
        scoped ArenaLease<TFourth> fourth,
        scoped ArenaLease<TFifth> fifth,
        scoped ArenaLease<TSixth> sixth,
        scoped ArenaLease<TSeventh> seventh,
        NativeLeaseSeptupleAction<
            TFirst,
            TSecond,
            TThird,
            TFourth,
            TFifth,
            TSixth,
            TSeventh> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        NativeMultiOwnerOperationEntryBuffer entries = default;
        entries[0] = new(
            first.KernelForComposite,
            first.GenerationStateForComposite,
            first.AllocationStateForComposite,
            first.GenerationForComposite,
            first.AllocationIdForComposite);
        entries[1] = new(
            second.KernelForComposite,
            second.GenerationStateForComposite,
            second.AllocationStateForComposite,
            second.GenerationForComposite,
            second.AllocationIdForComposite);
        entries[2] = new(
            third.KernelForComposite,
            third.GenerationStateForComposite,
            third.AllocationStateForComposite,
            third.GenerationForComposite,
            third.AllocationIdForComposite);
        entries[3] = new(
            fourth.KernelForComposite,
            fourth.GenerationStateForComposite,
            fourth.AllocationStateForComposite,
            fourth.GenerationForComposite,
            fourth.AllocationIdForComposite);
        entries[4] = new(
            fifth.KernelForComposite,
            fifth.GenerationStateForComposite,
            fifth.AllocationStateForComposite,
            fifth.GenerationForComposite,
            fifth.AllocationIdForComposite);
        entries[5] = new(
            sixth.KernelForComposite,
            sixth.GenerationStateForComposite,
            sixth.AllocationStateForComposite,
            sixth.GenerationForComposite,
            sixth.AllocationIdForComposite);
        entries[6] = new(
            seventh.KernelForComposite,
            seventh.GenerationStateForComposite,
            seventh.AllocationStateForComposite,
            seventh.GenerationForComposite,
            seventh.AllocationIdForComposite);
        NativeMultiOwnerOperationToken token = new(
            ref entries,
            7,
            nameof(Access));
        try
        {
            action(
                token.GetView<TFirst>(0),
                token.GetView<TSecond>(1),
                token.GetView<TThird>(2),
                token.GetView<TFourth>(3),
                token.GetView<TFifth>(4),
                token.GetView<TSixth>(5),
                token.GetView<TSeventh>(6));
        }
        finally
        {
            token.Dispose();
        }
    }

    /// <summary>Enters eight arena leases for one bounded callback.</summary>
    public static void Access<
        TFirst,
        TSecond,
        TThird,
        TFourth,
        TFifth,
        TSixth,
        TSeventh,
        TEighth>(
        scoped ArenaLease<TFirst> first,
        scoped ArenaLease<TSecond> second,
        scoped ArenaLease<TThird> third,
        scoped ArenaLease<TFourth> fourth,
        scoped ArenaLease<TFifth> fifth,
        scoped ArenaLease<TSixth> sixth,
        scoped ArenaLease<TSeventh> seventh,
        scoped ArenaLease<TEighth> eighth,
        NativeLeaseOctupleAction<
            TFirst,
            TSecond,
            TThird,
            TFourth,
            TFifth,
            TSixth,
            TSeventh,
            TEighth> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        NativeOwnerKernel firstKernel =
            first.KernelForComposite;
        NativeGeneration firstGeneration =
            first.GenerationStateForComposite;
        long generationNumber =
            first.GenerationForComposite;
        if (ReferenceEquals(
                firstGeneration,
                second.GenerationStateForComposite)
            && ReferenceEquals(
                firstGeneration,
                third.GenerationStateForComposite)
            && ReferenceEquals(
                firstGeneration,
                fourth.GenerationStateForComposite)
            && ReferenceEquals(
                firstGeneration,
                fifth.GenerationStateForComposite)
            && ReferenceEquals(
                firstGeneration,
                sixth.GenerationStateForComposite)
            && ReferenceEquals(
                firstGeneration,
                seventh.GenerationStateForComposite)
            && ReferenceEquals(
                firstGeneration,
                eighth.GenerationStateForComposite))
        {
            NativeCompositeAllocationBuffer allocations =
                default;
            allocations[0] =
                first.AllocationStateForComposite;
            allocations[1] =
                second.AllocationStateForComposite;
            allocations[2] =
                third.AllocationStateForComposite;
            allocations[3] =
                fourth.AllocationStateForComposite;
            allocations[4] =
                fifth.AllocationStateForComposite;
            allocations[5] =
                sixth.AllocationStateForComposite;
            allocations[6] =
                seventh.AllocationStateForComposite;
            allocations[7] =
                eighth.AllocationStateForComposite;
            Span<long> allocationIds = stackalloc long[8]
            {
                first.AllocationIdForComposite,
                second.AllocationIdForComposite,
                third.AllocationIdForComposite,
                fourth.AllocationIdForComposite,
                fifth.AllocationIdForComposite,
                sixth.AllocationIdForComposite,
                seventh.AllocationIdForComposite,
                eighth.AllocationIdForComposite
            };
            NativeCompositeOperationToken directToken =
                firstKernel.EnterCompositeOperation(
                    firstGeneration,
                    allocations,
                    generationNumber,
                    allocationIds,
                    nameof(Access));
            try
            {
                action(
                    directToken.GetView<TFirst>(0),
                    directToken.GetView<TSecond>(1),
                    directToken.GetView<TThird>(2),
                    directToken.GetView<TFourth>(3),
                    directToken.GetView<TFifth>(4),
                    directToken.GetView<TSixth>(5),
                    directToken.GetView<TSeventh>(6),
                    directToken.GetView<TEighth>(7));
            }
            finally
            {
                directToken.Dispose();
            }

            return;
        }

        NativeMultiOwnerOperationEntryBuffer entries = default;
        entries[0] = new(
            first.KernelForComposite,
            first.GenerationStateForComposite,
            first.AllocationStateForComposite,
            first.GenerationForComposite,
            first.AllocationIdForComposite);
        entries[1] = new(
            second.KernelForComposite,
            second.GenerationStateForComposite,
            second.AllocationStateForComposite,
            second.GenerationForComposite,
            second.AllocationIdForComposite);
        entries[2] = new(
            third.KernelForComposite,
            third.GenerationStateForComposite,
            third.AllocationStateForComposite,
            third.GenerationForComposite,
            third.AllocationIdForComposite);
        entries[3] = new(
            fourth.KernelForComposite,
            fourth.GenerationStateForComposite,
            fourth.AllocationStateForComposite,
            fourth.GenerationForComposite,
            fourth.AllocationIdForComposite);
        entries[4] = new(
            fifth.KernelForComposite,
            fifth.GenerationStateForComposite,
            fifth.AllocationStateForComposite,
            fifth.GenerationForComposite,
            fifth.AllocationIdForComposite);
        entries[5] = new(
            sixth.KernelForComposite,
            sixth.GenerationStateForComposite,
            sixth.AllocationStateForComposite,
            sixth.GenerationForComposite,
            sixth.AllocationIdForComposite);
        entries[6] = new(
            seventh.KernelForComposite,
            seventh.GenerationStateForComposite,
            seventh.AllocationStateForComposite,
            seventh.GenerationForComposite,
            seventh.AllocationIdForComposite);
        entries[7] = new(
            eighth.KernelForComposite,
            eighth.GenerationStateForComposite,
            eighth.AllocationStateForComposite,
            eighth.GenerationForComposite,
            eighth.AllocationIdForComposite);
        NativeMultiOwnerOperationToken token = new(
            ref entries,
            8,
            nameof(Access));
        try
        {
            action(
                token.GetView<TFirst>(0),
                token.GetView<TSecond>(1),
                token.GetView<TThird>(2),
                token.GetView<TFourth>(3),
                token.GetView<TFifth>(4),
                token.GetView<TSixth>(5),
                token.GetView<TSeventh>(6),
                token.GetView<TEighth>(7));
        }
        finally
        {
            token.Dispose();
        }
    }

    /// <summary>
    /// Enters one typed-pool lease and four heterogeneous arena leases for one
    /// bounded callback. When all arena leases share an owner generation, their
    /// admission is failure-atomic and uses one composite owner entry.
    /// </summary>
    public static void Access<TPooled, TFirst, TSecond, TThird, TFourth>(
        scoped Pooled<TPooled> pooled,
        scoped ArenaLease<TFirst> first,
        scoped ArenaLease<TSecond> second,
        scoped ArenaLease<TThird> third,
        scoped ArenaLease<TFourth> fourth,
        NativeLeaseQuintupleAction<
            TPooled,
            TFirst,
            TSecond,
            TThird,
            TFourth> action)
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
        NativeOperationToken pooledToken =
            pooled.EnterForComposite(nameof(Access));
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
                        pooledToken.GetView<TPooled>(),
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

    /// <summary>Enters three typed leases and one arena lease for one callback.</summary>
    public static void Access<TFirst, TSecond, TThird, TFourth>(
        scoped Pooled<TFirst> first,
        scoped Pooled<TSecond> second,
        scoped Pooled<TThird> third,
        scoped ArenaLease<TFourth> fourth,
        NativeLeaseQuadrupleAction<TFirst, TSecond, TThird, TFourth> action)
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

    /// <summary>
    /// Enters one fixed five-view composition for a bounded callback. The first three
    /// parameters are typed-pool leases and the fourth and fifth parameters are arena
    /// leases. This reusable shape is intended for a stage with three stable repeated
    /// buffers and two heterogeneous ranges; use the pair, triple, or pooled-arena
    /// overloads when a different backing composition is required. The callback receives
    /// only direct native views and all operation tokens are released in reverse order.
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
        NativeOwnerKernel fourthKernel = fourth.KernelForComposite;
        NativeOwnerKernel fifthKernel = fifth.KernelForComposite;
        bool compositeArenaEntry = ReferenceEquals(fourthKernel, fifthKernel)
            && fourth.GenerationForComposite == fifth.GenerationForComposite
            && ReferenceEquals(
                fourth.GenerationStateForComposite,
                fifth.GenerationStateForComposite);
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
                                firstToken.GetView<TFirst>(),
                                secondToken.GetView<TSecond>(),
                                thirdToken.GetView<TThird>(),
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
