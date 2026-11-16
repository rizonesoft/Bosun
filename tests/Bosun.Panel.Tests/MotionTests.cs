using System.IO;
using System.Windows;
using System.Windows.Markup;
using Bosun.Panel.Theme;
using Xunit;

namespace Bosun.Panel.Tests;

/// <summary>
/// The one motion gate (D02 T03 §1, design/SPEC.md §19): the design's durations while Windows' animation effects are
/// on, zero and no pulse while they are off, following each change of the setting without a restart.
/// </summary>
public class MotionTests
{
    sealed class FakeAnimation(bool on) : IWindowsAnimation
    {
        public bool On { get; private set; } = on;

        public event Action? Changed;

        public void Set(bool on)
        {
            On = on;
            Changed?.Invoke();
        }
    }

    static ResourceDictionary Generated(string file)
    {
        using var stream = File.OpenRead(TestPaths.GeneratedTheme(file));
        return (ResourceDictionary)XamlReader.Load(stream);
    }

    static (Duration Fast, Duration Normal, Duration Slow, Duration Pulse, bool Animates) Read(Motion motion) =>
        (motion.Fast, motion.Normal, motion.Slow, motion.Pulse, motion.Animates);

    static readonly (Duration, Duration, Duration, Duration, bool) Designed =
        (new(TimeSpan.FromMilliseconds(120)), new(TimeSpan.FromMilliseconds(220)), new(TimeSpan.FromMilliseconds(400)), new(TimeSpan.FromMilliseconds(2400)), true);

    static readonly (Duration, Duration, Duration, Duration, bool) Instant =
        (new(TimeSpan.Zero), new(TimeSpan.Zero), new(TimeSpan.Zero), new(TimeSpan.Zero), false);

    [Theory]
    [InlineData("Dark.xaml")]
    [InlineData("Light.xaml")]
    public void With_animation_on_the_durations_are_the_generated_theme_s_and_with_it_off_they_are_zero(string file)
    {
        Sta.Run(() =>
        {
            var theme = Generated(file);
            using (var on = new Motion(theme, new FakeAnimation(true)))
            {
                Assert.Equal(Designed, Read(on));
                Assert.Same(theme[Motion.EaseKey], on.Ease);
            }
            using var off = new Motion(theme, new FakeAnimation(false));
            Assert.Equal(Instant, Read(off));
            return 0;
        });
    }

    [Fact]
    public void Turning_the_setting_on_off_and_on_again_is_followed_and_said_each_time_without_a_restart()
    {
        Sta.Run(() =>
        {
            var windows = new FakeAnimation(true);
            using var motion = new Motion(Generated("Dark.xaml"), windows);
            var said = new List<(Duration, Duration, Duration, Duration, bool)>();
            motion.Changed += () => said.Add(Read(motion)); // read as each subscriber would, inside the notice

            windows.Set(false);
            windows.Set(true);
            windows.Set(false);
            Assert.Equal([Instant, Designed, Instant], said);
            Assert.Equal(Instant, Read(motion));

            motion.Dispose(); // a disposed gate hears nothing more
            windows.Set(true);
            Assert.Equal(3, said.Count);
            return 0;
        });
    }

    [Fact]
    public void Windows_setting_is_read_again_only_when_WPF_says_it_changed()
    {
        var value = true;
        var reads = 0;
        using var windows = new WindowsAnimation(() => { reads++; return value; });
        var changes = 0;
        windows.Changed += () => changes++;
        Assert.True(windows.On);

        value = false;
        windows.PropertyChanged(nameof(SystemParameters.HighContrast)); // another parameter: not read again
        Assert.Equal((true, 0, 1), (windows.On, changes, reads));
        windows.PropertyChanged(nameof(SystemParameters.ClientAreaAnimation));
        Assert.Equal((false, 1), (windows.On, changes));
        windows.PropertyChanged(nameof(SystemParameters.ClientAreaAnimation)); // the same value: nothing said
        Assert.Equal(1, changes);
        value = true;
        windows.PropertyChanged(nameof(SystemParameters.ClientAreaAnimation));
        Assert.Equal((true, 2), (windows.On, changes));
    }

    [Fact]
    public void A_theme_without_a_motion_resource_is_refused_when_the_gate_is_made()
    {
        Sta.Run(() =>
        {
            var theme = Generated("Light.xaml");
            theme.Remove(Motion.PulseKey);
            var refused = Assert.Throws<InvalidOperationException>(() => new Motion(theme, new FakeAnimation(true)));
            Assert.Contains(Motion.PulseKey, refused.Message);
            return 0;
        });
    }
}
