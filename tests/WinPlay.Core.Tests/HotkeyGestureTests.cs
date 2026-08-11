// SPDX-License-Identifier: GPL-3.0-or-later
using WinPlay.Core.Input;
using Xunit;

namespace WinPlay.Core.Tests;

/// <summary>Verifies the remappable global-hotkey parser (Task F3).</summary>
public class HotkeyGestureTests
{
    [Fact]
    public void The_Default_Is_Win_Shift_A()
    {
        Assert.Equal(HotkeyGesture.ModWin | HotkeyGesture.ModShift, HotkeyGesture.Default.Modifiers);
        Assert.Equal('A', (char)HotkeyGesture.Default.VirtualKey);
    }

    [Theory]
    [InlineData("Win+Shift+A", HotkeyGesture.ModWin | HotkeyGesture.ModShift, (uint)'A')]
    [InlineData("win + shift + a", HotkeyGesture.ModWin | HotkeyGesture.ModShift, (uint)'A')] // case/space-insensitive
    [InlineData("Ctrl+Alt+P", HotkeyGesture.ModControl | HotkeyGesture.ModAlt, (uint)'P')]
    [InlineData("Control+9", HotkeyGesture.ModControl, (uint)'9')]
    [InlineData("Win+F12", HotkeyGesture.ModWin, 0x7Bu)]  // VK_F12
    [InlineData("Meta+Shift+F1", HotkeyGesture.ModWin | HotkeyGesture.ModShift, 0x70u)]
    public void Valid_Gestures_Parse(string text, uint modifiers, uint key)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal(modifiers, gesture.Modifiers);
        Assert.Equal(key, gesture.VirtualKey);
    }

    [Theory]
    [InlineData("A")]              // bare key would swallow typing system-wide
    [InlineData("Win+Shift")]      // no key
    [InlineData("Win+Foo+A")]      // unknown token
    [InlineData("Win+A+B")]        // two keys
    [InlineData("Win+F25")]        // out of F-key range
    [InlineData("Win+Esc")]        // outside the supported key space
    [InlineData("")]
    [InlineData(null)]
    public void Invalid_Gestures_Are_Rejected(string? text)
        => Assert.False(HotkeyGesture.TryParse(text, out _));

    [Theory]
    [InlineData("Win+Shift+A")]
    [InlineData("Ctrl+Alt+F9")]
    [InlineData("Win+7")]
    public void ToString_RoundTrips_Through_TryParse(string text)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var first));
        Assert.True(HotkeyGesture.TryParse(first.ToString(), out var second));
        Assert.Equal(first, second);
    }
}
