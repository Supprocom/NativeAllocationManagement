namespace Supprocom.NativeAllocationManagement;

/// <summary>Identifies the actual storage and lifecycle model behind an owner snapshot.</summary>
public enum NativeOwnerModel
{
    /// <summary>No runtime owner supplied this default snapshot.</summary>
    Unspecified,
    /// <summary>Unmanaged typed slots backed by slabs or prepared pages, with thread-confined tokens and no generations.</summary>
    ThreadConfinedPool,
    /// <summary>Thread-confined bump storage with one lexical lifetime.</summary>
    ThreadConfinedRegion,
    /// <summary>Thread-confined ordinary and scoped bump lanes with checked epochs.</summary>
    ThreadConfinedArena,
    /// <summary>Synchronized typed storage with generations and managed-root support.</summary>
    SynchronizedPool,
    /// <summary>Synchronized lexical bump storage used by the runtime kernel.</summary>
    SynchronizedRegion,
    /// <summary>Synchronized ordinary/scoped bump storage with generations and fast lanes.</summary>
    SynchronizedArena,
    /// <summary>One growable unmanaged block with exclusive builder admission and destructive completion.</summary>
    SingleWriterBuilder,
    /// <summary>One fixed zero-initialized unmanaged block with thread-confined bounded use.</summary>
    ThreadConfinedWorkspace
}
