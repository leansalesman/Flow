using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flow.Infrastructure;
using Flow.Library;
using Flow.Services;

namespace Flow.Spotify;

public sealed class SpotifyPlaylist
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Uris { get; set; } = new();
}

/// <summary>Cached import, saved to %LOCALAPPDATA%\Flow\spotify_library.json.</summary>
public sealed class SpotifyLibraryCache
{
    public List<SpotifyTrackDto> Tracks { get; set; } = new();
    public List<SpotifyPlaylist> Playlists { get; set; } = new();
    /// <summary>"album:{id}" / "track:{uri}" → genre ("" = looked up, not found). Cached across syncs.</summary>
    public Dictionary<string, string> Genres { get; set; } = new();
}

public sealed class SpotifyTrackDto
{
    public string Uri { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string AlbumArtist { get; set; } = "";
    public string Album { get; set; } = "";
    public string AlbumId { get; set; } = "";
    public int Year { get; set; }
    public int TrackNumber { get; set; }
    public int DiscNumber { get; set; }
    public long DurationMs { get; set; }
    public string? ArtUrl { get; set; }
    public DateTime Added { get; set; }
    public List<string>? ArtistIds { get; set; }
    public string Genre { get; set; } = "";
}

/// <summary>
/// Spotify Web API access: PKCE sign-in (no client secret), DPAPI-encrypted token storage,
/// token refresh, rate-limit handling and the library import.
/// </summary>
public sealed class SpotifyService : ObservableObject
{
    public const int CallbackPort = 43117;
    public static readonly string RedirectUri = $"http://127.0.0.1:{CallbackPort}/callback";
    public const string DashboardUrl = "https://developer.spotify.com/dashboard";

    private const string Scopes =
        "user-library-read playlist-read-private playlist-read-collaborative " +
        "user-read-playback-state user-modify-playback-state user-read-currently-playing";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly SettingsService _settings;
    private readonly LibraryService _library;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly string _tokenPath, _cachePath;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private readonly SemaphoreSlim _syncGate = new(1, 1);

    private string? _accessToken, _refreshToken;
    private DateTime _expiresAt;

    public SpotifyLibraryCache Cache { get; private set; } = new();
    public event Action? LibraryImported;

    public SpotifyService(SettingsService settings, LibraryService library)
    {
        _settings = settings;
        _library = library;
        _tokenPath = Path.Combine(settings.DataDir, "spotify.token");
        _cachePath = Path.Combine(settings.DataDir, "spotify_library.json");
        LoadToken();
    }

    // ---- Observable state ----

    public bool IsConnected => _refreshToken != null;

    private string? _userName;
    public string? UserName { get => _userName; private set { Set(ref _userName, value); OnPropertyChanged(nameof(StatusText)); } }

