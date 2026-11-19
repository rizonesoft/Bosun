using System.IO;
using System.Security.Principal;
using Bosun.Core.Optimise;
using Bosun.Panel.Startup;
using Microsoft.Win32;

namespace Bosun.Panel.Optimise;

/// <summary>
/// The Windows form of the helper's standing (D05 T02 §6): the real token's elevation, the SID this process runs as,
/// and each account's own panel data folder.
///
/// An account's folder is its profile's <c>AppData\Local\Bosun</c>, from the profile list, never from a request. When
/// <c>BOSUN_PANEL_DATA</c> is set (a test, or a person who moved their data) only this process's own account is known,
/// with that folder. Elevation is the process token's and nothing else: no variable, argument or file can say it.
/// </summary>
public sealed class WindowsHelperStanding(Func<string, string?> environment) : IHelperStanding
{
    private const string ProfileList = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    public bool IsElevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public string CurrentSid => WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("this process has no user SID");

    public IReadOnlyList<AccountFolder> Accounts()
    {
        var variable = environment(PanelPaths.Variable);
        var own = new AccountFolder(CurrentSid, PanelPaths.Data(variable), string.IsNullOrEmpty(variable) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : null);
        if (!string.IsNullOrEmpty(variable)) return [own];
        return [own, .. FromProfiles(ReadProfileList()).Where(a => !string.Equals(a.Sid, own.Sid, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>An account's panel data folder for each profile of a local or work account: <c>AppData\Local\Bosun</c> under the profile.</summary>
    public static IReadOnlyList<AccountFolder> FromProfiles(IEnumerable<(string Sid, string ProfilePath)> profiles) =>
        [.. profiles
            .Where(p => (p.Sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) || p.Sid.StartsWith("S-1-12-1-", StringComparison.Ordinal)) && Path.IsPathFullyQualified(p.ProfilePath))
            .Select(p => new AccountFolder(p.Sid, Path.Combine(p.ProfilePath, "AppData", "Local", "Bosun"), p.ProfilePath))];

    private static IEnumerable<(string Sid, string ProfilePath)> ReadProfileList()
    {
        using var key = Registry.LocalMachine.OpenSubKey(ProfileList);
        if (key is null) yield break;
        foreach (var sid in key.GetSubKeyNames())
        {
            using var profile = key.OpenSubKey(sid);
            if (profile?.GetValue("ProfileImagePath") is string path && path.Length > 0) yield return (sid, path);
        }
    }
}
