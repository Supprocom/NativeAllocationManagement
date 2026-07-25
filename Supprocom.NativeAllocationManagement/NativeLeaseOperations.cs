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

/// <summary>Runs one bounded operation over three pooled views and two arena views.</summary>
public delegate void NativeLeaseMeshAction<TFace, TVertex, TIndex, TSlice, TByte>(
    scoped NativeLeaseView<TFace> faces,
    scoped NativeLeaseView<TVertex> vertices,
    scoped NativeLeaseView<TIndex> indices,
    scoped NativeLeaseView<TSlice> slices,
    scoped NativeLeaseView<TByte> upload);

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
    /// Enters the complete native mesh output set for one bounded callback. The face,
    /// vertex, and index buffers are typed pool leases; heterogeneous slice descriptors
    /// and upload bytes are arena leases. Every view is direct native storage.
    /// </summary>
    public static void Access<TFace, TVertex, TIndex, TSlice, TByte>(
        scoped Pooled<TFace> faces,
        scoped Pooled<TVertex> vertices,
        scoped Pooled<TIndex> indices,
        scoped ArenaLease<TSlice> slices,
        scoped ArenaLease<TByte> upload,
        NativeLeaseMeshAction<TFace, TVertex, TIndex, TSlice, TByte> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        NativeOperationToken faceToken = faces.KernelForComposite.EnterOperation(
            faces.GenerationForComposite,
            faces.AllocationIdForComposite,
            nameof(Access));
        try
        {
            NativeOperationToken vertexToken = vertices.KernelForComposite.EnterOperation(
                vertices.GenerationForComposite,
                vertices.AllocationIdForComposite,
                nameof(Access));
            try
            {
                NativeOperationToken indexToken = indices.KernelForComposite.EnterOperation(
                    indices.GenerationForComposite,
                    indices.AllocationIdForComposite,
                    nameof(Access));
                try
                {
                    NativeOperationToken sliceToken = slices.KernelForComposite.EnterOperation(
                        slices.GenerationForComposite,
                        slices.AllocationIdForComposite,
                        nameof(Access));
                    try
                    {
                        NativeOperationToken uploadToken = upload.KernelForComposite.EnterOperation(
                            upload.GenerationForComposite,
                            upload.AllocationIdForComposite,
                            nameof(Access));
                        try
                        {
                            action(
                                faceToken.GetView<TFace>(),
                                vertexToken.GetView<TVertex>(),
                                indexToken.GetView<TIndex>(),
                                sliceToken.GetView<TSlice>(),
                                uploadToken.GetView<TByte>());
                        }
                        finally
                        {
                            uploadToken.Dispose();
                        }
                    }
                    finally
                    {
                        sliceToken.Dispose();
                    }
                }
                finally
                {
                    indexToken.Dispose();
                }
            }
            finally
            {
                vertexToken.Dispose();
            }
        }
        finally
        {
            faceToken.Dispose();
        }
    }
}
