namespace Supprocom.NativeAllocationManagement;

/// <summary>Provides one exclusive callback-bounded builder authority.</summary>
/// <typeparam name="T">The unmanaged element type.</typeparam>
public readonly ref struct NativeBuilderBorrow<T>
    where T : unmanaged
{
    private readonly NativeBuilder<T> _builder;

    internal NativeBuilderBorrow(NativeBuilder<T> builder)
    {
        _builder = builder;
    }

    /// <summary>Gets the initialized element count.</summary>
    public int Count => _builder.ReadBorrowedState(
        readCapacity: false);

    /// <summary>Gets the current native element capacity.</summary>
    public int Capacity => _builder.ReadBorrowedState(
        readCapacity: true);

    /// <summary>Appends one value through the exclusive borrow.</summary>
    public void Append(
        T value,
        CancellationToken cancellationToken = default) =>
        _builder.AppendBorrowed(
            value,
            cancellationToken);

    /// <summary>Appends one range through the exclusive borrow.</summary>
    public void Append(
        scoped ReadOnlySpan<T> source,
        CancellationToken cancellationToken = default) =>
        _builder.AppendBorrowed(
            source,
            cancellationToken);

    /// <summary>Writes one bounded range through the exclusive borrow.</summary>
    public void Write(
        int maximumAdditionalCount,
        NativeBuilderWriteAction<T> action,
        CancellationToken cancellationToken = default) =>
        _builder.WriteBorrowed(
            maximumAdditionalCount,
            action,
            cancellationToken);
}

/// <summary>Uses one exclusive builder borrow during a bounded callback.</summary>
/// <typeparam name="T">The unmanaged element type.</typeparam>
public delegate void NativeBuilderBorrowAction<T>(
    scoped ref NativeBuilderBorrow<T> builder)
    where T : unmanaged;

/// <summary>Uses two exclusive builder borrows during one bounded callback.</summary>
/// <typeparam name="T">The unmanaged element type.</typeparam>
public delegate void NativeBuilderPairBorrowAction<T>(
    scoped ref NativeBuilderBorrow<T> first,
    scoped ref NativeBuilderBorrow<T> second)
    where T : unmanaged;
