namespace Supprocom.NativeAllocationManagement;

/// <summary>Performs a bounded mutation while a NAM operation token is held.</summary>
public delegate void NativeLeaseAction<T>(scoped NativeLeaseView<T> view);

/// <summary>Performs a bounded read while a NAM operation token is held.</summary>
public delegate TResult NativeLeaseFunc<T, TResult>(scoped NativeLeaseView<T> view);
