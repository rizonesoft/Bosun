# Measure-Job: run one command inside its own Windows job object and report what it cost.
#
#   . scripts/lib/job-measure.ps1
#   $r = Measure-Job -FilePath pwsh -ArgumentList '-NoProfile', '-File', 'scripts/build.ps1' -TimeoutSeconds 1800
#   $r.ExitCode, $r.Seconds, $r.PeakMB, $r.TimedOut
#
# The command starts suspended, is put in the job, and only then runs, so every process it ever starts is in the job
# too. PeakMB is the job's PeakJobMemoryUsed: the most memory the command and everything under it had committed at
# one moment. Seconds runs from the resume to the exit of the command itself. The job ends every process still in it
# when Measure-Job returns, on a normal exit, a timeout, an error or Ctrl+C alike, so nothing a measured command starts
# outlives the measurement. An interruption is seen within a fifth of a second, not when the command ends.
#
# -OutputPath sends the command's output and errors to that file; without it they go to this console.

if (-not ('Bosun.JobMeasure' -as [type])) {
  Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Bosun {
  public sealed class JobResult {
    public int ExitCode;
    public double Seconds;
    public double PeakMB;
    public bool TimedOut;
    public double QueuedSeconds;    // how long the command waited to start its work (see -StartedPath); 0 without one
    public bool TimedOutInQueue;    // it was stopped while still waiting, before its work began
  }

  public static class JobMeasure {
    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION {
      public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
      public uint LimitFlags;
      public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
      public uint ActiveProcessLimit;
      public UIntPtr Affinity;
      public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS { public ulong a, b, c, d, e, f; }
    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION {
      public JOBOBJECT_BASIC_LIMIT_INFORMATION Basic;
      public IO_COUNTERS Io;
      public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO {
      public int cb;
      public string lpReserved, lpDesktop, lpTitle;
      public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
      public short wShowWindow, cbReserved2;
      public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential)]
    struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }

    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    const int JobObjectExtendedLimitInformation = 9;
    const uint CREATE_SUSPENDED = 0x4, CREATE_UNICODE_ENVIRONMENT = 0x400;
    const int STARTF_USESTDHANDLES = 0x100;
    const uint GENERIC_WRITE = 0x40000000, FILE_SHARE_READ = 1, CREATE_ALWAYS = 2;
    const uint WAIT_TIMEOUT = 0x102, WAIT_FAILED = 0xFFFFFFFF, INFINITE = 0xFFFFFFFF;
    static readonly IntPtr INVALID_HANDLE = new IntPtr(-1);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateJobObjectW(IntPtr attributes, string name);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool QueryInformationJobObject(IntPtr job, int infoClass, out JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length, IntPtr returned);
    [DllImport("kernel32", SetLastError = true, EntryPoint = "QueryInformationJobObject")]
    static extern bool QueryJobProcessIds(IntPtr job, int infoClass, IntPtr list, int length, IntPtr returned);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool TerminateJobObject(IntPtr job, uint exitCode);
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(string application, System.Text.StringBuilder commandLine, IntPtr processAttributes,
      IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string currentDirectory,
      ref STARTUPINFO startup, out PROCESS_INFORMATION info);
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, ref SECURITY_ATTRIBUTES security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32", SetLastError = true)]
    static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32", SetLastError = true)]
    static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32")]
    static extern IntPtr GetStdHandle(int which);
    [DllImport("kernel32")]
    static extern IntPtr GetCurrentProcess();

    static IntPtr ownJob = IntPtr.Zero;

    // Put this process in a job of its own that ends every process in it when this process ends, however it ends
    // (its window closed, killed, or a normal exit): the job's only handle is this process's, so Windows closes it
    // then. Everything this process starts from now on is in the job, and no breakaway is allowed. Once only.
    public static void EndTreeWithThisProcess() {
      if (ownJob != IntPtr.Zero) return;
      IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
      if (job == IntPtr.Zero) throw Fail("could not create a job object", "this process");
      JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
      limits.Basic.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
      if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf(limits))
          || !AssignProcessToJobObject(job, GetCurrentProcess())) {
        Exception failure = Fail("could not put this process in a job that ends with it", "this process");
        CloseHandle(job);
        throw failure;
      }
      ownJob = job;
    }

    static Exception Fail(string what, string commandLine) {
      int error = Marshal.GetLastWin32Error();
      return new InvalidOperationException(what + " for: " + commandLine + " (Win32 error " + error + ": " + new Win32Exception(error).Message + ")");
    }

    // One command in its job. Start it, call Wait in short slices until it returns true (so the caller can be
    // interrupted between them), then Finish for the result. Dispose ends every process still in the job and is
    // safe to call at any point, which is what makes an interrupted or failed caller leave nothing running.
    public sealed class Run : IDisposable {
      IntPtr job = IntPtr.Zero, output = INVALID_HANDLE;
      PROCESS_INFORMATION process = new PROCESS_INFORMATION();
      readonly string commandLine;
      Stopwatch watch;
      bool exited;

      public Run(string commandLine, string workingDirectory, string outputPath) {
        this.commandLine = commandLine;
        try {
          job = CreateJobObjectW(IntPtr.Zero, null);
          if (job == IntPtr.Zero) throw Fail("could not create a job object", commandLine);
          JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
          limits.Basic.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
          if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf(limits)))
            throw Fail("could not set the job to end its processes on close", commandLine);

          STARTUPINFO startup = new STARTUPINFO();
          startup.cb = Marshal.SizeOf(typeof(STARTUPINFO));
          if (!String.IsNullOrEmpty(outputPath)) {
            SECURITY_ATTRIBUTES inherit = new SECURITY_ATTRIBUTES();
            inherit.nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES));
            inherit.bInheritHandle = 1;
            output = CreateFileW(outputPath, GENERIC_WRITE, FILE_SHARE_READ, ref inherit, CREATE_ALWAYS, 0x80, IntPtr.Zero);
            if (output == INVALID_HANDLE) throw Fail("could not open the output file " + outputPath, commandLine);
            startup.dwFlags = STARTF_USESTDHANDLES;
            startup.hStdInput = GetStdHandle(-10);
            startup.hStdOutput = output;
            startup.hStdError = output;
          }

          if (!CreateProcessW(null, new System.Text.StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, true,
                              CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT, IntPtr.Zero, workingDirectory, ref startup, out process))
            throw Fail("could not start the command", commandLine);
          if (!AssignProcessToJobObject(job, process.hProcess)) {
            Exception failure = Fail("could not put the command in its job", commandLine);
            TerminateProcess(process.hProcess, 1);
            throw failure;
          }
          watch = Stopwatch.StartNew();
          if (ResumeThread(process.hThread) == 0xFFFFFFFF) throw Fail("could not resume the command", commandLine);
        } catch {
          Dispose();
          throw;
        }
      }

      // True once the command itself has exited. Returns at that moment, or after the given milliseconds.
      public bool Wait(int milliseconds) {
        if (exited) return true;
        uint wait = WaitForSingleObject(process.hProcess, (uint)Math.Max(milliseconds, 0));
        if (wait == WAIT_FAILED) throw Fail("could not wait for the command", commandLine);
        if (wait != WAIT_TIMEOUT) { exited = true; watch.Stop(); }
        return exited;
      }

      public double ElapsedSeconds { get { return watch.Elapsed.TotalSeconds; } }

      // The ids of the processes in the job now. They are live members of this job, so none can be a number reused
      // by an unrelated process; anything the command started is here, whenever it started.
      public int[] ProcessIds() {
        const int JobObjectBasicProcessIdList = 3;
        for (int room = 64; ; room *= 4) {
          int size = 8 + room * IntPtr.Size;
          IntPtr buffer = Marshal.AllocHGlobal(size);
          try {
            if (QueryJobProcessIds(job, JobObjectBasicProcessIdList, buffer, size, IntPtr.Zero)) {
              int count = Marshal.ReadInt32(buffer, 4);  // NumberOfProcessIdsInList
              int[] ids = new int[count];
              for (int i = 0; i < count; i++) ids[i] = (int)Marshal.ReadIntPtr(buffer, 8 + i * IntPtr.Size).ToInt64();
              return ids;
            }
            if (Marshal.GetLastWin32Error() != 234 || room > 65536) throw Fail("could not list the processes in the job", commandLine);  // 234: more data
          } finally { Marshal.FreeHGlobal(buffer); }
        }
      }

      // The result. With timedOut the job is ended first, and the exit code is -1.
      public JobResult Finish(bool timedOut) {
        JobResult result = new JobResult();
        result.TimedOut = timedOut;
        if (timedOut) {
          watch.Stop();
          TerminateJobObject(job, 1);
          WaitForSingleObject(process.hProcess, 10000);
          result.ExitCode = -1;
        } else {
          uint code;
          if (!GetExitCodeProcess(process.hProcess, out code)) throw Fail("could not read the command's exit code", commandLine);
          result.ExitCode = unchecked((int)code);
        }
        result.Seconds = Math.Round(watch.Elapsed.TotalSeconds, 2);
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION used;
        if (!QueryInformationJobObject(job, JobObjectExtendedLimitInformation, out used, Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)), IntPtr.Zero))
          throw Fail("could not read the job's peak memory", commandLine);
        result.PeakMB = Math.Round(used.PeakJobMemoryUsed.ToUInt64() / 1048576.0, 1);
        return result;
      }

      public void Dispose() {
        // Ending the job ends every process still in it; closing the handle would too, and this does not wait for that.
        if (job != IntPtr.Zero) { TerminateJobObject(job, 1); CloseHandle(job); job = IntPtr.Zero; }
        if (process.hThread != IntPtr.Zero) { CloseHandle(process.hThread); process.hThread = IntPtr.Zero; }
        if (process.hProcess != IntPtr.Zero) { CloseHandle(process.hProcess); process.hProcess = IntPtr.Zero; }
        if (output != INVALID_HANDLE) { CloseHandle(output); output = INVALID_HANDLE; }
      }
    }
  }
}
'@
}

