using System.Security.Principal;
using System.Text.RegularExpressions;

namespace Bosun.Panel.Startup;

/// <summary>
/// The name one panel instance goes by (D02 T02 §1): the user's SID, so each user has a panel of their own, or the
/// value of <c>BOSUN_PANEL_INSTANCE</c> when it is set, so a test, or a second panel run from a checkout, never meets
/// the panel the person is using.
/// </summary>
public static partial class InstanceName
{
    public const string Variable = "BOSUN_PANEL_INSTANCE";

    // The name becomes part of a mutex's and a pipe's name, where a backslash is a namespace and a slash a path.
    [GeneratedRegex(@"^[A-Za-z0-9.-]{1,64}$")]
    private static partial Regex Allowed();

    /// <summary>The name for this process: the variable's value when set, else the user's SID.</summary>
    public static bool TryResolve(out string name, out string refusal) => TryResolve(Environment.GetEnvironmentVariable(Variable), out name, out refusal);

    /// <summary>
    /// The name for a given value of the variable; null or empty means it is not set. False, with why, when the value
    /// can't be a name.
    /// </summary>
    public static bool TryResolve(string? variable, out string name, out string refusal)
    {
        refusal = "";
        if (string.IsNullOrEmpty(variable))
        {
            name = Sid();
            return true;
        }
        name = variable;
        if (Allowed().IsMatch(variable)) return true;
        name = "";
        refusal = $"{Variable} is set to a value that can't name a panel instance. It must be 1 to 64 letters, digits, dots or hyphens. Clear the variable, or correct it, and start Bosun Panel again.";
        return false;
    }

    /// <summary>The SID of the user this process runs as.</summary>
    public static string Sid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidOperationException("this process has no user SID");
    }
}
