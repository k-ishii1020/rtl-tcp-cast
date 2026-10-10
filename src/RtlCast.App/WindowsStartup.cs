// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 いしかず
using Microsoft.Win32;

namespace RtlCast.App;

public static class WindowsStartup
{
    public const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "RTL-TCP-CAST";
    public static void SetEnabled(bool enabled, string? executablePath = null, string registryPath = RegistryPath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(registryPath, writable: true);
        if (enabled)
        {
            var path = executablePath ?? Environment.ProcessPath ?? throw new InvalidOperationException("実行ファイルの場所を取得できません。");
            key.SetValue(ValueName, $"\"{path}\"", RegistryValueKind.String);
        }
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
