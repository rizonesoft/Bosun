using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Bosun.Core;

/// <summary>
/// A child process created inside a job object of its own, with three pipes. <see cref="System.Diagnostics.Process"/>
/// can only put a child in a job after it has started, which leaves a moment in which the child can start something
/// that escapes; here the job is named in the child's start-up attributes, so it is in the job from its first
/// instruction, and so is everything it starts. The same attributes list the three pipe ends as the only handles the
/// child inherits. Closing this ends whatever is still in the job.
/// </summary>
internal sealed class ChildProcess : IDisposable
{
    private readonly IntPtr _job;
    private readonly IntPtr _process;
    private readonly ProcessExit _exit;
    private readonly RegisteredWaitHandle _wait;
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    private ChildProcess(IntPtr job, IntPtr process, Stream input, Stream output, Stream error)
    {
        (_job, _process, Input, Output, Error) = (job, process, input, output, error);
        _exit = new ProcessExit(process);
        _wait = ThreadPool.RegisterWaitForSingleObject(_exit, (_, _) => _exited.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    /// <summary>The child's stdin, to write to. Whoever writes closes it.</summary>
    public Stream Input { get; }

    public Stream Output { get; }

    public Stream Error { get; }

    /// <summary>Completes when the child itself has exited.</summary>
    public Task Exited => _exited.Task;

    public int ExitCode() => GetExitCodeProcess(_process, out var code) ? unchecked((int)code) : throw new Win32Exception();

    /// <summary>Ends the child and everything it started that is still running.</summary>
    public void EndTree() => TerminateJobObject(_job, 1);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wait.Unregister(null);
        _exit.Dispose();
        Input.Dispose();
        Output.Dispose();
        Error.Dispose();
        CloseHandle(_process);
        CloseHandle(_job); // the job's last handle: anything still in it ends here (KILL_ON_JOB_CLOSE)
    }

    /// <summary>
    /// Starts the request's program. Throws, with every handle closed and no process left, when the job, the pipes or
    /// the process can't be made.
    /// </summary>
    public static ChildProcess Start(ProcessRequest request)
    {
        var program = ProcessRunner.Resolve(request.Program);
        var commandLine = ProcessRunner.CommandLine(program, request.Arguments);
        if (commandLine.Length > 32766) throw new ArgumentException($"the program and its arguments come to {commandLine.Length} characters; Windows takes at most 32,766");

        var job = IntPtr.Zero;
        var attributes = IntPtr.Zero;
        var attributesReady = false;
        var handleList = IntPtr.Zero;
        var jobList = IntPtr.Zero;
        var environment = IntPtr.Zero;
        SafeFileHandle? childIn = null, childOut = null, childErr = null, ourIn = null, ourOut = null, ourErr = null;
        try
        {
            job = CreateJobObjectW(IntPtr.Zero, IntPtr.Zero);
            if (job == IntPtr.Zero) throw Failure("can't make a job for the program");
            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                throw Failure("can't set the job to end with the call");

            Pipe(out childIn, out ourIn, childReads: true);
            Pipe(out ourOut, out childOut, childReads: false);
            Pipe(out ourErr, out childErr, childReads: false);

            // Two start-up attributes: the handles the child inherits (these three and no others, so a call running
            // beside this one can't leak its pipes into this child), and the job it is created in.
            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributes, 2, 0, ref size)) throw Failure("can't prepare the program's start-up attributes");
            attributesReady = true;

            handleList = Marshal.AllocHGlobal(3 * IntPtr.Size);
            Marshal.WriteIntPtr(handleList, 0, childIn.DangerousGetHandle());
            Marshal.WriteIntPtr(handleList, IntPtr.Size, childOut.DangerousGetHandle());
            Marshal.WriteIntPtr(handleList, 2 * IntPtr.Size, childErr.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handleList, 3 * IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw Failure("can't list the handles the program inherits");

            jobList = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(jobList, job);
            if (!UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_JOB_LIST, jobList, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw Failure("can't name the job the program is created in");

            var startup = new STARTUPINFOEX { lpAttributeList = attributes };
            startup.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
            startup.StartupInfo.hStdInput = childIn.DangerousGetHandle();
            startup.StartupInfo.hStdOutput = childOut.DangerousGetHandle();
            startup.StartupInfo.hStdError = childErr.DangerousGetHandle();

            if (request.Environment is { Count: > 0 } changes) environment = Marshal.StringToHGlobalUni(EnvironmentBlock(changes));

            var line = new StringBuilder(commandLine, commandLine.Length + 1); // CreateProcessW may write to it
            if (!CreateProcessW(program, line, IntPtr.Zero, IntPtr.Zero, true,
                    EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW,
                    environment, request.WorkingDirectory, ref startup, out var made))
                throw Failure($"can't start {program}");

            CloseHandle(made.hThread);
            var child = new ChildProcess(job, made.hProcess,
                new FileStream(ourIn, FileAccess.Write, 1, isAsync: false),
                new FileStream(ourOut, FileAccess.Read, 1, isAsync: false),
                new FileStream(ourErr, FileAccess.Read, 1, isAsync: false));
            job = IntPtr.Zero; // the child owns them now
            ourIn = ourOut = ourErr = null;
            return child;
        }
        finally
        {
            // The child has its own copies of its three pipe ends; ours must close, or its output would never end.
            childIn?.Dispose();
            childOut?.Dispose();
            childErr?.Dispose();
            ourIn?.Dispose();
            ourOut?.Dispose();
            ourErr?.Dispose();
            if (attributesReady) DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if (handleList != IntPtr.Zero) Marshal.FreeHGlobal(handleList);
            if (jobList != IntPtr.Zero) Marshal.FreeHGlobal(jobList);
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
            if (job != IntPtr.Zero) CloseHandle(job);
        }
    }

    private static Win32Exception Failure(string what)
    {
        var code = Marshal.GetLastPInvokeError();
        return new Win32Exception(code, $"{what}: {new Win32Exception(code).Message} (error {code})");
    }

    /// <summary>A pipe whose one end the child inherits and whose other end stays here.</summary>
    private static void Pipe(out SafeFileHandle read, out SafeFileHandle write, bool childReads)
    {
        var inheritable = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = 1 };
        if (!CreatePipe(out read, out write, ref inheritable, 0)) throw Failure("can't make a pipe to the program");
        if (!SetHandleInformation(childReads ? write : read, HANDLE_FLAG_INHERIT, 0)) throw Failure("can't keep a pipe end out of the program");
    }

