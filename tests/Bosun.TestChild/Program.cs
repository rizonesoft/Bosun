using System.Diagnostics;
using System.Text;
using System.Text.Json;

// The program the process runner's tests start. Each verb does one thing a real tool can do to its caller:
//
//   args <a> <b> ...            print the arguments received, as a JSON array
//   print <text> [<err>] [<n>]  write <text> to stdout and <err> to stderr as UTF-8, then exit <n> (0)
//   out <bytes> <bytes>         write that many bytes to stderr, then that many to stdout
//   stdin                       copy stdin to stdout
//   sleep <ms>                  sleep
//   exit <code>                 exit with the code
//   cwd                         print the current folder
//   env <name>                  print the variable's value, or nothing when it isn't set
//   (any, with BOSUN_FAKE_EXPECT) a fake bosun: see below
//   tree <file> <ms>            start a grandchild (`beat <file>`), then sleep
//   orphan <file>               start the same grandchild, wait until it has written, then exit: it is left with no parent
//   beat <file>                 write this process's id as the file's first line, then a line every 100 ms
//   hold-desktop <name> <folder> take the desktop lock <name> with its record in <folder> (D02 T02 §11), write `held`,
//                               wait for a file `release` (at most 60 s), release it and write `released`
//   probe-desktop <name>        write `taken` or `free` to `probe`: whether the lock is held elsewhere at this moment
//
// `beat` writes at most 100 lines (ten seconds) and exits, so a grandchild nothing stops still ends.
var stdout = Console.OpenStandardOutput();
var stderr = Console.OpenStandardError();
var utf8 = new UTF8Encoding(false);

void Write(Stream stream, string text)
{
    var bytes = utf8.GetBytes(text);
    stream.Write(bytes, 0, bytes.Length);
    stream.Flush();
}

void Flood(Stream stream, long count, byte value)
{
    var block = new byte[64 * 1024];
    Array.Fill(block, value);
    for (var left = count; left > 0; left -= block.Length) stream.Write(block, 0, (int)Math.Min(block.Length, left));
    stream.Flush();
}

Process Beat(string file)
{
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add("beat");
    start.ArgumentList.Add(file);
    return Process.Start(start)!;
}

int Lines(string file)
{
    try
    {
        using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
    }
    catch (IOException)
    {
        return 0; // not written yet
    }
}

// A fake tool asked several things (D03 T02 §1: bosun.exe and wsl.exe): with BOSUN_FAKE_SCRIPT set (a JSON object from
// the arguments, joined by single spaces, to an answer {"answer": <file>, "exit": <n>, "stderr": <text>}), the child
// appends its arguments as one JSON line to BOSUN_FAKE_LOG, when that is set, then answers as the script says. Arguments
// the script has no answer for: exit 64, naming them, so a caller that asked something unplanned fails.
if (Environment.GetEnvironmentVariable("BOSUN_FAKE_SCRIPT") is { } script)
{
    // Callers ask several things at once: an append that meets another child's is tried again, never lost.
    if (Environment.GetEnvironmentVariable("BOSUN_FAKE_LOG") is { Length: > 0 } log)
        for (var tries = 0; ; tries++)
        {
            try
            {
                File.AppendAllText(log, JsonSerializer.Serialize(args) + "\n");
                break;
            }
            catch (IOException) when (tries < 100)
            {
                Thread.Sleep(10);
            }
        }
    using var plan = JsonDocument.Parse(script);
    if (!plan.RootElement.TryGetProperty(string.Join(' ', args), out var reply))
    {
        Write(stderr, "fake tool: no answer for " + JsonSerializer.Serialize(args));
        return 64;
    }
    if (reply.TryGetProperty("stderr", out var err)) Write(stderr, err.GetString() ?? "");
    if (reply.TryGetProperty("answer", out var path) && path.GetString() is { Length: > 0 } scripted)
    {
        using var file = File.OpenRead(scripted);
        file.CopyTo(stdout);
        stdout.Flush();
    }
    return reply.TryGetProperty("exit", out var exit) ? exit.GetInt32() : 0;
}

