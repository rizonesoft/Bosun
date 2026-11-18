using System.Windows.Automation;

namespace Bosun.Panel.E2E;

/// <summary>Raised instead of sending input whose target is not the one the test meant.</summary>
sealed class RealInputRefused(string message) : Exception(message);

/// <summary>
/// Every key and pointer event the smoke sends goes through here, and only after Windows confirms where it will land.
/// Real input goes to whatever owns the foreground or lies under the pointer, which on a machine someone is using can
/// be anything: <c>scripts/capture-window.ps1</c> once typed into an unrelated editor because a
/// <c>SetForegroundWindow</c> had silently failed. So a key needs the panel's window in the foreground; a point on the
/// window needs the panel's window under it; a point on the notification area needs the taskbar or its overflow under
/// it and the point inside a Bosun Panel icon; a point on a menu needs a popup menu of the panel's own process under it.
/// And nothing may be held down: a key sent while someone holds Alt or Windows is Alt+Tab or Task View, and a click
/// with Ctrl or a button held is another gesture, so input waits a moment for held keys to be let go and is refused
/// if they are not (the person's keys are never released for them). Otherwise nothing is sent and the test fails
/// with why.
/// </summary>
static class RealInput
{
    private static readonly string[] TrayWindows = ["Shell_TrayWnd", "Shell_SecondaryTrayWnd", "TopLevelWindowForOverflowXamlIsland"];

    /// <summary>
    /// Gives an element the keyboard through UI Automation: no input, so the watcher's hook cannot stop it, yet it takes
    /// the foreground with it and what a person types lands there. Every focus change in the smoke comes through here.
    /// </summary>
    public static void Focus(AutomationElement element)
    {
        SmokeInput.Require("RealInput.Focus");
        element.SetFocus();
    }

    /// <summary>A key, to the panel's window only.</summary>
    public static void Key(IntPtr panel, int virtualKey)
    {
        SmokeInput.Require("RealInput.Key");
        RequireForeground(panel, "a key");
        Native.Key(virtualKey);
    }

    /// <summary>A key with a modifier held, to the panel's window only.</summary>
    public static void Chord(IntPtr panel, int modifier, int virtualKey)
    {
        SmokeInput.Require("RealInput.Chord");
        RequireForeground(panel, "a key");
        Native.Chord(modifier, virtualKey);
    }

    /// <summary>The pointer onto a point of the panel's window.</summary>
    public static void MoveOnWindow(IntPtr panel, int x, int y)
    {
        SmokeInput.Require("RealInput.MoveOnWindow");
        RequireWindowAt(panel, x, y, "the pointer");
        Native.MoveTo(x, y);
    }

    /// <summary>A right click on a point of the panel's window.</summary>
    public static void RightClickOnWindow(IntPtr panel, int x, int y)
    {
        SmokeInput.Require("RealInput.RightClickOnWindow");
        RequireWindowAt(panel, x, y, "a right click");
        Native.RightClick(x, y);
    }

    /// <summary>A left click on a point of the panel's window.</summary>
    public static void ClickOnWindow(IntPtr panel, int x, int y)
    {
        SmokeInput.Require("RealInput.ClickOnWindow");
        RequireWindowAt(panel, x, y, "a click");
        Native.LeftDown(x, y);
        Native.LeftUp(x, y);
    }

    /// <summary>A left-button drag that starts on the panel's window; the button is let go wherever it ends.</summary>
    public static void DragOnWindow(IntPtr panel, int fromX, int fromY, int toX, int toY, int steps = 12)
    {
        SmokeInput.Require("RealInput.DragOnWindow");
        RequireWindowAt(panel, fromX, fromY, "a drag");
        Native.LeftDown(fromX, fromY);
        try
        {
            for (var step = 1; step <= steps; step++)
            {
                Native.MoveTo(fromX + (toX - fromX) * step / steps, fromY + (toY - fromY) * step / steps);
                Thread.Sleep(15);
            }
        }
        finally
        {
            Native.LeftUp(toX, toY); // never leave the button held
        }
    }

    /// <summary>The pointer onto, or a right click on, a Bosun Panel icon in the notification area.</summary>
    public static void MoveOnIcon(AutomationElement icon)
    {
        SmokeInput.Require("RealInput.MoveOnIcon");
        var (x, y) = RequireIcon(icon, "the pointer");
        Native.MoveTo(x, y);
    }

    public static void ClickIcon(AutomationElement icon)
    {
        SmokeInput.Require("RealInput.ClickIcon");
        var (x, y) = RequireIcon(icon, "a click");
        Native.MoveTo(x, y);
        Native.LeftDown(x, y);
        Native.LeftUp(x, y);
    }

