using System.IO;
using System.Windows.Threading;
using Bosun.Core;
using Bosun.Panel.Shell;
using Xunit;

namespace Bosun.Panel.Tests.Settings;

/// <summary>
/// The pressure alert threshold on the Settings page (D03 T01 §4, design/SPEC.md §15): it saves and reads back, what
/// can't be a threshold is refused beside the field with nothing saved (from the page and from the question asked on
/// leaving it), Discard puts back the stored value, and a save that fails keeps the edit, the stored floor unchanged.
/// </summary>
public class ThresholdTests
{
    private static void OnUi(Action test) => Sta.Run(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        test();
        return 0;
    });

    [Fact]
    public void The_threshold_saves_and_reads_back_and_Discard_puts_back_the_stored_one()
    {
        OnUi(() =>
        {
            var f = new SettingsFixture().Open();
            Assert.Equal(("2048", (string?)null, false), (f.Page.PressureFloorText, f.Page.PressureFloorError, f.Page.HasUnsavedEdits));

            f.Page.PressureFloorText = "4096";
            Assert.True(f.Page.HasUnsavedEdits);
            Assert.True(f.Page.SaveAsync().Result.Succeeded);
            Assert.Equal(4096, f.OnDisk().PressureFloorMb); // read back as a new panel reads it
            Assert.Equal("Saved and read back: pressure alert threshold 4,096 MB.", f.Page.Status);
            Assert.False(f.Page.HasUnsavedEdits);

            f.Page.PressureFloorText = "3000";
            f.Page.Discard();
            Assert.Equal(("4096", false), (f.Page.PressureFloorText, f.Page.HasUnsavedEdits));
        });
    }

    [Fact]
    public void A_threshold_typed_with_leading_zeros_saves_as_its_value_and_is_no_longer_an_edit()
    {
        // Round 1: "04096" saved as 4096 stayed an edit by its text, and the page asked about it on leaving.
        OnUi(() =>
        {
            var f = new SettingsFixture().Open();
            f.Page.PressureFloorText = "02048";
            Assert.False(f.Page.HasUnsavedEdits); // the stored value, typed another way: nothing to save
            f.Page.PressureFloorText = "04096";
            Assert.True(f.Page.SaveAsync().Result.Succeeded);
            Assert.Equal((4096L, "4096", false), (f.OnDisk().PressureFloorMb, f.Page.PressureFloorText, f.Page.HasUnsavedEdits));
        });
    }

    [Theory]
    [InlineData("", "Enter the threshold in MB")]
    [InlineData("   ", "Enter the threshold in MB")]
    [InlineData("lots", "Enter a whole number of MB")]
    [InlineData("2,048", "Enter a whole number of MB")]
    [InlineData("-1", "Enter a whole number of MB")]
    [InlineData("100", "less than the least a threshold may be, 512 MB")]
    [InlineData("70000", "more than the most a threshold may be, 65,536 MB")]
    [InlineData("99999999999999999999999", "more than the most a threshold may be, 65,536 MB")]
    public void What_can_t_be_a_threshold_is_refused_beside_the_field_and_nothing_is_saved(string typed, string said)
    {
        OnUi(() =>
        {
            var f = new SettingsFixture().Open();
            f.Page.SelectedTheme = f.Option(ThemeChoice.Light); // another edit beside it: nothing of the page is saved
            f.Page.PressureFloorText = typed;
            Assert.Contains(said, f.Page.PressureFloorError);
            var outcome = f.Page.SaveAsync().Result;
            Assert.False(outcome.Succeeded);
            Assert.StartsWith("Not saved: ", f.Page.Status);
            Assert.Equal((2048L, ThemeChoice.FollowWindows), (f.OnDisk().PressureFloorMb, f.OnDisk().Theme));
            Assert.True(f.Page.HasUnsavedEdits);

            f.Page.PressureFloorText = "1024"; // corrected: the refusal goes
            Assert.Null(f.Page.PressureFloorError);
        });
    }

    [Fact]
    public void Leaving_with_a_threshold_that_can_t_be_one_is_refused_and_nothing_is_saved()
    {
        OnUi(() =>
        {
            var f = new SettingsFixture();
            var shell = f.Shell();
            shell.Show(shell.Sections.Single(section => section.Key == "settings"));
            f.Page.PressureFloorText = "nine";
            var leaving = shell.GoToAsync(shell.Sections[0]);
            Assert.Equal(new LeaveQuestion("Settings"), shell.Asking);
            shell.Answer(LeaveChoice.Save);
            Assert.True(StartupSupport.Pump(() => leaving.IsCompleted));
            Assert.False(leaving.Result); // the save was refused, so the page is not left
            Assert.Same(f.Page, shell.Page);
            Assert.Contains("Enter a whole number of MB", f.Page.SaveFailure);
            Assert.Equal(2048, f.OnDisk().PressureFloorMb);
        });
    }

    [Fact]
    public void A_save_that_fails_keeps_the_edit_says_why_and_the_stored_floor_stays_the_one_in_force()
    {
        OnUi(() =>
        {
            var f = new SettingsFixture().Open();
            f.Page.PressureFloorText = "8192";
            f.Files.FailWrite = new IOException("the disk is full");
            Assert.False(f.Page.SaveAsync().Result.Succeeded);
            Assert.Contains("the disk is full", f.Page.Status);
            Assert.Equal(("8192", true), (f.Page.PressureFloorText, f.Page.HasUnsavedEdits));
            Assert.Equal(2048, f.Store.Current.PressureFloorMb); // what the pressure check reads: the floor last saved
        });
    }
}
