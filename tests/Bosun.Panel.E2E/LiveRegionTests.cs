using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using Microsoft.Win32;
using Xunit;

namespace Bosun.Panel.E2E;

/// <summary>A screen reader's side of a live region: the event a client subscribed to the window receives.</summary>
public class LiveRegionTests
{
    [Fact]
    public void Saving_a_setting_is_heard_from_the_status_line_and_reads_back()
    {
        var runBefore = RunValue();
        using var panel = PanelProcess.Start();
        var (window, _) = KeyboardTests.OnSettings(panel);
        var notify = Ui.Get(window, "NotifyMemoryPressure");
        var wanted = Ui.Toggle(notify) != ToggleState.On;

        var heard = new List<(string Id, string Name)>();
        AutomationEventHandler listen = (sender, _) =>
        {
            if (sender is AutomationElement source)
                lock (heard) heard.Add((source.Current.AutomationId, source.Current.Name));
        };
        Automation.AddAutomationEventHandler(AutomationElementIdentifiers.LiveRegionChangedEvent, window, TreeScope.Subtree, listen);
        try
        {
            ((TogglePattern)notify.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
            Ui.Invoke(Ui.Get(window, "Save"));
            var said = $"Saved and read back: memory pressure notifications {(wanted ? "on" : "off")}.";
            Ui.Until(() => { lock (heard) return heard.Any(h => h.Id == "Status"); }, Ui.Short, "the status line's live region event");
            Thread.Sleep(500); // a second event for the same change would arrive within this
            lock (heard)
            {
                var status = heard.Where(h => h.Id == "Status").ToList();
                Assert.Equal([("Status", said)], status);
            }
        }
        finally
        {
            Automation.RemoveAutomationEventHandler(AutomationElementIdentifiers.LiveRegionChangedEvent, window, listen);
        }

        // What was saved is in the test's own file, and Start with Windows was not touched.
        using var file = JsonDocument.Parse(File.ReadAllText(Path.Combine(panel.DataFolder, "panel.json")));
        Assert.Equal(wanted, file.RootElement.GetProperty("notify_memory_pressure").GetBoolean()); // PanelSettingsStore.Serialize
        Assert.Equal(runBefore, RunValue());
    }

    /// <summary>The user's Run key's Bosun Panel value (src/Bosun.Core/StartupEntry.cs), or null.</summary>
    private static string? RunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue("Bosun Panel") as string;
    }
}