    private string _status = "";
    public string Status { get => _status; private set { Set(ref _status, value); OnPropertyChanged(nameof(StatusText)); } }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(System.Windows.Input.CommandManager.InvalidateRequerySuggested);
        }
    }

    public string StatusText
    {
        get
        {
            if (!string.IsNullOrEmpty(_status)) return _status;
            if (!IsConnected) return "Not connected";
            var n = _library.SpotifyCount;
            var when = _settings.Current.SpotifyLastSync is DateTime d ? $" · synced {Ago(d)}" : "";
            return $"Connected{(UserName != null ? " as " + UserName : "")} · {n:N0} songs{when}";
        }
    }

    private static string Ago(DateTime d)
    {
        var s = DateTime.Now - d;
        if (s.TotalMinutes < 1) return "just now";
        if (s.TotalHours < 1) return $"{(int)s.TotalMinutes} min ago";
        if (s.TotalDays < 1) return $"{(int)s.TotalHours} h ago";
        return d.ToString("MMM d");
    }

    private void RaiseState()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(StatusText));
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(System.Windows.Input.CommandManager.InvalidateRequerySuggested);
    }

    // ---- Token storage (DPAPI, current user) ----

    private void LoadToken()
    {
        try
        {
            if (!File.Exists(_tokenPath)) return;
            var json = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(_tokenPath), null, DataProtectionScope.CurrentUser));
            var node = JsonNode.Parse(json)!;
            _refreshToken = node["refresh"]?.GetValue<string>();
            _accessToken = node["access"]?.GetValue<string>();
            _expiresAt = node["expires"]?.GetValue<DateTime>() ?? DateTime.MinValue;
        }
        catch { _refreshToken = null; }
    }

    private void SaveToken()
    {
        try
        {
            var json = new JsonObject { ["refresh"] = _refreshToken, ["access"] = _accessToken, ["expires"] = _expiresAt }.ToJsonString();
            File.WriteAllBytes(_tokenPath, ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) { App.Log(ex); }
    }

    // ---- Sign-in (Authorization Code with PKCE) ----

    public async Task<bool> ConnectAsync()
    {
        var clientId = _settings.Current.SpotifyClientId.Trim();
        if (clientId.Length == 0) { Status = "Paste your Spotify Client ID first"; return false; }

        IsBusy = true;
        Status = "Waiting for you to approve Flow in your browser…";
        TcpListener? listener = null;
        try
        {
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var state = Base64Url(RandomNumberGenerator.GetBytes(16));

            listener = new TcpListener(IPAddress.Loopback, CallbackPort);
            listener.Start();

            var url = "https://accounts.spotify.com/authorize" +
                      $"?client_id={Uri.EscapeDataString(clientId)}&response_type=code" +
                      $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                      $"&code_challenge_method=S256&code_challenge={challenge}" +
                      $"&scope={Uri.EscapeDataString(Scopes)}&state={state}";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            string? code = null, error = null;
            while (code == null && error == null)
            {
                using var client = await listener.AcceptTcpClientAsync(cts.Token);
                var (path, query) = await ReadRequestAsync(client, cts.Token);
                if (!path.StartsWith("/callback", StringComparison.Ordinal)) { await RespondAsync(client, 404, "Not found"); continue; }
                if (query.GetValueOrDefault("state") != state) { await RespondAsync(client, 400, "State mismatch — please try again from Flow."); error = "state mismatch"; break; }
                error = query.GetValueOrDefault("error");
                code = query.GetValueOrDefault("code");
                await RespondAsync(client, 200, error == null
                    ? "Flow is connected to Spotify. You can close this tab and return to Flow."
                    : "Spotify sign-in was cancelled. You can close this tab.");
            }
            if (code == null) { Status = error == "access_denied" ? "Sign-in cancelled" : $"Sign-in failed: {error}"; return false; }

            var ok = await RequestTokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = RedirectUri,
                ["client_id"] = clientId,
                ["code_verifier"] = verifier,
            });
            if (!ok) { Status = "Spotify rejected the sign-in. Check the Client ID and Redirect URI."; return false; }

            Status = "";
            RaiseState();
            await LoadProfileAsync();
            return true;
        }
        catch (OperationCanceledException) { Status = "Sign-in timed out"; return false; }
        catch (SocketException) { Status = $"Port {CallbackPort} is busy — close other apps using it and retry"; return false; }
        catch (Exception ex) { App.Log(ex); Status = "Sign-in failed: " + ex.Message; return false; }
        finally
        {
            listener?.Stop();
            IsBusy = false;
        }
    }

    public void Disconnect()
    {
        _accessToken = _refreshToken = null;
        try { File.Delete(_tokenPath); } catch { }
        try { File.Delete(_cachePath); } catch { }
        Cache = new SpotifyLibraryCache();
        _library.SetSpotifyTracks(Array.Empty<Track>());
        _settings.Current.SpotifyLastSync = null;
        UserName = null;
        Status = "";
        RaiseState();
        LibraryImported?.Invoke();
    }

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<(string Path, Dictionary<string, string> Query)> ReadRequestAsync(TcpClient client, CancellationToken ct)
    {
        var stream = client.GetStream();
        var buf = new byte[8192];
        int n = await stream.ReadAsync(buf, ct);
        var line = Encoding.ASCII.GetString(buf, 0, n).Split("\r\n")[0]; // GET /callback?code=... HTTP/1.1
        var parts = line.Split(' ');
        var target = parts.Length > 1 ? parts[1] : "/";
        var q = new Dictionary<string, string>();
        var idx = target.IndexOf('?');
        var path = idx >= 0 ? target[..idx] : target;
        if (idx >= 0)
            foreach (var kv in target[(idx + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = kv.IndexOf('=');
                if (eq > 0) q[Uri.UnescapeDataString(kv[..eq])] = Uri.UnescapeDataString(kv[(eq + 1)..].Replace('+', ' '));
            }
        return (path, q);
    }

    private static async Task RespondAsync(TcpClient client, int status, string message)
    {
        var html = "<!doctype html><html><head><meta charset=\"utf-8\"><title>Flow</title></head>" +
                   "<body style=\"font-family:Segoe UI,sans-serif;background:#16161a;color:#eee;display:flex;" +
                   "align-items:center;justify-content:center;height:100vh;margin:0\"><div style=\"text-align:center\">" +
                   "<h2 style=\"font-weight:600\">Flow</h2><p>" + WebUtility.HtmlEncode(message) + "</p></div></body></html>";
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        var s = client.GetStream();
        await s.WriteAsync(head);
        await s.WriteAsync(body);
        await s.FlushAsync();
    }

    private async Task<bool> RequestTokenAsync(Dictionary<string, string> form)
    {
        using var resp = await _http.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(form));
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            DiagLog.Write($"Spotify token error {(int)resp.StatusCode}: {text}");
            return false;
        }
        var node = JsonNode.Parse(text)!;
        _accessToken = node["access_token"]!.GetValue<string>();
        _expiresAt = DateTime.UtcNow.AddSeconds(node["expires_in"]?.GetValue<int>() ?? 3600);
        var refresh = node["refresh_token"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(refresh)) _refreshToken = refresh; // Spotify may rotate it
        SaveToken();
        return true;
    }

    private async Task<string?> GetAccessTokenAsync(bool forceRefresh = false)
    {
        if (_refreshToken == null) return null;
        if (!forceRefresh && _accessToken != null && DateTime.UtcNow < _expiresAt.AddSeconds(-60)) return _accessToken;
        await _tokenGate.WaitAsync();
        try
        {
            if (!forceRefresh && _accessToken != null && DateTime.UtcNow < _expiresAt.AddSeconds(-60)) return _accessToken;
            var ok = await RequestTokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = _refreshToken!,
                ["client_id"] = _settings.Current.SpotifyClientId.Trim(),
            });
            if (!ok)
            {
                // Refresh token revoked/expired: require a new sign-in.
                _accessToken = _refreshToken = null;
                try { File.Delete(_tokenPath); } catch { }
                Status = "Spotify session expired — please connect again";
                RaiseState();
                return null;
            }
            return _accessToken;
        }
        finally { _tokenGate.Release(); }
    }

    // ---- API transport ----

    public sealed record ApiResult(HttpStatusCode Status, JsonNode? Json)
    {
        public bool Ok => (int)Status is >= 200 and < 300;
    }

    public async Task<ApiResult> SendAsync(HttpMethod method, string url, object? body = null)
    {
        if (!url.StartsWith("http", StringComparison.Ordinal)) url = "https://api.spotify.com/v1" + url;
        bool refreshed = false;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var token = await GetAccessTokenAsync(forceRefresh: false);
            if (token == null) return new ApiResult(HttpStatusCode.Unauthorized, null);
            using var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json");
            else if (method == HttpMethod.Put || method == HttpMethod.Post) req.Content = new StringContent("", Encoding.UTF8, "application/json");

            HttpResponseMessage resp;
            try { resp = await _http.SendAsync(req); }
            catch (Exception ex) when (attempt < 2) { DiagLog.Write("Spotify request failed: " + ex.Message); await Task.Delay(500); continue; }

            using (resp)
            {
                if (resp.StatusCode == HttpStatusCode.Unauthorized && !refreshed)
                {
                    refreshed = true;
                    await GetAccessTokenAsync(forceRefresh: true);
                    continue;
                }
                if (resp.StatusCode == (HttpStatusCode)429)
                {
                    var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, Math.Max(1, wait.TotalSeconds))));
                    continue;
                }
                var text = await resp.Content.ReadAsStringAsync();
                JsonNode? json = null;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    try { json = JsonNode.Parse(text); } catch { }
                }
                if (!resp.IsSuccessStatusCode)
                    SpotifyLog.Write($"{method} {url.Replace("https://api.spotify.com/v1", "")} -> {(int)resp.StatusCode} {(text.Length > 300 ? text[..300] : text)}");
                return new ApiResult(resp.StatusCode, json);
            }
        }
        return new ApiResult(HttpStatusCode.ServiceUnavailable, null);
    }

    public Task<ApiResult> GetAsync(string url) => SendAsync(HttpMethod.Get, url);

    private async Task LoadProfileAsync()
    {
        var r = await GetAsync("/me");
        if (r.Ok) UserName = r.Json?["display_name"]?.GetValue<string>() ?? r.Json?["id"]?.GetValue<string>();
    }

    private async IAsyncEnumerable<JsonNode> PagesAsync(string firstUrl)
    {
        string? url = firstUrl;
        int guard = 0;
        while (url != null && guard++ < 400)
        {
            var r = await GetAsync(url);
            if (!r.Ok || r.Json == null) yield break;
            if (r.Json["items"] is JsonArray items)
                foreach (var it in items)
                    if (it != null) yield return it;
            url = r.Json["next"]?.GetValue<string>();
        }
    }

    // ---- Library import ----

    public void LoadCache()
    {
        try
        {
            if (File.Exists(_cachePath))
                Cache = JsonSerializer.Deserialize<SpotifyLibraryCache>(File.ReadAllText(_cachePath)) ?? new();
        }
        catch { Cache = new(); }
        if (IsConnected) _library.SetSpotifyTracks(Cache.Tracks.Select(ToTrack));
        RaiseState();
        if (IsConnected) _ = LoadProfileAsync();
    }

    public bool SyncIsDue => IsConnected &&
        (_settings.Current.SpotifyLastSync is not DateTime d || (DateTime.Now - d).TotalHours > 12
         || Cache.Tracks.Any(t => t.ArtistIds == null)); // older cache without artist IDs (needed for genres)

    public async Task SyncAsync()
    {
        if (!IsConnected || !await _syncGate.WaitAsync(0)) return;
        IsBusy = true;
        try
        {
            var s = _settings.Current;
            var byUri = new Dictionary<string, SpotifyTrackDto>();
            var playlists = new List<SpotifyPlaylist>();

            void Add(SpotifyTrackDto? t)
            {
                if (t == null) return;
                if (byUri.TryGetValue(t.Uri, out var e)) { if (t.Added < e.Added) e.Added = t.Added; }
                else byUri[t.Uri] = t;
            }

            if (s.SpotifyImportLiked)
            {
                Status = "Importing Liked Songs…";
                await foreach (var it in PagesAsync("/me/tracks?limit=50"))
                {
                    Add(ParseTrack(it["track"], null, ParseDate(it["added_at"])));
                    if (byUri.Count % 250 == 0) Status = $"Importing Liked Songs… {byUri.Count:N0}";
                }
            }

            if (s.SpotifyImportAlbums)
            {
                Status = "Importing saved albums…";
                await foreach (var it in PagesAsync("/me/albums?limit=50"))
                {
                    var album = it["album"];
                    if (album == null) continue;
                    var added = ParseDate(it["added_at"]);
                    var tracks = album["tracks"];
                    if (tracks?["items"] is JsonArray first)
                        foreach (var t in first) Add(ParseTrack(t, album, added));
                    var next = tracks?["next"]?.GetValue<string>();
                    if (next != null)
                        await foreach (var t in PagesAsync(next)) Add(ParseTrack(t, album, added));
                }
            }

            if (s.SpotifyImportPlaylists)
            {
                Status = "Importing playlists…";
                var me = (await GetAsync("/me")).Json?["id"]?.GetValue<string>();
                await foreach (var p in PagesAsync("/me/playlists?limit=50"))
                {
                    var id = p["id"]?.GetValue<string>();
                    var name = p["name"]?.GetValue<string>() ?? "Playlist";
                    var owner = p["owner"]?["id"]?.GetValue<string>();
                    bool collab = p["collaborative"]?.GetValue<bool>() ?? false;
                    // Spotify only exposes the contents of playlists you own or collaborate on.
                    if (id == null || (owner != me && !collab)) continue;
                    Status = $"Importing playlist “{name}”…";
                    var pl = new SpotifyPlaylist { Id = id, Name = name };
                    await foreach (var it in PagesAsync($"/playlists/{id}/items?limit=50&additional_types=track"))
                    {
                        if (it["is_local"]?.GetValue<bool>() == true) continue;
                        var t = ParseTrack(it["item"] ?? it["track"], null, ParseDate(it["added_at"]));
                        if (t == null) continue;
                        Add(t);
                        pl.Uris.Add(t.Uri);
                    }
                    playlists.Add(pl);
                }
            }

            Cache = new SpotifyLibraryCache { Tracks = byUri.Values.ToList(), Playlists = playlists, Genres = Cache.Genres };
            ApplyGenres();
            SaveCache();
            _library.SetSpotifyTracks(Cache.Tracks.Select(ToTrack));
            s.SpotifyLastSync = DateTime.Now;
            Status = "Downloading album art…";
            await DownloadArtAsync();
            await ImportGenresAsync();
            Status = "";
            LibraryImported?.Invoke();
        }
        catch (Exception ex)
        {
            App.Log(ex);
            Status = "Spotify sync failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            RaiseState();
            _syncGate.Release();
        }
    }

    private void SaveCache()
    {
        try { File.WriteAllText(_cachePath, JsonSerializer.Serialize(Cache)); } catch (Exception ex) { App.Log(ex); }
    }

    // ---- Genres ----
    // Spotify no longer returns genres to personal apps, so Flow looks them up in Deezer's public catalog:
    // once per album (or per song on compilations), cached so later syncs only look up new albums.

    private readonly GenreLookup _genres = new();

    private static string FirstArtist(string artists) => artists.Split(", ")[0];
    private static bool IsCompilation(string albumArtist) =>
        string.IsNullOrWhiteSpace(albumArtist) || albumArtist.Equals("Various Artists", StringComparison.OrdinalIgnoreCase);

    private async Task ImportGenresAsync()
    {
        var known = Cache.Genres;
        var jobs = new List<(string Key, Func<Task<string>> Lookup)>();
        foreach (var album in Cache.Tracks.GroupBy(t => t.AlbumId))
        {
            var first = album.First();
            if (!IsCompilation(first.AlbumArtist))
            {
                var key = "album:" + album.Key;
                if (!known.ContainsKey(key))
                    jobs.Add((key, async () =>
                    {
                        var g = await _genres.ForAlbumAsync(first.AlbumArtist, first.Album);
                        return g.Length > 0 ? g : await _genres.ForTrackAsync(FirstArtist(first.Artist), first.Title);
                    }));
            }
            else
            {
                foreach (var t in album)
                {
                    var key = "track:" + t.Uri;
                    if (!known.ContainsKey(key))
                        jobs.Add((key, () => _genres.ForTrackAsync(FirstArtist(t.Artist), t.Title)));
                }
            }
        }
        if (jobs.Count == 0) return;

        SpotifyLog.Write($"Genres: looking up {jobs.Count} album(s)/song(s) on Deezer");
        int done = 0, found = 0;
        foreach (var (key, lookup) in jobs)
        {
            string genre;
            try { genre = await lookup(); } catch { genre = ""; }
            known[key] = genre; // "" = looked up, not found (won't be retried every sync)
            if (genre.Length > 0) found++;
            if (++done % 25 == 0)
            {
                Status = $"Importing genres… {done:N0} of {jobs.Count:N0}";
                ApplyGenres();
                _library.SetSpotifyTracks(Cache.Tracks.Select(ToTrack));
                SaveCache();
            }
        }
        ApplyGenres();
        _library.SetSpotifyTracks(Cache.Tracks.Select(ToTrack));
        SaveCache();
        SpotifyLog.Write($"Genres: done - {found} of {jobs.Count} found");
    }

    /// <summary>Fills each track's Genre from the cache (song-level first, then album-level).</summary>
    private void ApplyGenres()
    {
        foreach (var t in Cache.Tracks)
        {
            t.Genre = Cache.Genres.TryGetValue("track:" + t.Uri, out var g) && g.Length > 0 ? g
                    : Cache.Genres.TryGetValue("album:" + t.AlbumId, out var a) ? a
                    : "";
        }
    }

    private async Task DownloadArtAsync()
    {
        var art = _library.Art;
        var jobs = Cache.Tracks.GroupBy(t => t.AlbumId).Select(g => g.First())
            .Select(t => (Key: ArtKeyFor(t.AlbumId, t.Uri), t.ArtUrl))
            .Where(j => !art.Has(j.Key)).ToList();
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(jobs.Select(async j =>
        {
            await gate.WaitAsync();
            try { await art.EnsureFromUrlAsync(j.Key, j.ArtUrl); } finally { gate.Release(); }
        }));
        if (jobs.Count > 0) _library.SetSpotifyTracks(Cache.Tracks.Select(ToTrack)); // refresh shelves with art
    }

    private static DateTime ParseDate(JsonNode? n) =>
        n != null && DateTime.TryParse(n.GetValue<string>(), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var d)
            ? d.ToLocalTime() : DateTime.Now;

    private static SpotifyTrackDto? ParseTrack(JsonNode? t, JsonNode? albumOverride, DateTime added)
    {
        if (t == null) return null;
        if (t["type"]?.GetValue<string>() is string type && type != "track") return null;
        var uri = t["uri"]?.GetValue<string>();
        if (uri == null || !uri.StartsWith("spotify:track:", StringComparison.Ordinal)) return null;
        var album = albumOverride ?? t["album"];
        string Names(JsonNode? arr) => arr is JsonArray a
            ? string.Join(", ", a.Select(x => x?["name"]?.GetValue<string>()).Where(x => !string.IsNullOrEmpty(x)))
            : "";
        var release = album?["release_date"]?.GetValue<string>() ?? "";
        string? image = null;
        if (album?["images"] is JsonArray imgs && imgs.Count > 0)
            image = imgs.OrderByDescending(i => i?["width"]?.GetValue<int>() ?? 0).First()?["url"]?.GetValue<string>();
        var albumArtists = Names(album?["artists"]);
        return new SpotifyTrackDto
        {
            Uri = uri,
            Title = t["name"]?.GetValue<string>() ?? "",
            Artist = Names(t["artists"]),
            AlbumArtist = albumArtists.Split(", ")[0],
            Album = album?["name"]?.GetValue<string>() ?? "",
            AlbumId = album?["id"]?.GetValue<string>() ?? "",
            Year = release.Length >= 4 && int.TryParse(release[..4], out var y) ? y : 0,
            TrackNumber = t["track_number"]?.GetValue<int>() ?? 0,
            DiscNumber = t["disc_number"]?.GetValue<int>() ?? 0,
            DurationMs = t["duration_ms"]?.GetValue<long>() ?? 0,
            ArtUrl = image,
            Added = added,
            ArtistIds = t["artists"] is JsonArray ids
                ? ids.Select(a => a?["id"]?.GetValue<string>()).Where(id => !string.IsNullOrEmpty(id)).Cast<string>().ToList()
                : new List<string>(),
        };
    }

    private static string ArtKeyFor(string albumId, string uri) =>
        ArtworkCache.MakeKey("", "", "spotify:album:" + (string.IsNullOrEmpty(albumId) ? uri : albumId));

    public static Track ToTrack(SpotifyTrackDto d) => new()
    {
        Path = d.Uri,
        SpotifyUri = d.Uri,
        SpotifyAlbumUri = string.IsNullOrEmpty(d.AlbumId) ? null : Pool("spotify:album:" + d.AlbumId),
        Title = d.Title,
        Artist = Pool(d.Artist),
        AlbumArtist = Pool(d.AlbumArtist),
        Album = Pool(d.Album),
        Year = d.Year,
        TrackNumber = d.TrackNumber,
        DiscNumber = d.DiscNumber,
        Duration = TimeSpan.FromMilliseconds(d.DurationMs),
        Format = "Spotify",
        DateAdded = d.Added,
        ArtUrl = Pool(d.ArtUrl),
        ArtKey = Pool(ArtKeyFor(d.AlbumId, d.Uri)),
        Genre = Pool(d.Genre),
    };

    /// <summary>Shares one copy of strings repeated across thousands of songs (artist, album, genre…).</summary>
    private static string Pool(string? s) => string.IsNullOrEmpty(s) ? "" : string.Intern(s);

    public static void OpenDashboard() =>
        Process.Start(new ProcessStartInfo(DashboardUrl) { UseShellExecute = true });
}
