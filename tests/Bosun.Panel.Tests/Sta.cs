using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace Bosun.Panel.Tests;

/// <summary>The single-threaded apartment WPF needs, for tests: one helper, used wherever a test touches WPF.</summary>
static class Sta
{
    /// <summary>
    /// Runs <paramref name="work"/> on a thread of its own in a single-threaded apartment and returns what it returned.
    /// What it throws reaches the caller as itself, with its own message and stack. The thread's dispatcher is shut
    /// down when the work ends, so nothing it queued outlives the test.
    /// </summary>
    public static T Run<T>(Func<T> work)
    {
        TryRun(work, Timeout.InfiniteTimeSpan, out var result);
        return result;
    }

    /// <summary>
    /// As <see cref="Run{T}(Func{T})"/>, but gives up after <paramref name="limit"/>: false when the work had not
    /// ended by then. Work that blocks its own thread can't be stopped from outside, so that thread is left behind
    /// (a background thread, which never holds the process open) and the caller carries on.
    /// </summary>
    public static bool TryRun<T>(Func<T> work, TimeSpan limit, out T result)
    {
        result = default!;
        T made = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                made = work();
            }
            catch (Exception e)
            {
                failure = ExceptionDispatchInfo.Capture(e);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        })
        { IsBackground = true, Name = "bosun test STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(limit)) return false;
        failure?.Throw();
        result = made;
        return true;
    }

    public static void Run(Action work) => Run(() =>
    {
        work();
        return true;
    });
}
