using Bosun.Core.Optimise;
using Bosun.Panel.Shell;

namespace Bosun.Panel.Optimise;

/// <summary>
/// Adapts the change framework's <see cref="IChangeLog"/> (<c>Bosun.Core</c>, which has no Activity pane) to the
/// panel's <see cref="ActivityLog"/> (D05 T02 §2): each step is one line, INFO, WARN or FAIL by its level, saying the
/// item, the step, the exact target, the outcome and the backup's name.
/// </summary>
public sealed class ActivityChangeLog(ActivityLog activity) : IChangeLog
{
    public void Step(ChangeStep step)
    {
        var line = $"{step.Title}, {step.Step} ({step.Target}): {step.Outcome}";
        if (step.Backup is not null) line += $" [backup {step.Backup}]";
        switch (step.Level)
        {
            case ChangeLevel.Fail:
                activity.Fail(line);
                break;
            case ChangeLevel.Warn:
                activity.Warn(line);
                break;
            default:
                activity.Info(line);
                break;
        }
    }
}
