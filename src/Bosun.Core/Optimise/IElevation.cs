namespace Bosun.Core.Optimise;

/// <summary>The fixed command the elevated helper is started with: <c>Bosun.Panel.exe --optimise &lt;apply|undo&gt; &lt;item id&gt; &lt;invocation id&gt;</c>, and nothing else.</summary>
public sealed record HelperCommand(string Operation, string ItemId, string Invocation)
{
    public const string Switch = "--optimise";

    public IReadOnlyList<string> Arguments => [Switch, Operation, ItemId, Invocation];
}

/// <summary>How an attempt to start the elevated helper ended, before it ran.</summary>
public enum ElevationStartKind
{
    /// <summary>The helper is running (the administrator prompt was accepted).</summary>
    Running,

    /// <summary>The person declined the administrator prompt.</summary>
    Declined,

    /// <summary>The helper could not be started, for a reason other than a declined prompt.</summary>
    Failed,
}

/// <summary>The result of <see cref="IElevation.StartAsync"/>.</summary>
public sealed record ElevationStart(ElevationStartKind Kind, IElevatedProcess? Process = null, string? Reason = null)
{
    public static ElevationStart Declined { get; } = new(ElevationStartKind.Declined);

    public static ElevationStart Running(IElevatedProcess process) => new(ElevationStartKind.Running, process);

    public static ElevationStart Failed(string reason) => new(ElevationStartKind.Failed, Reason: reason);
}

/// <summary>A running elevated helper: tracked by its process handle, never on the UI thread.</summary>
public interface IElevatedProcess : IDisposable
{
    /// <summary>Completes with the exit code when the helper has exited. The panel reads its result only then.</summary>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Starts the elevated helper (D05 T02 §6). Called only from <see cref="ChangeCoordinator"/>, for an action whose
/// eligibility is <see cref="Eligibility.NeedsAdministrator"/>, and exactly once per change: the panel itself
/// never runs elevated. The Windows form is <c>ShellExecute</c> with the <c>runas</c> verb; a test form starts the
/// real executable without a prompt.
/// </summary>
public interface IElevation
{
    /// <summary>Starts the helper and completes when the administrator prompt has been answered (so the wait for the prompt is off the caller's thread).</summary>
    Task<ElevationStart> StartAsync(HelperCommand command, CancellationToken cancellationToken);
}
