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
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
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
        thread.Join();
        failure?.Throw();
        return result;
    }

    public static void Run(Action work) => Run(() =>
    {
        work();
        return true;
    });
}
