using System.IO;
using Xunit;

namespace Bosun.Panel.Tests.Tray;

/// <summary>Rules over the application's own source (D02 T02 §7): the tray is made with the real icon, and let go on exit.</summary>
public class TrayRulesTests
{
    [Fact]
    public void The_application_makes_the_tray_with_the_real_icon_starts_it_with_its_own_arguments_and_removes_it_on_exit()
    {
        var app = File.ReadAllText(Path.Combine(TestPaths.Panel(), "App.xaml.cs"));
        var make = app[app.IndexOf("private void MakeWindow()", StringComparison.Ordinal)..];
        make = make[..make.IndexOf("/// <summary>", StringComparison.Ordinal)];

        Assert.Contains("new TrayController(made, new NativeTrayIcon(new Win32TrayShell()), new Notifier(settings, made.Log), settings, Shutdown, queue: queueWords, refreshes: refreshes)", make, StringComparison.Ordinal);
        // The tray's Queue line is the Queue page's own words (D03 T02 §3), not "Not available" for ever.
        Assert.Contains("OfType<Bosun.Panel.Queue.QueuePage>().FirstOrDefault()?.Words", make, StringComparison.Ordinal);
        Assert.Contains("queue: Bosun.Core.Queue.QueueClient.ForThisUser()", make, StringComparison.Ordinal);
        Assert.Contains("TakeClose = tray.TakeClose", make, StringComparison.Ordinal);                 // the window's close asks the tray first
        Assert.Contains("window.Closed += (_, _) => Shutdown();", make, StringComparison.Ordinal);     // and a window that really closed ends the panel
        Assert.Contains("var inTray = TrayController.StartsInTray(Environment.GetCommandLineArgs());", make, StringComparison.Ordinal);
        Assert.Contains("windowShown: !inTray", make, StringComparison.Ordinal); // no page is on screen in a window that is not
        Assert.Contains("tray.Start(window, inTray, activates);", make, StringComparison.Ordinal);
        // The memory pressure check starts after the tray, so its notification has somewhere to go (D03 T01 §4).
        var check = make.IndexOf("tray.StartPressureCheck(", StringComparison.Ordinal);
        Assert.True(check > make.IndexOf("tray.Start(window, inTray, activates);", StringComparison.Ordinal), "the pressure check is started after the tray");

        // Closing the window no longer ends the application by itself.
        Assert.Contains("ShutdownMode = ShutdownMode.OnExplicitShutdown;", app, StringComparison.Ordinal);

        // However the panel ends, the icon is removed.
        var exit = app[app.IndexOf("protected override void OnExit", StringComparison.Ordinal)..];
        Assert.Contains("_tray?.Dispose()", exit[..exit.IndexOf('}')], StringComparison.Ordinal);
    }

    [Fact]
    public void The_window_asks_the_tray_before_its_own_question_as_it_closes()
    {
        var window = File.ReadAllText(Path.Combine(TestPaths.Panel(), "Shell", "MainWindow.xaml.cs"));
        var closing = window[window.IndexOf("protected override void OnClosing", StringComparison.Ordinal)..];
        var tray = closing.IndexOf("TakeClose?.Invoke() == true", StringComparison.Ordinal);
        var guard = closing.IndexOf("_exit.MayCloseNow()", StringComparison.Ordinal);
        Assert.True(tray >= 0 && guard > tray, "the tray is asked first, then the guard");
    }
}