// A fake bosun (D03 T01 §1): with BOSUN_FAKE_EXPECT set (a JSON array), the child checks it was given exactly those
// arguments, then sleeps BOSUN_FAKE_SLEEP_MS, writes BOSUN_FAKE_STDERR and the file BOSUN_FAKE_ANSWER, and exits
// BOSUN_FAKE_EXIT (0). Other arguments: exit 64, naming the ones it got, so a caller that asked wrongly fails.
if (Environment.GetEnvironmentVariable("BOSUN_FAKE_EXPECT") is { } expect)
{
    var wanted = JsonSerializer.Deserialize<string[]>(expect) ?? [];
    if (!wanted.SequenceEqual(args))
    {
        Write(stderr, "fake bosun: expected " + expect + ", got " + JsonSerializer.Serialize(args));
        return 64;
    }
    if (int.TryParse(Environment.GetEnvironmentVariable("BOSUN_FAKE_SLEEP_MS"), out var nap)) Thread.Sleep(nap);
    if (Environment.GetEnvironmentVariable("BOSUN_FAKE_STDERR") is { Length: > 0 } said) Write(stderr, said);
    if (Environment.GetEnvironmentVariable("BOSUN_FAKE_ANSWER") is { Length: > 0 } answer)
    {
        using var file = File.OpenRead(answer);
        file.CopyTo(stdout);
        stdout.Flush();
    }
    return int.TryParse(Environment.GetEnvironmentVariable("BOSUN_FAKE_EXIT"), out var code) ? code : 0;
}

if (args.Length == 0) return 64;
switch (args[0])
{
    case "args":
        Write(stdout, JsonSerializer.Serialize(args[1..]));
        return 0;
    case "print":
        Write(stdout, args[1]);
        if (args.Length > 2) Write(stderr, args[2]);
        return args.Length > 3 ? int.Parse(args[3]) : 0;
    case "out":
        Flood(stderr, long.Parse(args[2]), (byte)'e');
        Flood(stdout, long.Parse(args[1]), (byte)'o');
        return 0;
    case "stdin":
        Console.OpenStandardInput().CopyTo(stdout);
        stdout.Flush();
        return 0;
    case "sleep":
        Thread.Sleep(int.Parse(args[1]));
        return 0;
    case "exit":
        return int.Parse(args[1]);
    case "cwd":
        Write(stdout, Environment.CurrentDirectory);
        return 0;
    case "env":
        Write(stdout, Environment.GetEnvironmentVariable(args[1]) ?? "");
        return 0;
    case "tree":
        Beat(args[1]);
        Thread.Sleep(int.Parse(args[2]));
        return 0;
    case "orphan":
        Beat(args[1]);
        for (var tries = 0; tries < 200 && Lines(args[1]) < 2; tries++) Thread.Sleep(50);
        return Lines(args[1]) >= 2 ? 0 : 70;
    case "beat":
        File.AppendAllText(args[1], Environment.ProcessId + "\n");
        for (var beat = 0; beat < 100; beat++)
        {
            File.AppendAllText(args[1], "beat\n");
            Thread.Sleep(100);
        }
        return 0;
    case "hold-desktop":
        using (Bosun.Desktop.DesktopLock.Acquire(args[1], args[2], TimeSpan.FromSeconds(60)))
        {
            File.WriteAllText("held", Environment.ProcessId.ToString());
            for (var waited = 0; waited < 1200 && !File.Exists("release"); waited++) Thread.Sleep(50);
        }
        File.WriteAllText("released", "");
        return 0;
    case "probe-desktop":
        File.WriteAllText("probe", Probe(args[1]));
        return 0;
    default:
        return 64;
}

static string Probe(string name)
{
    using var mutex = new Mutex(false, @"Local\" + name);
    bool owned;
    try
    {
        owned = mutex.WaitOne(0);
    }
    catch (AbandonedMutexException)
    {
        owned = true;
    }
    if (!owned) return "taken";
    mutex.ReleaseMutex();
    return "free";
}
