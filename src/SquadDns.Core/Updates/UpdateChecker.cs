using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace SquadDns.Core.Updates;

public sealed record UpdateInfo(string Version, string Url, string NotesFr, string NotesEn, string? ReleaseDate)
{
    public bool IsNewerAgainst(string current)
    {
        if (!System.Version.TryParse(Normalize(Version), out var incoming) || !System.Version.TryParse(Normalize(current), out var local))
        {
            return !string.Equals(incoming is null ? Version : Normalize(Version), current, StringComparison.OrdinalIgnoreCase);
        }

        return incoming > local;
    }

    private static string Normalize(string value)
    {
        var trimmed = value.Trim().TrimStart('v', 'V');
        var parts = trimmed.Split('-')[0].Split('.');
        while (parts.Length < 3)
        {
            trimmed += ".0";
            parts = trimmed.Split('.');
        }

        return parts.Length > 3 ? string.Join('.', parts.Take(3)) : trimmed;
    }
}

public sealed class UpdateChecker
{
    private readonly HttpClient _http;

    public UpdateChecker(HttpClient? http = null) =>
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

    public static string DefaultFeedPlaceholder => "https://example.invalid/squadns/update.json";

    public async Task<UpdateInfo?> CheckAsync(string feedUrl, string currentVersion, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(feedUrl) || !feedUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            using var response = await _http.GetAsync(feedUrl, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var payload = await response.Content.ReadAsStringAsync(ct);
            var info = JsonSerializer.Deserialize<UpdateInfo>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return info is not null && info.IsNewerAgainst(currentVersion) ? info : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
        {
            return null;
        }
    }
}

public static class AppInfo
{
    public static string Version => typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    public static string Product => "Squad DNS";
    public const string DonationAddress = "xel:6mmj85x7504h3z9qwendxhahc4804xgrek59rec3zhhexcdywe8qqvnypnh";
    public const string SwapUrl = "https://trocador.app/?ref=BLbjXxTsoK";
    public const string XelisUrl = "https://www.xelis.io";
}
