// One desktop owner at a time (D02 T02 §11). The end-to-end smoke (tests/Bosun.Panel.E2E) and scripts/capture-window.ps1
// both take the foreground, the pointer and the keyboard, so two of them at once take each other's windows. Each holds
// the mutex Local\<name> (the input desktop belongs to one logon session) for as long as it uses the desktop, and
// publishes a record naming itself, since a mutex can't name its owner (the queue's pattern, D01 T01 §1).
//
// One source for both consumers: the smoke compiles it in, Bosun.TestChild links it for its helpers, and
// capture-window.ps1 loads it with Add-Type, so the rules can't drift between C# and PowerShell. It keeps to what
// Add-Type compiles: no top-level statements, no file-scoped namespace.
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Bosun.Desktop
{
    /// <summary>A setting the lock can't use, or a wait that ran out: its message says which, and what to do.</summary>
    public sealed class DesktopLockException : Exception
    {
        public DesktopLockException(string message, bool timedOut = false) : base(message) => TimedOut = timedOut;

        /// <summary>True for a wait that ran out, false for a setting refused.</summary>
        public bool TimedOut { get; }
    }

    /// <summary>The lock's name and wait, from <c>BOSUN_DESKTOP_LOCK</c> and <c>BOSUN_DESKTOP_WAIT_S</c>.</summary>
    public sealed class DesktopSettings
    {
        public DesktopSettings(string name, TimeSpan wait) => (Name, Wait) = (name, wait);

        public string Name { get; }

        public TimeSpan Wait { get; }

        public static DesktopSettings FromEnvironment() =>
            new DesktopSettings(DesktopLock.ParseName(Environment.GetEnvironmentVariable(DesktopLock.LockVariable)),
                DesktopLock.ParseWait(Environment.GetEnvironmentVariable(DesktopLock.WaitVariable)));
    }

    /// <summary>The lock itself: take it, describe who has it, and tell whether a parent's hold covers this process.</summary>
    public static class DesktopLock
    {
        public const string DefaultName = "bosun-desktop";
        public const string LockVariable = "BOSUN_DESKTOP_LOCK";
        public const string WaitVariable = "BOSUN_DESKTOP_WAIT_S";
        public const string HeldVariable = "BOSUN_DESKTOP_HELD";
        public const int DefaultWaitSeconds = 900;
        public const int MostWaitSeconds = 86400;
        public const string Unnamed = "an unnamed holder";

        /// <summary>
        /// A name is 1 to 64 of a-z, 0-9 and '-'. Mutex names are case-sensitive and file names are not, so a name
        /// kept to lower case can't put two locks in one record file. Empty or unset is the default.
        /// </summary>
        public static string ParseName(string? value)
        {
            if (value is null || value.Length == 0) return DefaultName;
            if (value.Length > 64) throw new DesktopLockException($"{LockVariable} is {value.Length} characters; a lock name is at most 64");
            foreach (var c in value)
            {
                if (!(c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '-'))
                {
                    throw new DesktopLockException($"{LockVariable} is '{value}'; a lock name is lower-case letters, digits and '-'");
                }
            }
            return value;
        }

        /// <summary>A whole number of seconds from 0 to 86400; 0 refuses at once when the lock is taken; empty or unset is 900.</summary>
        public static TimeSpan ParseWait(string? value)
        {
            if (value is null || value.Length == 0) return TimeSpan.FromSeconds(DefaultWaitSeconds);
            foreach (var c in value)
            {
                if (c < '0' || c > '9') throw new DesktopLockException($"{WaitVariable} is '{value}'; it takes a whole number of seconds from 0 to {MostWaitSeconds}");
            }
            if (value.Length > 5 || int.Parse(value, CultureInfo.InvariantCulture) > MostWaitSeconds)
            {
                throw new DesktopLockException($"{WaitVariable} is '{value}'; it takes a whole number of seconds from 0 to {MostWaitSeconds}");
            }
            return TimeSpan.FromSeconds(int.Parse(value, CultureInfo.InvariantCulture));
        }

        /// <summary>Where this logon session's records go: <c>%LOCALAPPDATA%\Bosun\run\&lt;session&gt;\desktop</c>.</summary>
        public static string RecordFolder()
        {
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (string.IsNullOrEmpty(local)) local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            using var me = Process.GetCurrentProcess();
            return Path.Combine(local, "Bosun", "run", me.SessionId.ToString(CultureInfo.InvariantCulture), "desktop");
        }

        public static string RecordPath(string name, string folder) => Path.Combine(folder, name + ".json");

        /// <summary>
        /// Take <c>Local\&lt;name&gt;</c>, waiting up to <paramref name="wait"/>. When it is taken on arrival,
        /// <paramref name="onWaiting"/> hears once whom this waits for. An abandoned mutex (its holder died) is taken.
        /// The record is written only while holding, and its failures go to <paramref name="errors"/> (stderr when
        /// null) and change nothing: the mutex is the only authority.
        /// </summary>
        public static DesktopHolder Acquire(string name, string folder, TimeSpan wait, Action<string>? onWaiting = null, TextWriter? errors = null)
        {
            ParseName(name);
            errors ??= Console.Error;
            var mutex = new Mutex(false, @"Local\" + name);
            var owned = false;
            try
            {
                owned = Take(mutex, TimeSpan.Zero);
                if (!owned)
                {
                    if (wait > TimeSpan.Zero) onWaiting?.Invoke($"the desktop is held by {Describe(name, folder)}; waiting up to {(int)wait.TotalSeconds} s for it ({WaitVariable})");
                    owned = wait > TimeSpan.Zero && Take(mutex, wait);
                }
                if (!owned)
                {
                    throw new DesktopLockException(
                        $"the desktop is held by {Describe(name, folder)}, and {(int)wait.TotalSeconds} s ({WaitVariable}) went by without it", timedOut: true);
                }
                var holder = new DesktopHolder(mutex, name, folder, errors);
                holder.Publish();
                return holder;
            }
            catch
            {
                if (owned) mutex.ReleaseMutex();
                mutex.Dispose();
                throw;
            }
        }

        private static bool Take(Mutex mutex, TimeSpan wait)
        {
            try
            {
                return mutex.WaitOne(wait);
            }
            catch (AbandonedMutexException)
            {
                return true;  // its holder died holding it: this thread owns it now
            }
        }

        /// <summary>The holder its record names, when that process still runs with that start time; otherwise "an unnamed holder".</summary>
        public static string Describe(string name, string folder)
        {
            var record = ReadRecord(RecordPath(name, folder));
            if (record is null || !Alive(record.Pid, record.ProcessStart)) return Unnamed;
            var started = DateTime.FromFileTimeUtc((long)record.ProcessStart).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            return $"pid {record.Pid} ({Shorten(record.Command)}), started {started}";
        }

        /// <summary>A command on one line and at most 160 characters, for a message: the record keeps it whole.</summary>
        public static string Shorten(string command)
        {
            var line = new StringBuilder(command.Length);
            foreach (var c in command)
            {
                if (char.IsWhiteSpace(c)) { if (line.Length > 0 && line[line.Length - 1] != ' ') line.Append(' '); }
                else line.Append(c);
            }
            var text = line.ToString().TrimEnd();
            return text.Length <= 160 ? text : text.Substring(0, 157) + "...";
        }

        /// <summary>
        /// Whether a parent's hold covers this process: <c>BOSUN_DESKTOP_HELD</c> is <c>&lt;name&gt;:&lt;pid&gt;:&lt;process_start&gt;</c>,
        /// the name is this lock's, that process runs with that start time and is an ancestor of this one, and the lock's
        /// record names the same pid and start time. Anything less takes the lock as usual.
        /// </summary>
        public static bool InheritsHold(string name, string folder)
        {
            var held = Environment.GetEnvironmentVariable(HeldVariable);
            if (string.IsNullOrEmpty(held)) return false;
            var parts = held.Split(':');
            if (parts.Length != 3 || parts[0] != name) return false;
            if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) return false;
            if (!ulong.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var start)) return false;
            if (!Alive(pid, start)) return false;
            var record = ReadRecord(RecordPath(name, folder));
            if (record is null || record.Pid != pid || record.ProcessStart != start) return false;
            return IsAncestor(pid, start);
        }

        /// <summary>The value a holder hands its children in <c>BOSUN_DESKTOP_HELD</c>.</summary>
        public static string HeldValue(string name, int pid, ulong processStart) =>
            $"{name}:{pid.ToString(CultureInfo.InvariantCulture)}:{processStart.ToString(CultureInfo.InvariantCulture)}";

        /// <summary>A process's creation time as a FILETIME, the queue's form (src/core.hpp, Record::process_start); null when it is gone.</summary>
        public static ulong? StartOf(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return (ulong)process.StartTime.ToFileTimeUtc();
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }

        public static bool Alive(int pid, ulong processStart) => StartOf(pid) == processStart;

        internal sealed class Record
        {
            public int Pid;
            public ulong ProcessStart;
            public string Command = "";
        }

        /// <summary>The record, or null when it is missing, unreadable or not one.</summary>
        internal static Record? ReadRecord(string path)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;
                if (!root.TryGetProperty("schema_version", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1) return null;
                if (!root.TryGetProperty("pid", out var pid) || pid.ValueKind != JsonValueKind.Number) return null;
                if (!root.TryGetProperty("process_start", out var start) || start.ValueKind != JsonValueKind.Number) return null;
                var command = root.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
                return new Record { Pid = pid.GetInt32(), ProcessStart = start.GetUInt64(), Command = command };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException or ArgumentException)
            {
                return null;
            }
        }

        private static bool IsAncestor(int pid, ulong start)
        {
            var parents = ParentsByPid();
            using var me = Process.GetCurrentProcess();
            var current = me.Id;
            var currentStart = (ulong)me.StartTime.ToFileTimeUtc();
            for (var depth = 0; depth < 64; depth++)
            {
                if (!parents.TryGetValue(current, out var parent) || parent == 0) return false;
                // A parent id can be reused: a real parent started before its child.
                if (StartOf(parent) is not { } parentStart || parentStart > currentStart) return false;
                if (parent == pid) return parentStart == start;
                (current, currentStart) = (parent, parentStart);
            }
            return false;
        }

        private static Dictionary<int, int> ParentsByPid()
        {
            var parents = new Dictionary<int, int>();
            var snapshot = CreateToolhelp32Snapshot(0x2, 0);  // TH32CS_SNAPPROCESS
            if (snapshot == new IntPtr(-1)) return parents;
            try
            {
                var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
                for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                {
                    parents[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
                }
            }
            finally
            {
                CloseHandle(snapshot);
            }
            return parents;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }

    /// <summary>
    /// The lock, held. Dispose it on the thread that took it (a mutex belongs to its thread): it removes the record if
    /// the record is still this process's, then releases, whatever the removal did.
    /// </summary>
    public sealed class DesktopHolder : IDisposable
    {
        private readonly Mutex _mutex;
        private readonly TextWriter _errors;
        private bool _disposed;

        internal DesktopHolder(Mutex mutex, string name, string folder, TextWriter errors)
        {
            (_mutex, Name, _errors) = (mutex, name, errors);
            RecordPath = DesktopLock.RecordPath(name, folder);
            using var me = Process.GetCurrentProcess();
            Pid = me.Id;
            ProcessStart = (ulong)me.StartTime.ToFileTimeUtc();
        }

        public string Name { get; }

        public string RecordPath { get; }

        public int Pid { get; }

        public ulong ProcessStart { get; }

        /// <summary>What this holder hands its children in <c>BOSUN_DESKTOP_HELD</c>.</summary>
        public string HeldValue => DesktopLock.HeldValue(Name, Pid, ProcessStart);

        internal void Publish()
        {
            var temporary = RecordPath + "." + Pid.ToString(CultureInfo.InvariantCulture) + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(RecordPath)!);
                using (var stream = new MemoryStream())
                {
                    using (var json = new Utf8JsonWriter(stream))
                    {
                        json.WriteStartObject();
                        json.WriteNumber("schema_version", 1);
                        json.WriteNumber("pid", Pid);
                        json.WriteNumber("process_start", ProcessStart);
                        json.WriteString("command", Environment.CommandLine);
                        if (Repo() is { } repo) json.WriteString("repo", repo); else json.WriteNull("repo");
                        json.WriteString("acquired_at", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
                        json.WriteEndObject();
                    }
                    File.WriteAllBytes(temporary, stream.ToArray());
                }
                File.Move(temporary, RecordPath, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _errors.WriteLine($"desktop lock: the lock is held, but its record {RecordPath} couldn't be written: {e.Message}");
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception left) when (left is IOException or UnauthorizedAccessException)
                {
                    _errors.WriteLine($"desktop lock: its unfinished record {temporary} couldn't be removed: {left.Message}");
                }
            }
        }

        private static string? Repo()
        {
            for (var folder = new DirectoryInfo(Environment.CurrentDirectory); folder is not null; folder = folder.Parent)
            {
                if (Directory.Exists(Path.Combine(folder.FullName, ".git")) || File.Exists(Path.Combine(folder.FullName, ".git"))) return folder.FullName;
            }
            return null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (DesktopLock.ReadRecord(RecordPath) is { } record && record.Pid == Pid && record.ProcessStart == ProcessStart) File.Delete(RecordPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _errors.WriteLine($"desktop lock: its record {RecordPath} couldn't be removed: {e.Message}");
            }
            finally
            {
                _mutex.ReleaseMutex();
                _mutex.Dispose();
            }
        }
    }

    /// <summary>
    /// The lock held on a thread of its own, for a holder whose start and end may run on different threads (an xunit
    /// fixture): that thread takes the mutex, waits for <see cref="Dispose"/>, and releases it.
    /// </summary>
    public sealed class DesktopHold : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _held = new ManualResetEventSlim();
        private readonly ManualResetEventSlim _release = new ManualResetEventSlim();
        private DesktopHolder? _holder;
        private Exception? _failure;
        private bool _disposed;

        public DesktopHold(string name, string folder, TimeSpan wait, Action<string>? onWaiting = null, TextWriter? errors = null)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    _holder = DesktopLock.Acquire(name, folder, wait, onWaiting, errors);
                }
                catch (Exception e)
                {
                    _failure = e;
                    _held.Set();
                    return;
                }
                _held.Set();
                _release.Wait();
                _holder.Dispose();
            }) { IsBackground = true, Name = "desktop lock" };
            _thread.Start();
            _held.Wait();
            if (_failure is not null)
            {
                _thread.Join();
                throw _failure;
            }
        }

        public DesktopHolder Holder => _holder!;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _release.Set();
            if (!_thread.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("the desktop lock's thread didn't release it within 10 s");
            _held.Dispose();
            _release.Dispose();
        }
    }
}
