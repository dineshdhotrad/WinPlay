// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Win32;

namespace WinPlay.App.Services;

/// <summary>
/// "Start with Windows" via the per-user Run key (no admin, no scheduled task). Points at
/// the current executable so a moved/updated install keeps working after the user re-toggles.
/// </summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WinPlay";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        catch (Exception) { return false; }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                string exe = Environment.ProcessPath ?? "";
                if (exe.Length > 0) key.SetValue(ValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception) { /* best effort — the menu reflects the real state on next open */ }
    }
}
