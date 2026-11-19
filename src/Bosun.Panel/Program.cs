using Bosun.Core.Optimise;
using Bosun.Panel.Optimise;

namespace Bosun.Panel;

/// <summary>
/// The panel's entry point (D05 T02 §6). Helper mode is chosen here, before <see cref="App"/> is made, so before the
/// panel's crash handlers, its single-instance claim or any window exist: <c>--optimise</c> runs the elevated helper
/// headless and exits with its code. A fault in it is written to its result, never shown in a dialog, never restarts
/// the panel and never reaches the panel that is already running.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (ElevatedHelper.IsHelper(args)) return RunHelper(args);

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    private static int RunHelper(string[] args)
    {
        try
        {
            return ElevatedHelper.Run(args, new WindowsHelperStanding(Environment.GetEnvironmentVariable), PanelOptimisations.Registry(Environment.GetEnvironmentVariable));
        }
        // The helper's last resort: the engine records its own faults in the result, so this is a fault before there is
        // one to write to (the account list, the registry). It is an exit code; a dialog or a crash report here would be
        // shown to the person on the wrong desktop (the elevated one).
        catch (Exception)
        {
            return ElevatedHelper.ExitFaulted;
        }
    }
}
