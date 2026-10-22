using GamerPlug.Recorder.Core;
using Xunit;

namespace GamerPlug.Recorder.Core.Tests;

public class HotkeyActionTests
{
    [Theory]
    [InlineData("clip;30", HotkeyActionKind.Clip, 30)]
    [InlineData("clip;7.5", HotkeyActionKind.Clip, 7.5)]
    public void ParsesClipWithSeconds(string raw, HotkeyActionKind kind, double seconds)
    {
        Assert.True(HotkeyAction.TryParse(raw, out var action));
        Assert.Equal(kind, action.Kind);
        Assert.Equal(seconds, action.Seconds);
    }

    [Theory]
    [InlineData("bookmark", HotkeyActionKind.Bookmark)]
    [InlineData("screenshot", HotkeyActionKind.Screenshot)]
    [InlineData("segment_toggle", HotkeyActionKind.SegmentToggle)]
    public void ParsesSimpleActions(string raw, HotkeyActionKind kind)
    {
        Assert.True(HotkeyAction.TryParse(raw, out var action));
        Assert.Equal(kind, action.Kind);
    }

    [Theory]
    [InlineData("clip")]
    [InlineData("clip;")]
    [InlineData("clip;0")]
    [InlineData("clip;-5")]
    [InlineData("clip;abc")]
    [InlineData("explode")]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsInvalid(string raw)
    {
        Assert.False(HotkeyAction.TryParse(raw, out _));
    }

    [Theory]
    [InlineData("clip;30")]
    [InlineData("bookmark")]
    [InlineData("screenshot")]
    [InlineData("segment_toggle")]
    public void RoundTripsThroughToString(string raw)
    {
        Assert.True(HotkeyAction.TryParse(raw, out var action));
        Assert.Equal(raw, action.ToString());
    }
}
