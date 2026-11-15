using System.Windows.Threading;
using Bosun.Panel.Startup;

// The program the panel's startup tests start. Each verb begins the panel's own startup wiring (PanelStartup, as
// App does, with a host that shows nothing) and then does one thing a real panel could do to itself:
//
//   crash-thread   throw on a thread that is not the UI thread, which ends the process
//
// The data folder and the instance name come from BOSUN_PANEL_DATA and BOSUN_PANEL_INSTANCE, which the test sets, so
// nothing of the person's own is touched. Whatever happens, the process ends by itself within ten seconds.
static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args is not ["crash-thread"])
        {
            Console.Error.WriteLine("usage: Bosun.Panel.TestChild crash-thread");
            return 64;
        }

        using var startup = new PanelStartup(new StartupHost
        {
            Dispatcher = Dispatcher.CurrentDispatcher,
            DataVariable = Environment.GetEnvironmentVariable(PanelPaths.Variable),
            InstanceVariable = Environment.GetEnvironmentVariable(InstanceName.Variable),
            MakeWindow = () => { },
            BringForward = () => { },
            Tell = _ => { },
            Ask = _ => false,
            Restart = () => { },
            Exit = Environment.Exit,
        });
        if (!startup.Begin()) return 65;

        var thread = new Thread(() => throw new InvalidOperationException("thrown on a background thread by the test child")) { IsBackground = true };
        thread.Start();
        // Not reached past the throw: an exception nobody caught on any thread ends the process.
        thread.Join(TimeSpan.FromSeconds(10));
        return 66;
    }
}
