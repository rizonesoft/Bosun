using System.IO;
using Bosun.Core.Instances;
using Bosun.Panel.Instances;
using Xunit;
using static Bosun.Panel.Tests.Instances.InstanceFixtures;

namespace Bosun.Panel.Tests.Instances;

/// <summary>
/// Open folder and Open terminal (D03 T02 §2, design/SPEC.md §4): each side's command, built and not run, and never a
/// Windows path given to a WSL shell.
/// </summary>
public class OpenCommandsTests
{
    static OpenTarget Target(InstanceRow row) => OpenCommands.Resolve(row).Target!;

    [Fact]
    public void A_Windows_row_opens_Explorer_and_a_terminal_at_its_folder()
    {
        var target = Target(new InstanceRow(I("claude", 1, cwd: @"D:\src\Bosun", repo: "D:/src/Bosun"), null));
        Assert.Equal(new OpenTarget(null, @"D:\src\Bosun"), target);
        var folder = OpenCommands.Folder(target);
        Assert.Equal("explorer.exe", folder.FileName);
        Assert.Equal([@"D:\src\Bosun"], folder.ArgumentList);
        var wt = OpenCommands.Terminal(target, windowsTerminal: true);
        Assert.Equal(("wt.exe", @"D:\src\Bosun"), (wt.FileName, wt.WorkingDirectory));
        Assert.Equal(["-d", "."], wt.ArgumentList);
        var console = OpenCommands.Terminal(target, windowsTerminal: false);           // no Windows Terminal: a console of its own
        Assert.Equal(("cmd.exe", @"D:\src\Bosun", true), (console.FileName, console.WorkingDirectory, console.UseShellExecute));
    }

    [Fact]
    public void A_WSL_row_in_its_own_file_system_opens_through_the_distro_s_share_and_a_WSL_shell_at_its_Linux_path()
    {
        var target = Target(new InstanceRow(I("claude", 2, side: "wsl", cwd: "/home/me/app", repo: "/home/me/app"), "Ubuntu"));
        Assert.Equal(@"\\wsl.localhost\Ubuntu\home\me\app", OpenCommands.Folder(target).ArgumentList.Single());
        var wt = OpenCommands.Terminal(target, windowsTerminal: true);
        Assert.Equal(("wt.exe", @"\\wsl.localhost\Ubuntu\home\me\app"), (wt.FileName, wt.WorkingDirectory));
        Assert.Equal(["-d", ".", "wsl.exe", "-d", "Ubuntu"], wt.ArgumentList);
        var wsl = OpenCommands.Terminal(target, windowsTerminal: false);
        Assert.Equal(("wsl.exe", "-d Ubuntu", @"\\wsl.localhost\Ubuntu\home\me\app", true), (wsl.FileName, wsl.Arguments, wsl.WorkingDirectory, wsl.UseShellExecute));
    }

    /// <summary>The drives a distro's report lists, as its logger reads them from the mount table (D03 T02 §6).</summary>
    static DriveMounts Mounts(params (string Mount, char Letter)[] drives) =>
        new([.. drives.Select(d => new DriveMount { Mount = d.Mount, Windows = $"{d.Letter}:\\" })]);

    static readonly DriveMounts Default = Mounts(("/mnt/c", 'C'), ("/mnt/d", 'D'));

