namespace Supprocom.NativeAllocationManagement.Tests;

internal static class SingleExpectedExtensions
{
    internal static T SingleExpected<T>(IEnumerable<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        using IEnumerator<T> enumerator = source.GetEnumerator();
        Assert.True(enumerator.MoveNext(), "Expected exactly one item, but found none.");
        T value = enumerator.Current;
        Assert.False(enumerator.MoveNext(), "Expected exactly one item, but found more than one.");
        return value;
    }

    internal static T SingleExpected<T>(IEnumerable<T> source, Func<T, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return SingleExpected(source.Where(predicate));
    }
}
