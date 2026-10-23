// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 いしかず
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace RtlCast.App;

public static class SettingsStore
{
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RTL-TCP-Cast");
    public static string IniPath => Path.Combine(DefaultDirectory, "settings.ini");

    public static AppSettings Load(string? directory = null)
    {
        directory ??= DefaultDirectory;
        var ini = Path.Combine(directory, "settings.ini");
        if (File.Exists(ini)) return Parse(File.ReadAllLines(ini));
        var legacy = Path.Combine(directory, "settings.json");
        var settings = File.Exists(legacy)
            ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(legacy)) ?? new()
            : new AppSettings();
        settings.Validate();
        Save(settings, directory);
        return settings;
    }

    internal static string Serialize(AppSettings settings)
    {
        settings.Validate();
        static string Text(string value)
        {
            if (value.Contains('\r') || value.Contains('\n')) throw new ArgumentException("設定値に改行は使用できません。");
            return value;
        }
        var content = string.Join(Environment.NewLine, new[]
        {
            "; RTL-TCP-Cast settings (UTF-8)", "[Source]",
            $"UseLocalRtlTcp={settings.UseLocalRtlTcp.ToString().ToLowerInvariant()}",
            $"SessionId={Text(settings.SessionId)}", $"SessionName={Text(settings.SessionName)}",
            $"DeviceIdentifier={Text(settings.DeviceIdentifier)}", $"RtlTcpPath={Text(settings.RtlTcpPath)}", "", "[Network]",
            $"UpstreamHost={Text(settings.UpstreamHost)}", $"UpstreamPort={settings.UpstreamPort}",
            $"ListenAddress={Text(settings.ListenAddress)}", $"ListenPort={settings.ListenPort}",
            $"PreferredLocalAddress={Text(settings.PreferredLocalAddress)}",
            $"AutoReconnect={settings.AutoReconnect.ToString().ToLowerInvariant()}", "", "[SDR]",
            $"AcceptClientCommands={settings.AcceptClientCommands.ToString().ToLowerInvariant()}",
            $"LockCenterFrequency={settings.LockCenterFrequency.ToString().ToLowerInvariant()}",
            $"LockPpm={settings.LockPpm.ToString().ToLowerInvariant()}",
            $"LockGain={settings.LockGain.ToString().ToLowerInvariant()}",
            $"LockRtlAgc={settings.LockRtlAgc.ToString().ToLowerInvariant()}",
            $"LockSampleRate={settings.LockSampleRate.ToString().ToLowerInvariant()}",
            $"CenterFrequency={settings.CenterFrequency}", $"Ppm={settings.Ppm}",
            $"AutomaticGain={settings.AutomaticGain.ToString().ToLowerInvariant()}",
            $"GainTenthsDb={settings.GainTenthsDb}", $"RtlAgc={settings.RtlAgc.ToString().ToLowerInvariant()}",
            $"ManagedSampleRate={settings.ManagedSampleRate}", "", "[Advanced]",
            $"ReconnectDelaySeconds={settings.ReconnectDelay.TotalSeconds.ToString(CultureInfo.InvariantCulture)}",
            $"ConnectTimeoutSeconds={settings.ConnectTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)}",
            $"SendTimeoutSeconds={settings.SendTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)}",
            $"ClientQueueCapacity={settings.ClientQueueCapacity}", $"MaxClients={settings.MaxClients}", ""
        });
        return content;
    }

    public static void Save(AppSettings settings, string? directory = null)
    {
        var content = Serialize(settings);
        directory ??= DefaultDirectory;
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "settings.ini");
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static AppSettings Parse(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = "";
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1].Trim(); continue; }
            var separator = line.IndexOf('=');
            if (separator < 1) throw new ArgumentException($"INIの書式が不正です: {line}");
            values[$"{section}.{line[..separator].Trim()}"] = line[(separator + 1)..].Trim();
        }
        string Value(string key, string fallback) => values.GetValueOrDefault(key, fallback);
        int Number(string key, int fallback) => int.TryParse(Value(key, fallback.ToString(CultureInfo.InvariantCulture)), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result : throw new ArgumentException($"INIの{key}には整数を指定してください。");
        uint Unsigned(string key, uint fallback) => uint.TryParse(Value(key, fallback.ToString(CultureInfo.InvariantCulture)), out var result)
            ? result : throw new ArgumentException($"INIの{key}には正の整数を指定してください。");
        bool Flag(string key, bool fallback) => bool.TryParse(Value(key, fallback.ToString()), out var result)
            ? result : throw new ArgumentException($"INIの{key}にはtrueまたはfalseを指定してください。");
        TimeSpan Duration(string key, TimeSpan fallback)
        {
            if (!double.TryParse(Value(key, fallback.TotalSeconds.ToString(CultureInfo.InvariantCulture)), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
                !double.IsFinite(seconds) || seconds <= 0 || seconds > 86400)
                throw new ArgumentException($"INIの{key}には0より大きく86400以下の秒数を指定してください。");
            return TimeSpan.FromSeconds(seconds);
        }
        var defaults = new AppSettings();
        var legacyClientControl = Flag("SDR.AcceptClientCommands", defaults.AcceptClientCommands);
        var hasLockSettings = new[] { "SDR.LockCenterFrequency", "SDR.LockPpm", "SDR.LockGain", "SDR.LockRtlAgc", "SDR.LockSampleRate" }
            .Any(values.ContainsKey);
        var legacyLockDefault = !hasLockSettings && !legacyClientControl;
        var settings = defaults with
        {
            AcceptClientCommands = legacyClientControl,
            LockCenterFrequency = Flag("SDR.LockCenterFrequency", legacyLockDefault),
            LockPpm = Flag("SDR.LockPpm", legacyLockDefault),
            LockGain = Flag("SDR.LockGain", legacyLockDefault),
            LockRtlAgc = Flag("SDR.LockRtlAgc", legacyLockDefault),
            LockSampleRate = Flag("SDR.LockSampleRate", legacyLockDefault),
            CenterFrequency = Unsigned("SDR.CenterFrequency", defaults.CenterFrequency),
            Ppm = Number("SDR.Ppm", defaults.Ppm),
            AutomaticGain = Flag("SDR.AutomaticGain", defaults.AutomaticGain),
            GainTenthsDb = Number("SDR.GainTenthsDb", defaults.GainTenthsDb),
            RtlAgc = Flag("SDR.RtlAgc", defaults.RtlAgc),
            ManagedSampleRate = Unsigned("SDR.ManagedSampleRate", defaults.ManagedSampleRate),
            SessionId = Value("Source.SessionId", ""),
            SessionName = Value("Source.SessionName", defaults.SessionName),
            DeviceIdentifier = Value("Source.DeviceIdentifier", "0"),
            UseLocalRtlTcp = Flag("Source.UseLocalRtlTcp", defaults.UseLocalRtlTcp),
            RtlTcpPath = Value("Source.RtlTcpPath", defaults.RtlTcpPath),
            UpstreamHost = Value("Network.UpstreamHost", defaults.UpstreamHost),
            UpstreamPort = Number("Network.UpstreamPort", defaults.UpstreamPort),
            ListenAddress = Value("Network.ListenAddress", defaults.ListenAddress),
            ListenPort = Number("Network.ListenPort", defaults.ListenPort),
            PreferredLocalAddress = Value("Network.PreferredLocalAddress", defaults.PreferredLocalAddress),
            AutoReconnect = Flag("Network.AutoReconnect", defaults.AutoReconnect),
            ReconnectDelay = Duration("Advanced.ReconnectDelaySeconds", defaults.ReconnectDelay),
            ConnectTimeout = Duration("Advanced.ConnectTimeoutSeconds", defaults.ConnectTimeout),
            SendTimeout = Duration("Advanced.SendTimeoutSeconds", defaults.SendTimeout),
            ClientQueueCapacity = Number("Advanced.ClientQueueCapacity", defaults.ClientQueueCapacity),
            MaxClients = Number("Advanced.MaxClients", defaults.MaxClients)
        };
        settings.Validate();
        return settings;
    }
}
