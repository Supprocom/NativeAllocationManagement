namespace Supprocom.NativeAllocationManagement.Tests;

// Runs an action on a background thread that ends before Run returns, and
// rethrows its failure on the calling thread. NAM releases an ended thread's
// kept storage (NativeEndedThreads).
internal static class TestThread
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "The worker failure is rethrown on the test thread.")]
    internal static NativeThreadState Run(Action action)
    {
        NativeThreadState? state = null;
        Exception? failure = null;
        Thread thread = new(() =>
        {
            state = NativeThreadState.Current;
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            IsBackground = true
        };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return state!;
    }
}
