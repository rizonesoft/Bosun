namespace Bosun.Core.Optimise;

/// <summary>
/// Keeps the elevated helper's writes inside the initiating user's own data folder (D05 T02 §6). A folder name checked
/// as text proves nothing: a directory junction or symbolic link anywhere below the account's folder sends a write
/// somewhere else under the helper's elevated token. So every folder is resolved to its final path, following links
/// component by component, and a write is allowed only when that final path is inside the data folder's own final path.
/// </summary>
public static class FolderConfinement
{
    private const int MaxLinkDepth = 32;

    /// <summary>The path with every link on it followed (components that do not exist yet are kept as they are).</summary>
    public static string FinalPath(string path) => Resolve(Path.GetFullPath(path), 0);

    private static string Resolve(string full, int depth)
    {
        if (depth > MaxLinkDepth) throw new IOException($"'{full}' passes through too many links");
        var root = Path.GetPathRoot(full) ?? "";
        var current = root;
        foreach (var segment in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (info.Exists && info.LinkTarget is not null)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true) ?? throw new IOException($"the link '{next}' can't be resolved");
                next = Resolve(Path.GetFullPath(target.FullName), depth + 1);
            }
            current = next;
        }
        return current;
    }

    private static string Trim(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>True when <paramref name="folder"/>'s final path is <paramref name="finalRoot"/> or inside it.</summary>
    public static bool IsInside(string folder, string finalRoot)
    {
        var final = Trim(FinalPath(folder));
        var root = Trim(finalRoot);
        return string.Equals(final, root, StringComparison.OrdinalIgnoreCase) || final.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Why the account's data folder can't be trusted to hold the helper's writes, or null when it can: the folder is
    /// itself redirected (not directly its parent's child), or its <c>helper</c> or <c>backups</c> folder is a link
    /// that leads out of it.
    /// </summary>
    public static string? Problem(string dataFolder)
    {
        try
        {
            var full = Trim(Path.GetFullPath(dataFolder));
            var parent = Path.GetDirectoryName(full);
            if (parent is null) return "the data folder has no parent folder";
            var expected = Path.Combine(FinalPath(parent), Path.GetFileName(full));
            var final = FinalPath(full);
            if (!string.Equals(Trim(final), Trim(expected),StringComparison.OrdinalIgnoreCase))
                return $"the data folder is redirected: it resolves to '{final}'";
            foreach (var name in new[] { "helper", "backups" })
            {
                var inner = Path.Combine(full, name);
                if (Directory.Exists(inner) && !IsInside(inner, final)) return $"'{name}' in the data folder leads outside it, to '{FinalPath(inner)}'";
            }
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"the data folder could not be resolved: {e.Message}";
        }
    }
}

/// <summary>
/// The helper's disk access: every write, move and delete is checked first, at the moment it is made, that the folder
/// it lands in resolves inside the data folder. Reads pass straight through.
/// </summary>
public sealed class ConfinedFiles(PanelSettingsFiles inner, string dataFolder) : PanelSettingsFiles
{
    private readonly string _root = FolderConfinement.FinalPath(dataFolder);

    private void Check(string path)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? throw new IOException($"'{path}' has no folder");
        if (!FolderConfinement.IsInside(folder, _root)) throw new IOException($"'{folder}' resolves outside the data folder: nothing is written there");
    }

    public override byte[] Read(string path, int limit) => inner.Read(path, limit);

    public override void Write(string path, byte[] bytes)
    {
        Check(path);
        inner.Write(path, bytes);
    }

    public override void WriteNew(string path, byte[] bytes)
    {
        Check(path);
        inner.WriteNew(path, bytes);
    }

    public override void Replace(string from, string to)
    {
        Check(from);
        Check(to);
        inner.Replace(from, to);
    }

    public override void MoveNew(string from, string to)
    {
        Check(from);
        Check(to);
        inner.MoveNew(from, to);
    }

    public override void Delete(string path)
    {
        Check(path);
        inner.Delete(path);
    }
}
