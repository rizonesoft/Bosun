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
    public static string FinalPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows()) return Resolve(full, 0);
        // Windows: the deepest folder that exists is asked of an open handle for what it really is (links followed, long
        // names), and what does not exist yet is kept as it is.
        var tail = new Stack<string>();
        for (var candidate = Trim(full); candidate.Length > 0 && Path.GetFileName(candidate).Length > 0; candidate = Trim(Path.GetDirectoryName(candidate) ?? ""))
        {
            if (NativeFiles.TryFinalPathOfFolder(candidate) is { } final)
                return tail.Aggregate(Trim(final), (current, segment) => Path.Combine(current, segment));
            tail.Push(Path.GetFileName(candidate));
        }
        return Resolve(full, 0);
    }

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
    /// The data folder must sit under the account's registered profile folder, and no folder from there down to it (nor
    /// its <c>helper</c> and <c>backups</c> folders) may be a reparse point: a junction at <c>AppData\Local</c> would be
    /// followed by the folder's own final path and the check against it would pass, because both sides followed it.
    /// </summary>
    private static string? AncestorProblem(string full, string profileRoot)
    {
        if (!full.StartsWith(profileRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return $"the data folder is not inside the account's profile folder '{profileRoot}'";
        var chain = new List<string>();
        var current = profileRoot;
        foreach (var segment in full[(profileRoot.Length + 1)..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            chain.Add(current = Path.Combine(current, segment));
        chain.Add(Path.Combine(full, "helper"));
        chain.Add(Path.Combine(full, "backups"));
        foreach (var path in chain)
        {
            if (!Directory.Exists(path) && !File.Exists(path)) continue;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return $"'{path}' is a junction or link, so the account's profile folder is redirected there";
        }
        return null;
    }

    /// <summary>
    /// Why the account's data folder can't be trusted to hold the helper's writes, or null when it can: the folder is
    /// itself redirected (not directly its parent's child), or its <c>helper</c> or <c>backups</c> folder is a link
    /// that leads out of it.
    /// </summary>
    public static string? Problem(string dataFolder, string? profileRoot = null)
    {
        try
        {
            var full = Trim(Path.GetFullPath(dataFolder));
            if (profileRoot is not null && AncestorProblem(full, Trim(Path.GetFullPath(profileRoot))) is { } ancestor) return ancestor;
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

    private bool Inside(string finalFolder)
    {
        var folder = finalFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(folder, root, StringComparison.OrdinalIgnoreCase) || folder.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private string Check(string path)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? throw new IOException($"'{path}' has no folder");
        if (!FolderConfinement.IsInside(folder, _root)) throw new IOException($"'{folder}' resolves outside the data folder: nothing is written there");
        return folder;
    }

    /// <summary>
    /// Holds the folder open (nothing can remove or rename it while held) and checks, on the open handle, that it is
    /// the folder inside the data folder: so the move or delete that follows lands where it was checked.
    /// </summary>
    private List<IDisposable> Hold(params string[] paths)
    {
        var held = new List<IDisposable>();
        try
        {
            foreach (var path in paths)
            {
                var folder = Check(path);
                if (!OperatingSystem.IsWindows() || !Directory.Exists(folder)) continue;
                var handle = NativeFiles.OpenFolder(folder);
                held.Add(handle);
                if (!Inside(NativeFiles.FinalPath(handle))) throw new IOException($"'{folder}' resolves outside the data folder: nothing is written there");
            }
            return held;
        }
        catch
        {
            foreach (var h in held) h.Dispose();
            throw;
        }
    }

    private void Held(Action action, params string[] paths)
    {
        var held = Hold(paths);
        try
        {
            action();
        }
        finally
        {
            foreach (var h in held) h.Dispose();
        }
    }

    public override byte[] Read(string path, int limit) => inner.Read(path, limit);

    public override void Write(string path, byte[] bytes)
    {
        Check(path);
        inner.Write(path, bytes);
    }

    public override void WriteNew(string path, byte[] bytes) => WriteNew(path, bytes, null);

    public override void WriteNew(string path, byte[] bytes, Func<string, bool>? finalPathAllowed)
    {
        Check(path);
        // The file is created first and only then is its real place asked of its own handle: no content is written unless
        // it is inside the data folder (a directory swapped for a junction after the check is caught here).
        inner.WriteNew(path, bytes, final => Inside(Path.GetDirectoryName(final) ?? "") && (finalPathAllowed?.Invoke(final) ?? true));
    }

    public override void Replace(string from, string to) => Held(() => inner.Replace(from, to), from, to);

    public override void MoveNew(string from, string to) => Held(() => inner.MoveNew(from, to), from, to);

    public override void Delete(string path) => Held(() => inner.Delete(path), path);
}
