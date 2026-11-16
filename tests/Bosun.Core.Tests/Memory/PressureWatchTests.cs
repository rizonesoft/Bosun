using Bosun.Core.Memory;
using Bosun.Core.Tests.Settings;
using Xunit;

namespace Bosun.Core.Tests.Memory;

/// <summary>
/// Memory pressure episodes (D03 T01 §4): one start and one end per episode, however noisy the dip around the floor,
/// with the floor and the 1 GB way back exact, and a check that read nothing deciding nothing.
/// </summary>
public class PressureWatchTests
{
    private const long Floor = 2048;

    private static List<PressureChange> Run(PressureWatch watch, params long?[] samples) =>
        [.. samples.Select(sample => watch.Observe(sample, Floor)).Where(change => change != PressureChange.None)];

    [Fact]
    public void A_noisy_dip_around_the_floor_is_one_episode_and_two_dips_are_two()
    {
        var watch = new PressureWatch();
        // Down through the floor, bouncing over it and back under, then well clear: one start, one end.
        Assert.Equal([PressureChange.Started, PressureChange.Ended], Run(watch, 4000, 2100, 1900, 2300, 1700, 2600, 2900, 3100, 3200));
        Assert.False(watch.InEpisode);
        // A second episode is a second start and a second end.
        Assert.Equal([PressureChange.Started, PressureChange.Ended], Run(watch, 1500, 1600, 4000));
    }

    [Fact]
    public void The_floor_and_the_way_back_are_exact()
    {
        var watch = new PressureWatch();
        Assert.Empty(Run(watch, Floor)); // at the floor is not under it
        Assert.Equal([PressureChange.Started], Run(watch, Floor - 1));
        Assert.Empty(Run(watch, Floor + PressureWatch.Hysteresis)); // exactly 1 GB over: still the episode
        Assert.Equal([PressureChange.Ended], Run(watch, Floor + PressureWatch.Hysteresis + 1));
    }

    [Fact]
    public void A_check_that_read_nothing_keeps_the_episode_as_it_was()
    {
        var watch = new PressureWatch();
        Assert.Empty(Run(watch, null, null));
        Assert.False(watch.InEpisode);
        Assert.Equal([PressureChange.Started], Run(watch, 1000, null, null));
        Assert.True(watch.InEpisode); // unread checks neither end it nor start it again
        Assert.Equal([PressureChange.Ended], Run(watch, null, 9000));
    }

    [Fact]
    public void The_threshold_is_saved_read_back_defaulted_and_kept_within_its_limits()
    {
        using var scratch = new Scratch();
        var store = new PanelSettingsStore(scratch.Settings);
        Assert.Equal(2048, store.Current.PressureFloorMb); // no file: the default

        Assert.True(store.Update(current => current with { PressureFloorMb = 4096 }).Succeeded);
        Assert.Equal(4096, new PanelSettingsStore(scratch.Settings).Current.PressureFloorMb); // read back by a new store
        Assert.Contains("\"pressure_floor_mb\": 4096", File.ReadAllText(scratch.Settings));

        File.WriteAllText(scratch.Settings, "{\"schema_version\": 1, \"theme\": \"Dark\"}"); // a file from before the setting
        Assert.Equal(2048, new PanelSettingsStore(scratch.Settings).Current.PressureFloorMb);
        File.WriteAllText(scratch.Settings, "{\"schema_version\": 1, \"pressure_floor_mb\": 100}");
        Assert.Equal(512, new PanelSettingsStore(scratch.Settings).Current.PressureFloorMb); // brought within its limits
        File.WriteAllText(scratch.Settings, "{\"schema_version\": 1, \"pressure_floor_mb\": 999999}");
        Assert.Equal(65536, new PanelSettingsStore(scratch.Settings).Current.PressureFloorMb);
        File.WriteAllText(scratch.Settings, "{\"schema_version\": 1, \"pressure_floor_mb\": \"lots\"}");
        var damaged = new PanelSettingsStore(scratch.Settings);
        Assert.Equal(SettingsState.Restored, damaged.State); // not a number: the file is kept aside, as any damaged one
        Assert.Contains("pressure_floor_mb is not a whole number", damaged.Notice);
    }
}