    public static void RightClickIcon(AutomationElement icon)
    {
        SmokeInput.Require("RealInput.RightClickIcon");
        var (x, y) = RequireIcon(icon, "a right click");
        Native.MoveTo(x, y);
        Native.RightClick(x, y);
    }

    /// <summary>A left click on a line of a popup menu that the panel's process opened.</summary>
    public static void ClickMenuLine(int panelProcess, AutomationElement line)
    {
        SmokeInput.Require("RealInput.ClickMenuLine");
        RequireNothingHeld("a click");
        var (x, y) = Ui.Centre(line);
        var root = Native.RootAt(x, y);
        if (Native.ClassOf(root) != "#32768" || Native.ProcessOf(root) != panelProcess)
            throw new RealInputRefused($"a click was not sent to ({x}, {y}): the window there is {Describe(root)}, not a menu of the panel (process {panelProcess})");
        Native.LeftDown(x, y);
        Native.LeftUp(x, y);
    }

    /// <summary>
    /// A key to a menu the panel has open: sent only while nothing is held and the foreground is the panel's own (its
    /// window or the menu), so it can't become another chord or land in another program (review of D02 T03 §2).
    /// </summary>
    public static void KeyToOwnMenu(int panelProcess, int virtualKey)
    {
        SmokeInput.Require("RealInput.KeyToOwnMenu");
        RequireNothingHeld("a key");
        var foreground = Native.GetForegroundWindow();
        if (Native.ProcessOf(foreground) != panelProcess)
            throw new RealInputRefused($"a key was not sent: the foreground window is {Describe(foreground)}, not one of the panel's (process {panelProcess})");
        Native.Key(virtualKey);
    }

    /// <summary>Puts the pointer back where it was when the returned value is disposed.</summary>
    public static IDisposable KeepPointer()
    {
        SmokeInput.Require("RealInput.KeepPointer");
        var was = Native.Cursor();
        return new Restore(() => Native.MoveTo(was.X, was.Y));
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    private static readonly (int Key, string Name)[] Held =
    [
        (Native.VK_SHIFT, "Shift"), (Native.VK_CONTROL, "Ctrl"), (Native.VK_MENU, "Alt"), (Native.VK_LWIN, "Windows"), (Native.VK_RWIN, "Windows"),
        (Native.VK_LBUTTON, "the left mouse button"), (Native.VK_RBUTTON, "the right mouse button"), (Native.VK_MBUTTON, "the middle mouse button"),
    ];

    /// <summary>Refuses while a modifier key or a mouse button is down, after giving it a second to be let go.</summary>
    private static void RequireNothingHeld(string what)
    {
        var end = DateTime.UtcNow.AddSeconds(1);
        while (true)
        {
            var down = Held.Where(h => Native.IsDown(h.Key)).Select(h => h.Name).Distinct().ToList();
            if (down.Count == 0) return;
            if (DateTime.UtcNow > end) throw new RealInputRefused($"{what} was not sent: {string.Join(" and ", down)} {(down.Count == 1 ? "is" : "are")} held down, which would change what it does");
            Thread.Sleep(50);
        }
    }

    private static void RequireForeground(IntPtr panel, string what)
    {
        RequireNothingHeld(what);
        var foreground = Native.GetForegroundWindow();
        if (foreground != panel)
            throw new RealInputRefused($"{what} was not sent: the foreground window is {Describe(foreground)}, not the panel's ({panel})");
    }

    private static void RequireWindowAt(IntPtr panel, int x, int y, string what)
    {
        RequireForeground(panel, what);
        var root = Native.RootAt(x, y);
        if (root != panel) throw new RealInputRefused($"{what} was not sent to ({x}, {y}): the window there is {Describe(root)}, not the panel's ({panel})");
    }

    private static (int X, int Y) RequireIcon(AutomationElement icon, string what)
    {
        RequireNothingHeld(what);
        var (x, y) = Ui.Centre(icon);
        var name = icon.Current.Name;
        var box = icon.Current.BoundingRectangle;
        var root = Native.RootAt(x, y);
        if (!name.StartsWith("Bosun Panel", StringComparison.Ordinal) || !TrayWindows.Contains(Native.ClassOf(root)) || !box.Contains(x, y))
            throw new RealInputRefused($"{what} was not sent to ({x}, {y}): it is not on a Bosun Panel icon in the notification area (the icon says '{name}', the window there is {Describe(root)})");
        return (x, y);
    }

    private static string Describe(IntPtr window) =>
        window == IntPtr.Zero ? "none" : $"{window} ({Native.ClassOf(window)}, process {Native.ProcessOf(window)}, '{Native.TitleOf(window)}')";
}
