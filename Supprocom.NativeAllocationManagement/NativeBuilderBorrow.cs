namespace Supprocom.NativeAllocationManagement;

/// <summary>Provides one exclusive callback-bounded builder authority.</summary>
/// <typeparam name="T">The unmanaged element type.</typeparam>
public readonly ref struct NativeBuilderBorrow<T>
    where T : unmanaged
{
    private readonly NativeBuilder<T> _builder;
    private readonly int _authority;

    internal NativeBuilderBorrow(
        NativeBuilder<T> builder,
        int authority)
    {
        _builder = builder;
        _authority = authority;
    }

    /// <summary>Gets the initialized element count.</summary>
    public int Count => _builder.ReadBorrowedState(
        _authority,
        readCapacity: false);

    /// <summary>Gets the current native element capacity.</summary>
    public int Capacity => _builder.ReadBorrowedState(
        _authority,
        readCapacity: true);

    internal int AuthorityForTest => _authority;

    /// <summary>Appends one value through the exclusive borrow.</summary>
    public void Append(
        T value,
        CancellationToken cancellationToken = default) =>
        _builder.AppendBorrowed(
            _authority,
            value,
            cancellationToken);

    /// <summary>Appends one range through the exclusive borrow.</summary>
    public void Append(
        scoped ReadOnlySpan<T> source,
        CancellationToken cancellationToken = default) =>
        _builder.AppendBorrowed(
            _authority,
            source,
            cancellationToken);

    /// <summary>Writes one bounded range through the exclusive borrow.</summary>
    public void Write(
        int maximumAdditionalCount,
        NativeBuilderWriteAction<T> action,
        CancellationToken cancellationToken = default) =>
        _builder.WriteBorrowed(
            _authority,
            maximumAdditionalCount,
            action,
            cancellationToken);
}

/// <summary>Uses one exclusive builder borrow during a bounded callback.</summary>
/// <typeparam name="T">The unmanaged element type.</typeparam>
public delegate void NativeBuilderBorrowAction<T>(
    scoped ref NativeBuilderBorrow<T> builder)
    where T : unmanaged;
