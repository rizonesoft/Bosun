using Bosun.Core.Optimise;
using Bosun.Core;
using Microsoft.Win32;

namespace Bosun.Panel.Settings;

/// <summary>
/// The user's Run key in the registry, and Windows' record of which start-up entries it has turned off (D02 T02 §5).
/// The paths are given, under HKEY_CURRENT_USER, so a test proves this on a key of its own and the real Run key is
/// never touched by one.
/// </summary>
public sealed class RegistryRunKey(string runPath = RegistryRunKey.Run, string approvedPath = RegistryRunKey.Approved_) : IRunKey
{
    public const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string Approved_ = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public string? Read(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(runPath);
        return key?.GetValue(name) as string;
    }

    public void Write(string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(runPath, writable: true);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public void Delete(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(runPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    /// <summary>
    /// Windows' record of the entry, decoded by the one decoder Diagnostics uses (<see cref="StartupApproval.FromValue"/>):
    /// no key or no value is no record, which is on.
    /// </summary>
    public StartupApproval Approval(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(approvedPath);
        return StartupApproval.FromValue(key?.GetValue(name));
    }
}
