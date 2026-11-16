using Xunit;

namespace Bosun.Panel.E2E;

/// <summary>
/// The smoke's screen (D02 T03 §12), chosen from fake screen lists: the one <c>BOSUN_TEST_SCREEN</c> names, refused when
/// it names none here; else the largest that is not the main one, the leftmost of equals; else the main one.
/// </summary>
public class TestScreenTests
{
    static Native.RECT R(int left, int top, int width, int height) => new() { Left = left, Top = top, Right = left + width, Bottom = top + height };

    static Screen S(string device, int left, int width, int height, bool primary = false) =>
        new(device, R(left, 0, width, height), R(left, 0, width, height - 48), 144, primary);

    static readonly Screen Main = S(@"\\.\DISPLAY2", 0, 3840, 2160, primary: true);
    static readonly Screen Big = S(@"\\.\DISPLAY1", -3840, 3840, 2160);
    static readonly Screen Small = S(@"\\.\DISPLAY3", -5760, 1920, 1080);

    [Fact]
    public void A_named_screen_is_the_one_whatever_its_size_and_case()
    {
        Assert.Same(Small, TestScreen.Choose([Main, Big, Small], @"\\.\display3"));
        Assert.Same(Main, TestScreen.Choose([Main, Big, Small], @" \\.\DISPLAY2 "));
    }

    [Fact]
    public void A_name_that_is_no_screen_here_refuses_the_run_saying_the_screens_there_are()
    {
        var refused = Assert.Throws<InvalidOperationException>(() => TestScreen.Choose([Main, Big], @"\\.\DISPLAY9"));
        Assert.Contains(@"BOSUN_TEST_SCREEN names \\.\DISPLAY9, which is not a screen here", refused.Message);
        Assert.Contains(@"\\.\DISPLAY1", refused.Message);
    }

    [Fact]
    public void Unnamed_it_is_the_largest_screen_that_is_not_the_main_one()
    {
        Assert.Same(Big, TestScreen.Choose([Main, Small, Big], null));
        var twin = S(@"\\.\DISPLAY4", -7680, 3840, 2160); // as large, further left: the leftmost of equals
        Assert.Same(twin, TestScreen.Choose([Main, Big, twin], ""));
    }

    [Fact]
    public void With_one_screen_it_is_the_main_one()
    {
        Assert.Same(Main, TestScreen.Choose([Main], null));
        Assert.Throws<InvalidOperationException>(() => TestScreen.Choose([], null));
    }

    [Fact]
    public void The_place_it_seeds_is_centred_in_the_screen_s_work_area_at_its_DPI()
    {
        // 1360 x 860 at 144 DPI is 2040 x 1290, centred in a work area of 3840 x 2112 at (-3840, 0).
        Assert.Equal("{\"schema_version\":1,\"window\":{\"left\":-2940,\"top\":411,\"width\":2040,\"height\":1290,\"dpi\":144,\"maximised\":false}}\n",
            TestScreen.Settings(Big));
    }
}
