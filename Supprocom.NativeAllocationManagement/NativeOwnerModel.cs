namespace Supprocom.NativeAllocationManagement;

/// <summary>Identifies the actual storage and lifecycle model behind an owner snapshot.</summary>
public enum NativeOwnerModel
{
    /// <summary>No runtime owner supplied this default snapshot.</summary>
    Unspecified,
    /// <summary>Unmanaged typed slabs with thread-confined lease tokens, without generations.</summary>
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
    SynchronizedArena
}
