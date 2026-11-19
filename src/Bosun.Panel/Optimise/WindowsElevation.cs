using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Bosun.Core.Optimise;
using Microsoft.Win32.SafeHandles;

namespace Bosun.Panel.Optimise;

/// <summary>
/// The Windows form of <see cref="IElevation"/> (D05 T02 §6): <c>ShellExecuteEx</c> with the <c>runas</c> verb on
/// <c>Bosun.Panel.exe --optimise &lt;apply|undo&gt; &lt;item id&gt; &lt;invocation id&gt;</c>, hidden, so the only thing
/// that ever asks is the administrator prompt. The prompt is answered off the caller's thread, and the helper is
/// tracked by its process handle (a registered wait, so no thread sits blocked on it). The panel itself never runs
/// elevated. A real prompt is proven in D99 T01 §5.
/// </summary>
public sealed class WindowsElevation(string? program = null) : IElevation
{
    /// <summary>ERROR_CANCELLED: the person declined the administrator prompt.</summary>
    internal const int Declined = 1223;

    public Task<ElevationStart> StartAsync(HelperCommand command, CancellationToken cancellationToken) =>
        Task.Run(() => Start(command, program ?? Environment.ProcessPath ?? throw new InvalidOperationException("the panel's own path is not known")), cancellationToken);

    /// <summary>What a failed <c>ShellExecuteEx</c> means: error 1223 is a declined prompt, anything else a helper that could not start.</summary>
    internal static ElevationStart Classify(int error) =>
        error == Declined ? ElevationStart.Declined : ElevationStart.Failed(new Win32Exception(error).Message);

    private static ElevationStart Start(HelperCommand command, string program)
    {
        var info = new ShellExecuteInfo
        {
            Size = Marshal.SizeOf<ShellExecuteInfo>(),
            Mask = NoCloseProcess,
            Verb = "runas",
            File = program,
            Parameters = string.Join(' ', command.Arguments),   // the fixed command: an id, an operation, a hex invocation: nothing to quote
            Directory = Path.GetDirectoryName(program),
            Show = Hide,
        };
        if (!ShellExecuteEx(ref info)) return Classify(Marshal.GetLastWin32Error());
        return info.Process == IntPtr.Zero ? ElevationStart.Failed("Windows started the helper without a process handle to track") : ElevationStart.Running(new Running(info.Process));
    }

    /// <summary>One running helper: its handle, waited on by a registered wait.</summary>
    private sealed class Running(IntPtr handle) : IElevatedProcess
    {
        private readonly SafeWaitHandle _handle = new(handle, ownsHandle: true);

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            using var exited = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(_handle.DangerousGetHandle(), ownsHandle: false) };
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = ThreadPool.RegisterWaitForSingleObject(exited, static (state, _) => ((TaskCompletionSource<bool>)state!).TrySetResult(true), done, Timeout.Infinite, executeOnlyOnce: true);
            try
            {
                await using (cancellationToken.Register(() => done.TrySetCanceled(cancellationToken)))
                    await done.Task.ConfigureAwait(false);
            }
            finally
            {
                registration.Unregister(null);
            }
            return GetExitCodeProcess(_handle, out var code) ? unchecked((int)code) : -1;
        }

        public void Dispose() => _handle.Dispose();
    }

    private const uint NoCloseProcess = 0x00000040;
    private const int Hide = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int Size;
        public uint Mask;
        public IntPtr Window;
        public string Verb;
        public string File;
        public string Parameters;
        public string? Directory;
        public int Show;
        public IntPtr Instance;
        public IntPtr IdList;
        public string? Class;
        public IntPtr ClassKey;
        public uint HotKey;
        public IntPtr Icon;
        public IntPtr Process;
    }

    [DllImport("shell32.dll", EntryPoint = "ShellExecuteExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref ShellExecuteInfo info);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeWaitHandle process, out uint code);
}
