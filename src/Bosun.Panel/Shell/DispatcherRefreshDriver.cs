using System.Windows.Threading;
using Bosun.Core;

namespace Bosun.Panel.Shell;

/// <summary>
/// What ticks the panel's refresh scheduler (D02 T02 §8): one timer on the UI thread, running only while the
/// scheduler has something active. With every page hidden and nothing else registered, the timer is off and the
/// panel does nothing at all (design/SPEC.md §1: a hidden page polls nothing).
/// </summary>
public sealed class DispatcherRefreshDriver : IRefreshDriver
{
    /// <summary>How often the scheduler is asked what is due. The shortest interval is 5 s, so a second is fine enough.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(1);

    private readonly DispatcherTimer _timer;
    private Action? _tick;

    public DispatcherRefreshDriver(Dispatcher dispatcher, TimeSpan? every = null)
    {
        // Background priority: a tick never comes before input or drawing.
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = every ?? Every };
        _timer.Tick += (_, _) => _tick?.Invoke();
    }

    /// <summary>Whether the timer is running now.</summary>
    public bool IsTicking => _timer.IsEnabled;

    public void Start(Action tick)
    {
        _tick = tick;
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        _tick = null;
    }
}
