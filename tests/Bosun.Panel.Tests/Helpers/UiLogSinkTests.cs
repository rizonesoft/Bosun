using System.Windows.Threading;
using Bosun.Panel.Shell;
using Xunit;

namespace Bosun.Panel.Tests.Helpers;

/// <summary>
/// The Activity pane's lines (D02 T02 §2): the newest 500, each one line with its word and its time, added on the UI
/// thread whoever says them.
/// </summary>
public class UiLogSinkTests
{
    [Fact]
    public void The_sink_keeps_the_newest_500_lines_in_order()
    {
        Sta.Run(() =>
        {
            var sink = new UiLogSink(Dispatcher.CurrentDispatcher);
            for (var n = 1; n <= 600; n++) sink.Append(LogSeverity.Info, "line " + n);

            Assert.Equal(500, UiLogSink.MaxEntries);
            Assert.Equal(500, sink.Entries.Count);
            Assert.Equal(Enumerable.Range(101, 500).Select(n => "line " + n), sink.Entries.Select(entry => entry.Message));
        });
    }

    [Fact]
    public void A_line_said_on_another_thread_is_added_on_the_ui_thread_with_the_time_it_was_said()
    {
        Sta.Run(() =>
        {
            var said = new DateTime(2026, 11, 15, 9, 5, 7, DateTimeKind.Local);
            var sink = new UiLogSink(Dispatcher.CurrentDispatcher, () => said);
            var addedOn = new List<int>();
            sink.Entries.CollectionChanged += (_, _) => addedOn.Add(Environment.CurrentManagedThreadId);

            var other = Task.Run(() =>
            {
                sink.Append(LogSeverity.Warn, "from a worker");
                return Environment.CurrentManagedThreadId;
            });
            Assert.True(StartupSupport.Pump(() => other.IsCompleted && sink.Entries.Count == 1), "the line never arrived");

            Assert.NotEqual(Environment.CurrentManagedThreadId, other.Result);
            Assert.Equal([Environment.CurrentManagedThreadId], addedOn);
            var entry = Assert.Single(sink.Entries);
            Assert.Equal(new LogEntry(said, LogSeverity.Warn, "from a worker"), entry);
            Assert.Equal("09:05:07", entry.Time);
        });
    }

    [Theory]
    [InlineData(LogSeverity.Info, "INFO")]
    [InlineData(LogSeverity.Warn, "WARN")]
    [InlineData(LogSeverity.Fail, "FAIL")]
    public void Each_severity_has_its_word(LogSeverity severity, string word) =>
        Assert.Equal(word, new LogEntry(DateTime.Now, severity, "x").Tag);

    [Fact]
    public void A_message_is_one_line_cut_at_2000_characters_and_an_empty_one_is_refused()
    {
        Sta.Run(() =>
        {
            var sink = new UiLogSink(Dispatcher.CurrentDispatcher);

            sink.Append(LogSeverity.Fail, "bosun: the queue file is damaged\r\n  at line 3\n\tretry later\n");
            Assert.Equal("bosun: the queue file is damaged at line 3 retry later", sink.Entries[0].Message);

            sink.Append(LogSeverity.Fail, new string('x', 10_000));
            Assert.Equal(UiLogSink.MaxMessageLength, sink.Entries[1].Message.Length);
            Assert.Equal(2000, sink.Entries[1].Message.Length);
            Assert.EndsWith("x…", sink.Entries[1].Message, StringComparison.Ordinal);

            sink.Append(LogSeverity.Info, new string('y', 2000)); // exactly the limit is not cut
            Assert.Equal(new string('y', 2000), sink.Entries[2].Message);

            foreach (var nothing in new[] { "", "   ", "\r\n\t" })
                Assert.Throws<ArgumentException>(() => sink.Append(LogSeverity.Info, nothing));
            Assert.Equal(3, sink.Entries.Count);
        });
    }
}
