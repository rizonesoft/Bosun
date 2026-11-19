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
/// with that folder. <see cref="PanelOptimisations.TestElevatedVariable"/> =1 (elevated) or =0 (not) stands in for the token check, and only
/// together with <see cref="PanelOptimisations.TestItemVariable"/>.
/// </summary>
public sealed class WindowsHelperStanding(Func<string, string?> environment) : IHelperStanding
{
    private const string ProfileList = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    public bool IsElevated => TestSeam() ?? new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>The test seam: 1 says elevated and 0 says not, only while the test registration is on; otherwise null, and the real token decides.</summary>
    private bool? TestSeam() =>
        environment(PanelOptimisations.TestItemVariable) is not { Length: > 0 } ? null
        : environment(PanelOptimisations.TestElevatedVariable) switch { "1" => true, "0" => false, _ => null };

    public string CurrentSid => WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("this process has no user SID");

    public IReadOnlyList<AccountFolder> Accounts()
    {
        var variable = environment(PanelPaths.Variable);
        var own = new AccountFolder(CurrentSid, PanelPaths.Data(variable));
        if (!string.IsNullOrEmpty(variable)) return [own];
        return [own, .. FromProfiles(ReadProfileList()).Where(a => !string.Equals(a.Sid, own.Sid, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>An account's panel data folder for each profile of a local or work account: <c>AppData\Local\Bosun</c> under the profile.</summary>
    public static IReadOnlyList<AccountFolder> FromProfiles(IEnumerable<(string Sid, string ProfilePath)> profiles) =>
        [.. profiles
            .Where(p => (p.Sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) || p.Sid.StartsWith("S-1-12-1-", StringComparison.Ordinal)) && Path.IsPathFullyQualified(p.ProfilePath))
            .Select(p => new AccountFolder(p.Sid, Path.Combine(p.ProfilePath, "AppData", "Local", "Bosun")))];

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
