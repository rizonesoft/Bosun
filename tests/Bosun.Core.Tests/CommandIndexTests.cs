using Bosun.Core;
using Xunit;

namespace Bosun.Core.Tests;

/// <summary>The command palette's search (D02 T03 §8): a prefix over a word's start over anywhere inside, ties in order.</summary>
public class CommandIndexTests
{
    static readonly string[] Names = ["Settings", "Save settings", "Discard settings changes", "Memory", "Pause notifications for 1 hour", "Resume notifications", "Queue", "Unsaved"];

    [Fact]
    public void A_prefix_ranks_over_a_word_start_over_anywhere_inside()
    {
        Assert.Equal(["Settings", "Save settings", "Discard settings changes"], CommandIndex.Rank(Names, n => n, "set"));
        Assert.Equal(["Save settings", "Unsaved"], CommandIndex.Rank(Names, n => n, "sav"));                      // Unsaved: inside only
        Assert.Equal(["Pause notifications for 1 hour", "Resume notifications"], CommandIndex.Rank(Names, n => n, "notif")); // ties in order
    }

    [Theory]
    [InlineData("Save settings", "SAVE", CommandMatch.Prefix)]
    [InlineData("Save settings", "  sett ", CommandMatch.WordStart)]
    [InlineData("Unsaved", "save", CommandMatch.Substring)]
    [InlineData("Settings", "queue", CommandMatch.None)]
    [InlineData("Re-read settings", "read", CommandMatch.WordStart)]
    [InlineData("Breadth read", "read", CommandMatch.WordStart)]                                                   // a later start counts
    [InlineData("Anything", "", CommandMatch.Prefix)]
    public void Each_match_is_told_apart(string name, string query, CommandMatch expected) => Assert.Equal(expected, CommandIndex.MatchOf(name, query));

    [Fact]
    public void Nothing_typed_lists_everything_in_order_and_nothing_matching_lists_nothing()
    {
        Assert.Equal(Names, CommandIndex.Rank(Names, n => n, "  "));
        Assert.Empty(CommandIndex.Rank(Names, n => n, "zzz"));
    }
}