# Stop-TreeWithThisWindow: from here on, everything this PowerShell process starts ends when the process does, so a
# closed window leaves nothing running (scripts/start-run.ps1). Windows does it, through a job object, so it holds for
# a window that is closed or a process that is killed, where no script gets to clean up.
function Stop-TreeWithThisWindow { [Bosun.JobMeasure]::EndTreeWithThisProcess() }

# One argument as CreateProcess and the C runtime read it back: quoted when it has a space, a tab or a quote, with the
# backslashes before a quote doubled.
function ConvertTo-CommandLineArgument([string]$Argument) {
  if ($Argument -ne '' -and $Argument -notmatch '[\s"]') { return $Argument }
  '"' + ($Argument -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"'
}

function Measure-Job {
  param(
    [Parameter(Mandatory)][string]$FilePath,
    [string[]]$ArgumentList = @(),
    [string]$WorkingDirectory = (Get-Location).ProviderPath,
    [int]$TimeoutSeconds = 0,
    [string]$OutputPath,
    # For a command that waits its turn before it works (one run through `bosun run --`): the command creates the
    # file at -StartedPath when its work begins. Until then it may wait -QueueSeconds; from then on -TimeoutSeconds
    # counts. Without these, -TimeoutSeconds covers the whole run.
    [string]$StartedPath,
    [int]$QueueSeconds = 0
  )
  $command = Get-Command -Name $FilePath -CommandType Application -ErrorAction Ignore | Select-Object -First 1
  if (-not $command) { throw "Measure-Job could not start '$FilePath': no such program on PATH or at that path" }
  $line = (@($command.Source) + $ArgumentList | ForEach-Object { ConvertTo-CommandLineArgument $_ }) -join ' '
  $run = [Bosun.JobMeasure+Run]::new($line, $WorkingDirectory, $OutputPath)
  try {
    # Waited for in short slices, so Ctrl+C (or a stopped pipeline) is seen within one of them: the finally below
    # then ends the job at once. One long native wait would hold PowerShell until the command ended by itself.
    $timedOut = $false
    $waiting = [bool]$StartedPath
    $began = 0.0
    while (-not $run.Wait(200)) {
      $elapsed = $run.ElapsedSeconds
      if ($waiting -and (Test-Path -LiteralPath $StartedPath)) { $waiting = $false; $began = $elapsed }
      if ($waiting) { if ($QueueSeconds -gt 0 -and $elapsed -ge $QueueSeconds) { $timedOut = $true; break } }
      elseif ($TimeoutSeconds -gt 0 -and ($elapsed - $began) -ge $TimeoutSeconds) { $timedOut = $true; break }
    }
    if ($waiting -and (Test-Path -LiteralPath $StartedPath)) { $waiting = $false; $began = $run.ElapsedSeconds }
    $result = $run.Finish($timedOut)
    if ($StartedPath) {
      $result.QueuedSeconds = [Math]::Round($(if ($waiting) { $result.Seconds } else { $began }), 2)
      $result.TimedOutInQueue = $timedOut -and $waiting
    }
    $result
  } finally { $run.Dispose() }
}
