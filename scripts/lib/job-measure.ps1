# Measure-Job: run one command inside its own Windows job object and report what it cost.
#
#   . scripts/lib/job-measure.ps1
#   $r = Measure-Job -FilePath pwsh -ArgumentList '-NoProfile', '-File', 'scripts/build.ps1' -TimeoutSeconds 1800
#   $r.ExitCode, $r.Seconds, $r.PeakMB, $r.TimedOut
#
# The command starts suspended, is put in the job, and only then runs, so every process it ever starts is in the job
# too. PeakMB is the job's PeakJobMemoryUsed: the most memory the command and everything under it had committed at
# one moment. Seconds runs from the resume to the exit of the command itself. The job ends every process still in it
# when Measure-Job returns, on a normal exit, a timeout, an error or Ctrl+C alike (kill on job close), so nothing a
# measured command starts outlives the measurement.
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

    static Exception Fail(string what, string commandLine) {
      int error = Marshal.GetLastWin32Error();
      return new InvalidOperationException(what + " for: " + commandLine + " (Win32 error " + error + ": " + new Win32Exception(error).Message + ")");
    }

    public static JobResult Run(string commandLine, string workingDirectory, int timeoutSeconds, string outputPath) {
      IntPtr job = IntPtr.Zero, output = INVALID_HANDLE;
      PROCESS_INFORMATION process = new PROCESS_INFORMATION();
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

        Stopwatch watch = Stopwatch.StartNew();
        if (ResumeThread(process.hThread) == 0xFFFFFFFF) throw Fail("could not resume the command", commandLine);
        uint wait = WaitForSingleObject(process.hProcess, timeoutSeconds > 0 ? (uint)timeoutSeconds * 1000u : INFINITE);
        watch.Stop();
        if (wait == WAIT_FAILED) throw Fail("could not wait for the command", commandLine);

        JobResult result = new JobResult();
        result.Seconds = Math.Round(watch.Elapsed.TotalSeconds, 2);
        result.TimedOut = wait == WAIT_TIMEOUT;
        if (result.TimedOut) {
          TerminateJobObject(job, 1);
          WaitForSingleObject(process.hProcess, 10000);
          result.ExitCode = -1;
        } else {
          uint code;
          if (!GetExitCodeProcess(process.hProcess, out code)) throw Fail("could not read the command's exit code", commandLine);
          result.ExitCode = unchecked((int)code);
        }
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION used;
        if (!QueryInformationJobObject(job, JobObjectExtendedLimitInformation, out used, Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)), IntPtr.Zero))
          throw Fail("could not read the job's peak memory", commandLine);
        result.PeakMB = Math.Round(used.PeakJobMemoryUsed.ToUInt64() / 1048576.0, 1);
        return result;
      } finally {
        // Ending the job ends every process still in it; closing the handle would too, and this does not wait for that.
        if (job != IntPtr.Zero) { TerminateJobObject(job, 1); CloseHandle(job); }
        if (process.hThread != IntPtr.Zero) CloseHandle(process.hThread);
        if (process.hProcess != IntPtr.Zero) CloseHandle(process.hProcess);
        if (output != INVALID_HANDLE) CloseHandle(output);
      }
    }
  }
}
'@
}

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
    [string]$OutputPath
  )
  $command = Get-Command -Name $FilePath -CommandType Application -ErrorAction Ignore | Select-Object -First 1
  if (-not $command) { throw "Measure-Job could not start '$FilePath': no such program on PATH or at that path" }
  $line = (@($command.Source) + $ArgumentList | ForEach-Object { ConvertTo-CommandLineArgument $_ }) -join ' '
  [Bosun.JobMeasure]::Run($line, $WorkingDirectory, $TimeoutSeconds, $OutputPath)
}
