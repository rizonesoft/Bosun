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
        var target = Target(new InstanceRow(I("claude", 1, cwd: @"D:\Bosun", repo: "D:/Bosun"), null));
        Assert.Equal(new OpenTarget(null, @"D:\Bosun"), target);
        var folder = OpenCommands.Folder(target);
        Assert.Equal("explorer.exe", folder.FileName);
        Assert.Equal([@"D:\Bosun"], folder.ArgumentList);
        var wt = OpenCommands.Terminal(target, windowsTerminal: true);
        Assert.Equal("wt.exe", wt.FileName);
        Assert.Equal(["-d", @"D:\Bosun"], wt.ArgumentList);
        var console = OpenCommands.Terminal(target, windowsTerminal: false);           // no Windows Terminal: a console of its own
        Assert.Equal(("cmd.exe", @"D:\Bosun", true), (console.FileName, console.WorkingDirectory, console.UseShellExecute));
    }

    [Fact]
    public void A_WSL_row_in_its_own_file_system_opens_through_the_distro_s_share_and_a_WSL_shell_at_its_Linux_path()
    {
        var target = Target(new InstanceRow(I("claude", 2, side: "wsl", cwd: "/home/me/app", repo: "/home/me/app"), "Ubuntu"));
        Assert.Equal(@"\\wsl.localhost\Ubuntu\home\me\app", OpenCommands.Folder(target).ArgumentList.Single());
        var wt = OpenCommands.Terminal(target, windowsTerminal: true);
        Assert.Equal("wt.exe", wt.FileName);
        Assert.Equal(["wsl.exe", "-d", "Ubuntu", "--cd", "/home/me/app"], wt.ArgumentList);
        var wsl = OpenCommands.Terminal(target, windowsTerminal: false);
        Assert.Equal(("wsl.exe", "-d \"Ubuntu\" --cd \"/home/me/app\"", true), (wsl.FileName, wsl.Arguments, wsl.UseShellExecute));
    }

    [Fact]
    public void A_WSL_row_under_mnt_opens_the_Windows_drive_in_Explorer_and_keeps_its_Linux_path_in_the_shell()
    {
        var target = Target(new InstanceRow(I("claude", 3, side: "wsl", cwd: "/mnt/d/Bosun/src", repo: "/mnt/d/Bosun"), "Ubuntu"));
        Assert.Equal(@"D:\Bosun\src", OpenCommands.Folder(target).ArgumentList.Single());
        Assert.Equal(["wsl.exe", "-d", "Ubuntu", "--cd", "/mnt/d/Bosun/src"], OpenCommands.Terminal(target, true).ArgumentList);
        Assert.Equal(@"C:\", OpenCommands.MountedDrive("/mnt/c"));
        Assert.Null(OpenCommands.MountedDrive("/mnt/cdrom/x"));                         // a folder named like a drive is not one
    }

    [Fact]
    public void An_unjoined_WSL_holder_opens_in_the_distro_it_names_only_with_a_Linux_path()
    {
        // Windows saw the holder; its distro's report was not read, so the row has no distro of its own.
        var named = OpenCommands.Resolve(new InstanceRow(I("job", 9, side: "wsl", cwd: "/home/me/app", guestDistro: "Debian"), null));
        Assert.Equal(new OpenTarget("Debian", "/home/me/app"), named.Target);
        Assert.Equal((null, OpenCommands.NoDistro), OpenCommands.Resolve(new InstanceRow(I("job", 9, side: "wsl", cwd: "/home/me/app"), null)));
        // A Windows path is never handed to a WSL shell.
        Assert.Equal((null, OpenCommands.NoDistro), OpenCommands.Resolve(new InstanceRow(I("job", 9, side: "wsl", cwd: @"D:\Bosun", guestDistro: "Debian"), null)));
        Assert.Equal((null, OpenCommands.NoFolder), OpenCommands.Resolve(new InstanceRow(I("claude", 9, cwd: null, repo: null), null)));
        // Without a cwd, the repo.
        Assert.Equal(@"D:/Bosun", OpenCommands.Resolve(new InstanceRow(I("claude", 9, cwd: null, repo: "D:/Bosun"), null)).Target!.Path);
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
