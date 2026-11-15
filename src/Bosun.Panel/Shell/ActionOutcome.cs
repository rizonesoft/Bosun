using System.Globalization;
using Bosun.Core;

namespace Bosun.Panel.Shell;

/// <summary>
/// How a panel action ended (D02 T02 §4): done, or failed with the reason. An action returns one, because the tools
/// the panel runs never throw: a nonzero exit, a timeout or a program that could not be started is a typed result,
/// and an action that only threw on failure would write INFO for each of them.
/// </summary>
public sealed record ActionOutcome
{
    private ActionOutcome(bool succeeded, string? reason)
    {
        Succeeded = succeeded;
        Reason = reason;
    }

    public bool Succeeded { get; }

    /// <summary>Why it failed, in words for the person; for a done action, an optional detail of what was done.</summary>
    public string? Reason { get; }

    public static ActionOutcome Done { get; } = new(true, null);

    /// <summary>Done, with a detail worth a line ("slots 3").</summary>
    public static ActionOutcome DoneWith(string detail) => new(true, Words(detail, nameof(detail)));

    public static ActionOutcome Failed(string reason) => new(false, Words(reason, nameof(reason)));

    private static string Words(string text, string name) =>
        string.IsNullOrWhiteSpace(text) ? throw new ArgumentException("an outcome's words can't be empty", name) : text.Trim();

    /// <summary>
    /// The outcome of an action that ran a tool: exit 0 is done; anything else failed, with the tool's own first line
    /// of stderr when it gave one, since that is the message a failed state quotes (design/SPEC.md §1).
    /// </summary>
    public static ActionOutcome From(ProcessRequest request, ProcessResult result)
    {
        var tool = System.IO.Path.GetFileNameWithoutExtension(request.Program);
        switch (result.Outcome)
        {
            case ProcessOutcome.Exited when result.ExitCode == 0:
                return Done;
            case ProcessOutcome.Exited:
                var said = result.StandardError.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
                return Failed(said ?? string.Create(CultureInfo.InvariantCulture, $"{tool} exited with code {result.ExitCode}"));
            case ProcessOutcome.TimedOut:
                return Failed(string.Create(CultureInfo.InvariantCulture, $"{tool} did not finish within {request.Timeout.TotalSeconds:0.###} s and was ended"));
            case ProcessOutcome.Cancelled:
                return Failed($"{tool} was cancelled");
            default:
                return Failed(string.IsNullOrWhiteSpace(result.Reason) ? $"{tool} could not be started" : $"{tool} could not be started: {result.Reason.Trim()}");
        }
    }
}