    /// <summary>The caller's environment with the request's changes, in the sorted, double-null-ended form Windows takes.</summary>
    private static string EnvironmentBlock(IReadOnlyDictionary<string, string?> changes)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables()) variables[(string)entry.Key] = (string?)entry.Value ?? "";
        foreach (var (name, value) in changes)
        {
            if (value is null) variables.Remove(name);
            else variables[name] = value;
        }

        var block = new StringBuilder();
        foreach (var (name, value) in variables) block.Append(name).Append('=').Append(value).Append('\0');
        if (variables.Count == 0) block.Append('\0');
        return block.ToString(); // the marshaller adds the final null
    }

    /// <summary>The process handle as something the thread pool can wait on; it doesn't own the handle.</summary>
    private sealed class ProcessExit : WaitHandle
    {
        public ProcessExit(IntPtr process) => SafeWaitHandle = new SafeWaitHandle(process, ownsHandle: false);
    }

    private const int HANDLE_FLAG_INHERIT = 1;
    private const int STARTF_USESTDHANDLES = 0x100;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;
    private static readonly IntPtr PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x00020002;
    private static readonly IntPtr PROC_THREAD_ATTRIBUTE_JOB_LIST = 0x0002000D;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, IntPtr name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SECURITY_ATTRIBUTES attributes, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, int mask, int flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string? currentDirectory, ref STARTUPINFOEX startup, out PROCESS_INFORMATION made);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint code);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