    [Fact]
    public void A_WSL_row_under_mnt_opens_the_Windows_drive_in_Explorer_and_keeps_its_Linux_path_in_the_shell()
    {
        // The distro's drive mounts as its report gives them, which the merge puts on each row.
        var target = Target(new InstanceRow(I("claude", 3, side: "wsl", cwd: "/mnt/d/src/Bosun/src", repo: "/mnt/d/src/Bosun"), "Ubuntu", Default));
        Assert.Equal(@"D:\src\Bosun\src", OpenCommands.Folder(target).ArgumentList.Single());
        Assert.Equal(@"D:\src\Bosun\src", OpenCommands.Terminal(target, true).WorkingDirectory);    // wsl.exe starts in /mnt/d/src/Bosun/src behind it
        Assert.Equal(@"C:\", OpenCommands.MountedDrive("/mnt/c", Default));
        Assert.Null(OpenCommands.MountedDrive("/mnt/cdrom/x", Default));                         // a folder named like a drive is not one
    }

    [Fact]
    public void A_guest_path_names_a_Windows_folder_only_under_a_drive_the_distro_has_mounted()
    {
        // D03 T02 §6: from OpenCommands.Resolve, each row's Explorer argument and terminal folder, with and without
        // Windows Terminal, by the drives its distro has mounted now.
        void Opens(DriveMounts? mounts, string cwd, string windowsFolder)
        {
            var target = Target(new InstanceRow(I("claude", 3, side: "wsl", cwd: cwd), "Ubuntu", mounts));
            Assert.Equal(windowsFolder, OpenCommands.Folder(target).ArgumentList.Single());
            Assert.Equal(windowsFolder, OpenCommands.Terminal(target, windowsTerminal: true).WorkingDirectory);
            var console = OpenCommands.Terminal(target, windowsTerminal: false);
            Assert.Equal(("wsl.exe", "-d Ubuntu", windowsFolder), (console.FileName, console.Arguments, console.WorkingDirectory));
        }
        var moved = Mounts(("/windir/c", 'C'));
        Opens(moved, "/windir/c/app", @"C:\app");                                     // the drive under its moved mount
        Opens(moved, "/mnt/c/app", @"\\wsl.localhost\Ubuntu\mnt\c\app");               // /mnt/c is a guest folder there
        Opens(Mounts(("/c", 'C')), "/c/app", @"C:\app");                               // root=/
        Opens(Default, "/mnt/c", @"C:\");                                             // a drive's own root
        Opens(Default, "/mnt/C/app", @"\\wsl.localhost\Ubuntu\mnt\C\app");             // no such mount: Linux paths are exact
        Opens(Mounts(("/mnt/c", 'C'), ("/mnt/c/share", 'E')), "/mnt/c/share/x", @"E:\x"); // the longest mount holds it
        Opens(null, "/mnt/c/app", @"\\wsl.localhost\Ubuntu\mnt\c\app");               // not known: kept a guest folder
        // Round 1: a bind of a drive's subfolder is that subfolder; a guest file system mounted inside a drive hides it.
        var bound = new DriveMounts([new() { Mount = "/mnt/c", Windows = @"C:\" }, new() { Mount = "/work", Windows = @"C:\src" }, new() { Mount = "/mnt/c/work/app", Windows = null }]);
        Opens(bound, "/work/app", @"C:\src\app");
        Opens(bound, "/mnt/c/work/app/x", @"\\wsl.localhost\Ubuntu\mnt\c\work\app\x");
        Opens(bound, "/mnt/c/work/other", @"C:\work\other");
        // A mount deep in the guest (over 256 characters, within PATH_MAX) is a mount like any other.
        Assert.True(DriveMounts.IsValid(new DriveMount { Mount = "/" + new string('a', 300), Windows = @"C:\" }));
        // Round 5: Linux allows a tab in a folder's name, so a mount point holding one is a mount like any other; NUL is not.
        Assert.True(DriveMounts.IsValid(new DriveMount { Mount = "/tmp/bosun\tmount", Windows = @"C:\" }));
        Opens(new DriveMounts([new() { Mount = "/tmp/bosun\tmount", Windows = @"C:\" }]), "/tmp/bosun\tmount/app", @"C:\app");
        Assert.False(DriveMounts.IsValid(new DriveMount { Mount = "/tmp/a\0b", Windows = @"C:\" }));
        // Round 15: U+0085 is legal in a Windows folder's name; the characters 1 to 31 are not.
        Assert.True(DriveMounts.IsValid(new DriveMount { Mount = "/work", Windows = "C:\\A\u0085B" }));
        Assert.False(DriveMounts.IsValid(new DriveMount { Mount = "/work", Windows = "C:\\A\u0001B" }));
        Assert.Null(OpenCommands.MountedDrive("/mnt/c/app", null));
    }

    [Fact]
    public void An_unjoined_WSL_holder_opens_in_the_distro_it_names_only_with_a_Linux_path()
    {
        // Windows saw the holder; its distro's report was not read, so the row has no distro of its own.
        var named = OpenCommands.Resolve(new InstanceRow(I("job", 9, side: "wsl", cwd: "/home/me/app", guestDistro: "Debian"), null));
        Assert.Equal(new OpenTarget("Debian", "/home/me/app"), named.Target);
        Assert.Equal((null, OpenCommands.NoDistro), OpenCommands.Resolve(new InstanceRow(I("job", 9, side: "wsl", cwd: "/home/me/app"), null)));
        // A Windows path is never handed to a WSL shell.
        Assert.Equal((null, OpenCommands.NoDistro), OpenCommands.Resolve(new InstanceRow(I("job", 9, side: "wsl", cwd: @"D:\src\Bosun", guestDistro: "Debian"), null)));
        Assert.Equal((null, OpenCommands.NoFolder), OpenCommands.Resolve(new InstanceRow(I("claude", 9, cwd: null, repo: null), null)));
        // Without a cwd, the repo.
        Assert.Equal(@"D:/src/Bosun", OpenCommands.Resolve(new InstanceRow(I("claude", 9, cwd: null, repo: "D:/src/Bosun"), null)).Target!.Path);
    }

    [Fact]
    public void A_folder_name_is_never_an_argument_Windows_Terminal_could_split_into_another_command()
    {
        // Windows Terminal reads ';' in any argument as a new command (round 1): the folder goes in as the directory.
        foreach (var row in new[]
                 {
                     new InstanceRow(I("claude", 1, cwd: @"D:\x;calc.exe"), null),
                     new InstanceRow(I("claude", 2, side: "wsl", cwd: "/tmp/project;calc.exe"), "Ubuntu"),
                 })
        {
            var target = Target(row);
            foreach (var start in new[] { OpenCommands.Terminal(target, true), OpenCommands.Terminal(target, false) })
            {
                Assert.DoesNotContain(start.ArgumentList, a => a.Contains(';', StringComparison.Ordinal));
                Assert.DoesNotContain(";", start.Arguments, StringComparison.Ordinal);
                Assert.Contains(";calc.exe", start.WorkingDirectory, StringComparison.Ordinal);
            }
        }
        // A distro named with anything WSL would not name one with is refused.
        Assert.Equal((null, OpenCommands.NoDistro), OpenCommands.Resolve(new InstanceRow(I("claude", 3, side: "wsl", cwd: "/home/a"), "Ubuntu;calc")));
    }

    [Fact]
    public void Windows_Terminal_is_found_on_the_PATH_or_not_at_all()
    {
        var folder = Path.Combine(Path.GetTempPath(), "bosun-wt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            Assert.False(OpenCommands.HasWindowsTerminal(folder));
            File.WriteAllText(Path.Combine(folder, "wt.exe"), "");
            Assert.True(OpenCommands.HasWindowsTerminal($"C:\\nowhere{Path.PathSeparator}{folder}"));
            Assert.False(OpenCommands.HasWindowsTerminal("bad|path"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
