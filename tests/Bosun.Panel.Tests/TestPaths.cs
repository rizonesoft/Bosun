using System.IO;

namespace Bosun.Panel.Tests;

static class TestPaths
{
    /// <summary>
    /// The checkout: the first folder up from the test binary holding both the solution and the token file, since a
    /// copy of Bosun.slnx can sit under build/ (one did, from a measurement run).
    /// </summary>
    public static string Repo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Bosun.slnx")) && File.Exists(Path.Combine(dir.FullName, "design", "tokens", "colors.json")))
                return dir.FullName;
        throw new InvalidOperationException("no checkout (Bosun.slnx beside design/tokens/colors.json) above " + AppContext.BaseDirectory);
    }

    public static string Panel() => Path.Combine(Repo(), "src", "Bosun.Panel");

    public static string GeneratedTheme(string file) => Path.Combine(Panel(), "Theme", "Generated", file);
}
