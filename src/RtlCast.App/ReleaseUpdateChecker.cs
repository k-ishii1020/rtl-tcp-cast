// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 いしかず
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RtlCast.App;

internal sealed record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string? TagName,
    [property: JsonPropertyName("html_url")] string? HtmlUrl);

internal static class ReleaseUpdateChecker
{
    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RTL-TCP-Cast-UpdateChecker/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static async Task<GitHubRelease?> GetNewerReleaseAsync(string currentVersion)
    {
        try
        {
            using var response = await Client.GetAsync(
                "https://api.github.com/repos/k-ishii1020/rtl-tcp-cast/releases/latest").ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            await using var content = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(content,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)).ConfigureAwait(false);
            if (release?.TagName is not { } tagName ||
                !TryParseVersion(currentVersion, out var installed) ||
                !TryParseVersion(tagName, out var latest) || latest <= installed)
                return null;

            return release;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            return null;
        }
    }

    private static bool TryParseVersion(string value, out Version version)
    {
        var normalized = value.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V')) normalized = normalized[1..];
        var suffixIndex = normalized.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0) normalized = normalized[..suffixIndex];
        return Version.TryParse(normalized, out version!);
    }
}
