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
//   tree <file> <ms>            start a grandchild (`beat <file>`), then sleep
//   orphan <file>               start the same grandchild, wait until it has written, then exit: it is left with no parent
//   beat <file>                 write this process's id as the file's first line, then a line every 100 ms
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
    default:
        return 64;
}
