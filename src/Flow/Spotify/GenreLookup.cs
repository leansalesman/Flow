using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace Flow.Spotify;

/// <summary>
/// Looks up album genres in Deezer's public catalog (no account or key needed). Spotify no longer returns
/// genres to personal developer apps, so Flow uses Deezer's broad genres (Pop, Rap/Hip Hop, Electro, ...).
/// Results must match the artist name to be accepted.
/// </summary>
public sealed class GenreLookup
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private Dictionary<int, string>? _names;
    private DateTime _nextRequest = DateTime.MinValue;

    public GenreLookup() => _http.DefaultRequestHeaders.UserAgent.ParseAdd("Flow-MusicPlayer/1.0");

    /// <summary>Genre for an album (searching by album artist + title), or "" when Deezer doesn't know it.</summary>
    public async Task<string> ForAlbumAsync(string artist, string album)
    {
        var data = await SearchAsync("album", $"artist:\"{artist}\" album:\"{album}\"");
        if (data == null) return "";
        foreach (var hit in data)
        {
            if (!ArtistMatches(hit?["artist"]?["name"]?.GetValue<string>(), artist)) continue;
            if (!TitleMatches(hit?["title"]?.GetValue<string>(), album)) continue;
            var name = await GenreNameAsync(hit?["genre_id"]?.GetValue<int>() ?? -1);
            if (name.Length > 0) return name;
        }
        return "";
    }

    /// <summary>Genre for a single song (used for compilations, where every song has a different artist).</summary>
    public async Task<string> ForTrackAsync(string artist, string title)
    {
        var data = await SearchAsync("track", $"artist:\"{artist}\" track:\"{title}\"");
        if (data == null) return "";
        foreach (var hit in data)
        {
            if (!ArtistMatches(hit?["artist"]?["name"]?.GetValue<string>(), artist)) continue;
            var albumId = hit?["album"]?["id"]?.GetValue<long>();
            if (albumId == null) continue;
            var album = await GetAsync($"https://api.deezer.com/album/{albumId}");
            var name = album?["genres"]?["data"]?[0]?["name"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(name)) return name;
        }
        return "";
    }

    private async Task<JsonArray?> SearchAsync(string kind, string query)
    {
        var json = await GetAsync($"https://api.deezer.com/search/{kind}?limit=5&q={Uri.EscapeDataString(query)}");
        return json?["data"] as JsonArray;
    }

    private async Task<string> GenreNameAsync(int id)
    {
        if (id <= 0) return "";
        if (_names == null)
        {
            _names = new Dictionary<int, string>();
            var list = await GetAsync("https://api.deezer.com/genre");
            if (list?["data"] is JsonArray arr)
                foreach (var g in arr)
                {
                    var gid = g?["id"]?.GetValue<int>() ?? 0;
                    var n = g?["name"]?.GetValue<string>();
                    if (gid > 0 && n != null) _names[gid] = n;
                }
        }
        return _names.TryGetValue(id, out var name) ? name : "";
    }

    /// <summary>GET with gentle pacing (Deezer allows ~50 requests / 5 s) and quota back-off.</summary>
    private async Task<JsonNode?> GetAsync(string url)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var wait = _nextRequest - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
            _nextRequest = DateTime.UtcNow.AddMilliseconds(130);
            try
            {
                var text = await _http.GetStringAsync(url);
                var json = JsonNode.Parse(text);
                // Deezer reports quota errors in the body: {"error":{"type":"Exception","code":4,...}}
                if (json?["error"]?["code"]?.GetValue<int>() == 4) { await Task.Delay(5000); continue; }
                return json?["error"] != null ? null : json;
            }
            catch { await Task.Delay(1000); }
        }
        return null;
    }

    // ---- Matching ----

    private static bool ArtistMatches(string? found, string wanted)
    {
        if (string.IsNullOrEmpty(found)) return false;
        string a = Norm(found), b = Norm(wanted);
        return a.Length > 0 && b.Length > 0 && (a == b || a.Contains(b) || b.Contains(a));
    }

    private static bool TitleMatches(string? found, string wanted)
    {
        if (string.IsNullOrEmpty(found)) return false;
        string a = Norm(StripExtras(found)), b = Norm(StripExtras(wanted));
        return a.Length > 0 && b.Length > 0 && (a == b || a.StartsWith(b) || b.StartsWith(a));
    }

    /// <summary>Drops "(Deluxe Edition)", "[Remastered]", " - Single" style suffixes.</summary>
    private static string StripExtras(string s)
    {
        int cut = s.IndexOfAny(new[] { '(', '[' });
        if (cut > 0) s = s[..cut];
        int dash = s.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) s = s[..dash];
        return s;
    }

    /// <summary>Lowercase letters/digits only, accents removed, leading "the " dropped.</summary>
    private static string Norm(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (ch == '&') sb.Append("and");
        }
        var r = sb.ToString();
        return r.StartsWith("the") && r.Length > 6 ? r[3..] : r;
    }
}
