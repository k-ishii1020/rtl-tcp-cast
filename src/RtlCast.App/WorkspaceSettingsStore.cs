// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 いしかず
using System.IO;
using System.Text;

namespace RtlCast.App;

public sealed record WorkspaceSettings
{
    public string RtlTcpPath { get; init; } = "";
    public bool StartWithWindows { get; init; }
    public bool AutoStartStreaming { get; init; }
    public bool HideTrayNotice { get; init; }
    public string SelectedSessionId { get; init; } = "";
    public List<AppSettings> Sessions { get; init; } = new();
}

public static class WorkspaceSettingsStore
{
    public static WorkspaceSettings Load(string? directory = null)
    {
        directory ??= SettingsStore.DefaultDirectory;
        var path = Path.Combine(directory, "settings.ini");
        var legacyIniExists = File.Exists(path);
        if (!File.Exists(path) || !File.ReadAllLines(path).Any(l => l.Trim() == "[Application]"))
        {
            var legacy = SettingsStore.Load(directory);
            if (legacyIniExists) File.Copy(path, Path.Combine(directory, "settings.legacy.ini"), true);
            var session = legacy with { SessionId = Guid.NewGuid().ToString("N"), RtlTcpPath = "" };
            var migrated = new WorkspaceSettings { RtlTcpPath = legacy.RtlTcpPath,
                SelectedSessionId = session.SessionId, Sessions = new() { session } };
            Save(migrated, directory);
            return migrated;
        }
        return ParseFile(path);
    }

    public static WorkspaceSettings LoadFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("設定ファイルが見つかりません。", path);
        return ParseFile(path);
    }

    private static WorkspaceSettings ParseFile(string path)
    {
        var fileLines = File.ReadAllLines(path);
        if (!fileLines.Any(l => l.Trim() == "[Application]"))
            throw new ArgumentException("RTL-TCP-CASTの設定ファイルではありません。");
        var sections = new Dictionary<string, List<string>>();
        var application = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string section = "";
        foreach (var raw in fileLines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1]; continue; }
            if (section == "Application")
            {
                var split = line.IndexOf('=');
                if (split > 0) application[line[..split]] = line[(split + 1)..];
            }
            else if (section.StartsWith("Session."))
            {
                var split = section.IndexOf('.', 8);
                if (split < 0) throw new ArgumentException("配信設定のINIセクションが不正です。");
                var id = section[8..split];
                if (!sections.TryGetValue(id, out var lines)) sections[id] = lines = new();
                lines.Add("[" + section[(split + 1)..] + "]");
                lines.Add(line);
            }
        }
        var sessions = sections.Select(p => SettingsStore.Parse(p.Value) with { SessionId = p.Key, RtlTcpPath = "" }).ToList();
        if (sessions.Count == 0) throw new ArgumentException("配信設定がありません。");
        var selectedSessionId = application.GetValueOrDefault("SelectedSessionId", sessions[0].SessionId);
        if (!Guid.TryParseExact(selectedSessionId, "N", out _) || !sessions.Any(s => s.SessionId == selectedSessionId))
            throw new ArgumentException("選択中の配信タブIDが設定ファイル内にありません。");
        if (sessions.Any(s => !Guid.TryParseExact(s.SessionId, "N", out _)))
            throw new ArgumentException("配信設定の識別子が不正です。");
        return new() { RtlTcpPath = application.GetValueOrDefault("RtlTcpPath", ""),
            StartWithWindows = bool.TryParse(application.GetValueOrDefault("StartWithWindows", "false"), out var startup) && startup,
            AutoStartStreaming = bool.TryParse(application.GetValueOrDefault("AutoStartStreaming", "false"), out var autoStartStreaming) && autoStartStreaming,
            HideTrayNotice = bool.TryParse(application.GetValueOrDefault("HideTrayNotice", "false"), out var hideTrayNotice) && hideTrayNotice,
            SelectedSessionId = selectedSessionId, Sessions = sessions };
    }

    public static void Save(WorkspaceSettings settings, string? directory = null)
    {
        directory ??= SettingsStore.DefaultDirectory;
        SaveFile(settings, Path.Combine(directory, "settings.ini"));
    }

    public static void SaveFile(WorkspaceSettings settings, string path)
    {
        if (settings.Sessions.Count == 0 || settings.Sessions.Select(s => s.SessionId).Distinct().Count() != settings.Sessions.Count)
            throw new ArgumentException("配信設定の識別子が不正です。");
        if (!settings.Sessions.Any(s => s.SessionId == settings.SelectedSessionId))
            throw new ArgumentException("選択中の配信タブが設定に含まれていません。");
        if (settings.RtlTcpPath.Contains('\n') || settings.RtlTcpPath.Contains('\r')) throw new ArgumentException("パスに改行は使えません。");
        var lines = new List<string> { "; RTL-TCP-CAST settings", "[Application]", $"RtlTcpPath={settings.RtlTcpPath}",
            $"SelectedSessionId={settings.SelectedSessionId}", $"StartWithWindows={settings.StartWithWindows.ToString().ToLowerInvariant()}",
            $"AutoStartStreaming={settings.AutoStartStreaming.ToString().ToLowerInvariant()}",
            $"HideTrayNotice={settings.HideTrayNotice.ToString().ToLowerInvariant()}", "" };
        foreach (var session in settings.Sessions)
        {
            if (!Guid.TryParseExact(session.SessionId, "N", out _)) throw new ArgumentException("配信IDが不正です。");
            foreach (var line in SettingsStore.Serialize(session).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                if (line.StartsWith("RtlTcpPath=")) continue;
                lines.Add(line.StartsWith('[') ? $"[Session.{session.SessionId}.{line[1..]}" : line);
            }
        }
        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllLines(temporary, lines, new UTF8Encoding(false)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
