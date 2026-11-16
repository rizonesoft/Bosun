using System.Text;
using Bosun.Core;
using Xunit;

namespace Bosun.Core.Tests.Settings;

/// <summary>
/// The window's place in `panel.json` (D02 T03 §12): written and read back whole, missing reads as none so a file from
/// before it still reads, and every wrong shape is the store's fault, as every field's is.
/// </summary>
public class WindowPlaceStoreTests
{
    static PanelSettings Read(string json)
    {
        Assert.True(PanelSettingsStore.TryParse(Encoding.UTF8.GetBytes(json), out var settings, out var fault), fault);
        return settings;
    }

    static string Refused(string json)
    {
        Assert.False(PanelSettingsStore.TryParse(Encoding.UTF8.GetBytes(json), out _, out var fault));
        return fault;
    }

    [Fact]
    public void A_place_is_written_and_read_back_whole_on_a_screen_at_a_negative_origin()
    {
        var place = new WindowPlace(-3600, 120, 2040, 1290, 144, Maximised: true);
        var settings = new PanelSettings { Theme = ThemeChoice.Light, Window = place };
        Assert.True(PanelSettingsStore.TryParse(PanelSettingsStore.Serialize(settings), out var back, out var fault), fault);
        Assert.Equal(place, back.Window);
        Assert.Equal(ThemeChoice.Light, back.Theme);
    }

    [Fact]
    public void A_file_without_one_reads_as_none()
    {
        Assert.Null(Read("""{"schema_version":1}""").Window);
        Assert.Null(Read("""{"schema_version":1,"window":null}""").Window);
        Assert.DoesNotContain("window", Encoding.UTF8.GetString(PanelSettingsStore.Serialize(PanelSettings.Defaults)));
    }

    [Theory]
    [InlineData("""{"schema_version":1,"window":[1]}""", "its window is not an object")]
    [InlineData("""{"schema_version":1,"window":{"left":0,"top":0,"width":800,"height":600,"dpi":96}}""", "its window's maximised is missing")]
    [InlineData("""{"schema_version":1,"window":{"left":"0","top":0,"width":800,"height":600,"dpi":96,"maximised":false}}""", "its window's left is missing")]
    [InlineData("""{"schema_version":1,"window":{"left":0.5,"top":0,"width":800,"height":600,"dpi":96,"maximised":false}}""", "its window's left is missing")]
    [InlineData("""{"schema_version":1,"window":{"left":0,"top":0,"width":0,"height":600,"dpi":96,"maximised":false}}""", "its window size is not more than zero")]
    [InlineData("""{"schema_version":1,"window":{"left":0,"top":0,"width":800,"height":-1,"dpi":96,"maximised":false}}""", "its window size is not more than zero")]
    [InlineData("""{"schema_version":1,"window":{"left":0,"top":0,"width":800,"height":600,"dpi":20,"maximised":false}}""", "its window DPI is not from 48 to 960")]
    [InlineData("""{"schema_version":1,"window":{"left":0,"top":0,"width":800,"height":600,"dpi":96,"maximised":1}}""", "its window's maximised is missing")]
    public void Every_wrong_shape_is_refused_with_why(string json, string said) =>
        Assert.StartsWith(said, Refused(json));
}
