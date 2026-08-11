// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinPlay.Core.Input;

/// <summary>
/// A parsed global-hotkey gesture (Task F3): Win32 <c>RegisterHotKey</c> modifier flags plus a
/// virtual-key code. Parsing lives here — pure and unit-tested — so the user-remappable hotkey
/// string ("Win+Shift+A") is validated by logic, not by whatever the shell happens to accept.
/// </summary>
public readonly record struct HotkeyGesture(uint Modifiers, uint VirtualKey)
{
    // Win32 RegisterHotKey modifier flags.
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    /// <summary>Suppress auto-repeat while held (MOD_NOREPEAT).</summary>
    public const uint ModNoRepeat = 0x4000;

    /// <summary>WinPlay's default: Win+Shift+A opens the picker, echoing the native flyouts'
    /// Win+Ctrl+V / Win+K muscle memory (§5 fallback UX).</summary>
    public static HotkeyGesture Default { get; } = new(ModWin | ModShift, 'A');

    /// <summary>
    /// Tried in order when <see cref="Default"/> is already owned by another application — a
    /// real collision, observed in the wild. A hotkey that silently does nothing is worse than
    /// one that quietly moved, so WinPlay takes the first free combination and logs which.
    /// Chosen to stay clear of Windows' own reservations (Win+A/K/V etc. are system-owned).
    /// </summary>
    public static IReadOnlyList<HotkeyGesture> DefaultAlternates { get; } =
    [
        new(ModWin | ModShift, 'P'),        // "play"
        new(ModWin | ModAlt, 'A'),
        new(ModControl | ModAlt, 'A'),
        new(ModWin | ModShift, 0x77),       // VK_F8 — media-adjacent, rarely claimed
    ];

    /// <summary>
    /// Parses "Win+Shift+A", "Ctrl+Alt+F9", etc. Case- and whitespace-insensitive. Returns
    /// false when the gesture has no modifier, no key, an unknown token, or several keys —
    /// a malformed user setting falls back to <see cref="Default"/> rather than misfiring.
    /// </summary>
    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        uint modifiers = 0;
        uint key = 0;
        foreach (string raw in text.Split('+'))
        {
            string token = raw.Trim();
            switch (token.ToUpperInvariant())
            {
                case "WIN" or "WINDOWS" or "META": modifiers |= ModWin; continue;
                case "SHIFT": modifiers |= ModShift; continue;
                case "CTRL" or "CONTROL": modifiers |= ModControl; continue;
                case "ALT": modifiers |= ModAlt; continue;
            }

            if (key != 0) return false; // a second non-modifier token — ambiguous
            key = KeyFromToken(token);
            if (key == 0) return false;
        }

        // A bare key with no modifier would swallow ordinary typing system-wide; refuse it.
        if (modifiers == 0 || key == 0) return false;
        gesture = new HotkeyGesture(modifiers, key);
        return true;
    }

    /// <summary>A–Z, 0–9, and F1–F24 — the sensible global-hotkey key space.</summary>
    private static uint KeyFromToken(string token)
    {
        // An empty token is what a malformed gesture produces — "Win++A", or a trailing "+".
        // Indexing it threw IndexOutOfRangeException out of a method documented to return false
        // for anything it cannot parse. The only caller happens to wrap this in a broad catch, so
        // it degraded to the default hotkey instead of crashing; that is luck, not a contract.
        if (token.Length == 0) return 0;
        if (token.Length == 1)
        {
            char c = char.ToUpperInvariant(token[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c;
            return 0;
        }
        if ((token[0] is 'F' or 'f')
            && int.TryParse(token[1..], out int fn)
            && fn is >= 1 and <= 24)
            return (uint)(0x70 + fn - 1); // VK_F1 = 0x70
        return 0;
    }

    public override string ToString()
    {
        var parts = new List<string>(4);
        if ((Modifiers & ModWin) != 0) parts.Add("Win");
        if ((Modifiers & ModControl) != 0) parts.Add("Ctrl");
        if ((Modifiers & ModAlt) != 0) parts.Add("Alt");
        if ((Modifiers & ModShift) != 0) parts.Add("Shift");
        parts.Add(VirtualKey is >= 0x70 and <= 0x87
            ? $"F{VirtualKey - 0x70 + 1}"
            : ((char)VirtualKey).ToString());
        return string.Join("+", parts);
    }
}
