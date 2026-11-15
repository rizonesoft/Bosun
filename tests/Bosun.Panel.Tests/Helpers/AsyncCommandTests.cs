using System.Windows.Threading;
using Bosun.Panel.Mvvm;
using Xunit;

namespace Bosun.Panel.Tests.Helpers;

/// <summary>
/// The command behind every action (D02 T02 §2): it can't re-enter, and what the action throws reaches its failure
/// handler, never the UI thread's crash handler.
/// </summary>
public class AsyncCommandTests
{
    [Fact]
    public async Task A_second_execute_while_one_runs_starts_nothing_and_the_command_runs_again_after()
    {
        var starts = 0;
        var gate = new TaskCompletionSource();
        var command = new AsyncCommand(async () =>
        {
            starts++;
            await gate.Task;
        }, _ => Assert.Fail("nothing failed"));
        var changes = 0;
        command.CanExecuteChanged += (_, _) => changes++;

        Assert.True(command.CanExecute());
        var first = command.ExecuteAsync();
        Assert.True(command.IsRunning);
        Assert.False(command.CanExecute());
        Assert.Equal(1, changes); // at the start

        command.Execute(); // a double click
        await command.ExecuteAsync();
        Assert.Equal(1, starts);

        gate.SetResult();
        await first;
        Assert.False(command.IsRunning);
        Assert.True(command.CanExecute());
        Assert.Equal(2, changes); // and at the end

        gate = new TaskCompletionSource();
        gate.SetResult();
        await command.ExecuteAsync();
        Assert.Equal(2, starts);
    }

    [Fact]
    public async Task A_command_that_may_not_run_starts_nothing()
    {
        var starts = 0;
        var allowed = false;
        var command = new AsyncCommand(() =>
        {
            starts++;
            return Task.CompletedTask;
        }, _ => { }, () => allowed);

        Assert.False(command.CanExecute());
        command.Execute();
        await command.ExecuteAsync();
        Assert.Equal(0, starts);

        allowed = true;
        var asked = 0;
        command.CanExecuteChanged += (_, _) => asked++;
        command.Refresh();
        Assert.Equal(1, asked);
        await command.ExecuteAsync();
        Assert.Equal(1, starts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Execute_gives_what_the_action_throws_to_the_failure_handler_and_never_to_the_ui_thread(bool afterAnAwait)
    {
        var failures = new List<Exception>();
        var unhandled = new List<Exception>();
        var runs = 0;
        Sta.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.UnhandledException += (_, e) =>
            {
                unhandled.Add(e.Exception);
                e.Handled = true;
            };
            var command = new AsyncCommand(async () =>
            {
                runs++;
                if (afterAnAwait) await Task.Yield();
                throw new InvalidOperationException("the tool said no");
            }, failures.Add);

            command.Execute(); // as a button calls it: nothing to await, nowhere for an exception to go but the handler
            Assert.True(StartupSupport.Pump(() => failures.Count > 0 && !command.IsRunning), "the failure never reached the handler");

            Assert.Equal("the tool said no", Assert.Single(failures).Message);
            Assert.True(command.CanExecute(), "a failed action can be tried again");
            command.Execute();
            Assert.True(StartupSupport.Pump(() => failures.Count > 1 && !command.IsRunning));
            // Everything queued has run: an exception that had escaped would have surfaced by now.
            dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        });
        Assert.Equal(2, runs);
        Assert.Empty(unhandled);
    }

    [Fact]
    public async Task ExecuteAsync_gives_what_the_action_throws_to_its_caller_and_not_to_the_handler()
    {
        var failures = new List<Exception>();
        var command = new AsyncCommand(async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("the tool said no");
        }, failures.Add);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(command.ExecuteAsync);

        Assert.Equal("the tool said no", thrown.Message);
        Assert.Empty(failures);
        Assert.False(command.IsRunning);
        Assert.True(command.CanExecute());
    }
}
