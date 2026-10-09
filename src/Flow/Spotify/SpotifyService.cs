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
    /// <summary>Spotify's version of the playlist; unchanged = its songs don't need to be fetched again.</summary>
    public string? SnapshotId { get; set; }
}

/// <summary>A saved album and its song URIs (for incremental sync).</summary>
public sealed class SpotifySavedAlbum
{
    public string Id { get; set; } = "";
    public List<string> Uris { get; set; } = new();
}

/// <summary>Cached import, saved to %LOCALAPPDATA%\Flow\spotify_library.json.</summary>
public sealed class SpotifyLibraryCache
{
    public List<SpotifyTrackDto> Tracks { get; set; } = new();
    public List<SpotifyPlaylist> Playlists { get; set; } = new();
    /// <summary>"album:{id}" / "track:{uri}" → genre ("" = looked up, not found). Cached across syncs.</summary>
    public Dictionary<string, string> Genres { get; set; } = new();
    /// <summary>Liked Songs, newest first (null = not imported with incremental sync yet).</summary>
    public List<string>? LikedUris { get; set; }
    /// <summary>Saved albums, newest first (null = not imported with incremental sync yet).</summary>
    public List<SpotifySavedAlbum>? SavedAlbums { get; set; }
}

/// <summary>Spotify search results (Search page).</summary>
public sealed class SpotifySearchResults
{
    public List<SpotifySearchSong> Songs { get; } = new();
    public List<SpotifySearchAlbum> Albums { get; } = new();
}

/// <summary>
/// One line of the Search dropdown: an artist, song or album, with a small picture. Payload is the artist name,
/// the <see cref="SpotifySearchSong"/> or the <see cref="SpotifySearchAlbum"/>.
/// </summary>
public sealed record SpotifySuggestion(string Kind, string Title, string Subtitle, string? ImageUrl, object Payload)
{
    public bool IsArtist => Kind == "Artist";
    /// <summary>The line under the title: "Artist", or "Song · artist" / "Album · artist".</summary>
    public string SubLine => IsArtist ? "Artist" : $"{Kind}  ·  {Subtitle}";
}

/// <summary>A song found on Spotify, with a small cover image URL for the result row.</summary>
public sealed record SpotifySearchSong(Track Track, string? ImageUrl);

/// <summary>An album found on Spotify.</summary>
public sealed record SpotifySearchAlbum(string Uri, string Name, string Artist, int Year, int TotalTracks,
                                        string? ImageUrl, string? LargeImageUrl)
{
    public string InfoLine => (Year > 0 ? Year + "  ·  " : "") + (TotalTracks == 1 ? "1 song" : $"{TotalTracks} songs");
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
        _tracksReleased = _cacheLacksArtistIds = false;
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

    // Shared by every request: at most 6 at once, and after a 429 everyone waits out the same cooldown.
    private readonly SemaphoreSlim _requestGate = new(6);
    private DateTime _cooldownUntil = DateTime.MinValue;

    public async Task<ApiResult> SendAsync(HttpMethod method, string url, object? body = null)
    {
        if (!url.StartsWith("http", StringComparison.Ordinal)) url = "https://api.spotify.com/v1" + url;
#if DEBUG
        if (_fakeApi != null)
        {
            _fakeRequests++;
            var fake = _fakeApi(url);
            return new ApiResult(fake == null ? HttpStatusCode.NotFound : HttpStatusCode.OK, fake);
        }
#endif
        bool refreshed = false, retried5xx = false;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var wait = _cooldownUntil - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
            var token = await GetAccessTokenAsync(forceRefresh: false);
            if (token == null) return new ApiResult(HttpStatusCode.Unauthorized, null);
            using var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json");
            else if (method == HttpMethod.Put || method == HttpMethod.Post) req.Content = new StringContent("", Encoding.UTF8, "application/json");

            HttpResponseMessage resp;
            await _requestGate.WaitAsync();
            try { resp = await _http.SendAsync(req); }
            catch (Exception ex) when (attempt < 2) { DiagLog.Write("Spotify request failed: " + ex.Message); await Task.Delay(500); continue; }
            finally { _requestGate.Release(); }

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
                    var limitText = await resp.Content.ReadAsStringAsync();
                    if (limitText.Contains("QUOTA_EXCEEDED", StringComparison.OrdinalIgnoreCase))
                    {
                        // Spotify's quota for this app is used up: waiting a few seconds won't help.
                        SpotifyLog.Write($"{method} {url.Replace("https://api.spotify.com/v1", "")} -> 429 quota exceeded");
                        Status = "Spotify's request limit for this app has been reached. Try again later.";
                        return new ApiResult((HttpStatusCode)429, null);
                    }
                    var retry = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                    var until = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Clamp(retry.TotalSeconds, 1, 30));
                    if (until > _cooldownUntil) _cooldownUntil = until;
                    SpotifyLog.Write($"Rate limited: waiting {(until - DateTime.UtcNow).TotalSeconds:0} s");
                    continue;
                }
                if ((int)resp.StatusCode >= 500 && method == HttpMethod.Get && !retried5xx)
                {
                    retried5xx = true;   // a GET is safe to repeat once
                    await Task.Delay(800);
                    attempt--;
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
        _cacheLacksArtistIds = Cache.Tracks.Any(t => t.ArtistIds == null);
        if (IsConnected) _library.SetSpotifyTracks(Cache.Tracks.Select(ToTrack));
        ReleaseTracks();
        RaiseState();
        if (IsConnected) _ = LoadProfileAsync();
    }

    // The library keeps its own Track objects; the cache's song list is only needed while syncing, so it is
    // dropped from memory after loading and read from disk again for the next sync.
    private bool _tracksReleased, _cacheLacksArtistIds;

    private void ReleaseTracks()
    {
        if (Cache.Tracks.Count == 0) return;
        Cache.Tracks = new List<SpotifyTrackDto>();
        _tracksReleased = true;
    }

    private void EnsureTracksLoaded()
    {
        if (!_tracksReleased) return;
        _tracksReleased = false;
        try
        {
            if (File.Exists(_cachePath) && JsonSerializer.Deserialize<SpotifyLibraryCache>(File.ReadAllText(_cachePath)) is { } c)
                Cache.Tracks = c.Tracks;
        }
        catch (Exception ex) { App.Log(ex); }
    }

    public bool SyncIsDue => IsConnected &&
        (_settings.Current.SpotifyLastSync is not DateTime d || (DateTime.Now - d).TotalHours > 12
         || _cacheLacksArtistIds); // older cache without artist IDs (needed for genres)

    public async Task SyncAsync()
    {
        if (!IsConnected || !await _syncGate.WaitAsync(0)) return;
        IsBusy = true;
        try
        {
            EnsureTracksLoaded();
            // Incremental: only what changed since the last sync (a list the cache doesn't have yet is read in
            // full). Everything is read again when the cache lacks artist IDs (genres) or a reused song is missing.
            bool full = Cache.Tracks.Any(t => t.ArtistIds == null);
            var result = await ImportAsync(full);
            if (result == null && !full) result = await ImportAsync(full: true);
            if (result == null) throw new InvalidOperationException("Spotify's library couldn't be read completely.");
            var s = _settings.Current;
            Cache = result;
            _cacheLacksArtistIds = false;
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
            ReleaseTracks();
            IsBusy = false;
            RaiseState();
            _syncGate.Release();
        }
    }

    /// <summary>
    /// Reads Liked Songs, saved albums and playlists. Liked Songs and saved albums come newest first, so an
    /// incremental import stops at the first one already cached; Spotify's total tells whether any were removed
    /// (then that list is read in full). Playlists whose snapshot hasn't changed keep their cached songs.
    /// Returns null when an incremental import can't be completed from the cache.
    /// </summary>
    private async Task<SpotifyLibraryCache?> ImportAsync(bool full)
    {
        var s = _settings.Current;
        var old = Cache;
        var oldDtos = new Dictionary<string, SpotifyTrackDto>();
        foreach (var t in old.Tracks) oldDtos.TryAdd(t.Uri, t);
        var byUri = new Dictionary<string, SpotifyTrackDto>();
        bool missing = false;

        void Add(SpotifyTrackDto? t)
        {
            if (t == null) return;
            if (byUri.TryGetValue(t.Uri, out var e)) { if (t.Added < e.Added) e.Added = t.Added; }
            else byUri[t.Uri] = t;
        }
        void Reuse(IEnumerable<string> uris)
        {
            foreach (var u in uris)
            {
                if (u.Length == 0 || byUri.ContainsKey(u)) continue;
                if (oldDtos.TryGetValue(u, out var d)) byUri[u] = d;
                else missing = true;
            }
        }

        List<string>? liked = null;
        if (s.SpotifyImportLiked)
        {
            Status = "Importing Liked Songs…";
            for (int pass = 0; pass < 2 && liked == null; pass++)
            {
                var known = !full && pass == 0 ? old.LikedUris : null;
                var (items, total) = await NewestFirstAsync("/me/tracks?limit=50",
                    it => it["track"]?["uri"]?.GetValue<string>(), known, n => Status = $"Importing Liked Songs… {n:N0}");
                var fresh = items.Select(it => it["track"]?["uri"]?.GetValue<string>() ?? "").ToList();
                var all = known == null ? fresh : fresh.Concat(known).ToList();
                if (known != null && total >= 0 && all.Count != total) continue;   // songs were removed: read all
                foreach (var it in items) Add(ParseTrack(it["track"], null, ParseDate(it["added_at"])));
                if (known != null) Reuse(known);
                liked = all;
            }
        }

        List<SpotifySavedAlbum>? albums = null;
        if (s.SpotifyImportAlbums)
        {
            Status = "Importing saved albums…";
            for (int pass = 0; pass < 2 && albums == null; pass++)
            {
                var known = !full && pass == 0 ? old.SavedAlbums : null;
                var (items, total) = await NewestFirstAsync("/me/albums?limit=50",
                    it => it["album"]?["id"]?.GetValue<string>(), known?.Select(a => a.Id).ToList(), null);
                if (known != null && total >= 0 && items.Count + known.Count != total) continue;   // albums were removed
                var list = new List<SpotifySavedAlbum>();
                foreach (var it in items)
                {
                    var album = it["album"];
                    var entry = new SpotifySavedAlbum { Id = album?["id"]?.GetValue<string>() ?? "" };
                    list.Add(entry);
                    if (album == null) continue;
                    var added = ParseDate(it["added_at"]);
                    void AddAlbumTrack(JsonNode? t)
                    {
                        var d = ParseTrack(t, album, added);
                        if (d == null) return;
                        Add(d);
                        entry.Uris.Add(d.Uri);
                    }
                    var tracks = album["tracks"];
                    if (tracks?["items"] is JsonArray first)
                        foreach (var t in first) AddAlbumTrack(t);
                    var next = tracks?["next"]?.GetValue<string>();
                    if (next != null)
                        await foreach (var t in PagesAsync(next)) AddAlbumTrack(t);
                }
                if (known != null)
                {
                    list.AddRange(known);
                    foreach (var a in known) Reuse(a.Uris);
                }
                albums = list;
            }
        }

        var playlists = new List<SpotifyPlaylist>();
        if (s.SpotifyImportPlaylists)
        {
            Status = "Importing playlists…";
            var me = (await GetAsync("/me")).Json?["id"]?.GetValue<string>();
            var oldPlaylists = new Dictionary<string, SpotifyPlaylist>();
            foreach (var p in old.Playlists) if (p.SnapshotId != null) oldPlaylists.TryAdd(p.Id, p);
            await foreach (var p in PagesAsync("/me/playlists?limit=50"))
            {
                var id = p["id"]?.GetValue<string>();
                var name = p["name"]?.GetValue<string>() ?? "Playlist";
                var owner = p["owner"]?["id"]?.GetValue<string>();
                var snapshot = p["snapshot_id"]?.GetValue<string>();
                bool collab = p["collaborative"]?.GetValue<bool>() ?? false;
                // Spotify only exposes the contents of playlists you own or collaborate on.
                if (id == null || (owner != me && !collab)) continue;
                if (!full && snapshot != null && oldPlaylists.TryGetValue(id, out var same) && same.SnapshotId == snapshot)
                {
                    Reuse(same.Uris);
                    playlists.Add(new SpotifyPlaylist { Id = id, Name = name, Uris = same.Uris, SnapshotId = snapshot });
                    continue;
                }
                Status = $"Importing playlist “{name}”…";
                var pl = new SpotifyPlaylist { Id = id, Name = name, SnapshotId = snapshot };
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

        if (missing) { SpotifyLog.Write("Incremental sync: cached songs missing, reading everything"); return null; }
        SpotifyLog.Write($"Sync ({(full ? "full" : "incremental")}): {byUri.Count:N0} songs, {liked?.Count ?? 0:N0} liked, " +
                         $"{albums?.Count ?? 0:N0} albums, {playlists.Count:N0} playlists");
        return new SpotifyLibraryCache
        {
            Tracks = byUri.Values.ToList(), Playlists = playlists, Genres = old.Genres, LikedUris = liked, SavedAlbums = albums,
        };
    }

#if DEBUG
    // ---- Developer test (--test-sync): incremental sync against a simulated Spotify library, offline ----

    private Func<string, JsonNode?>? _fakeApi;
    private int _fakeRequests;

    public async Task<string> TestIncrementalSyncAsync()
    {
        var log = new List<string>();
        _refreshToken = "test"; _accessToken = "test"; _expiresAt = DateTime.UtcNow.AddHours(1);
        var s = _settings.Current;
        s.SpotifyImportLiked = s.SpotifyImportAlbums = s.SpotifyImportPlaylists = true;

        // The simulated account: newest first everywhere.
        var liked = Enumerable.Range(0, 230).Select(i => $"spotify:track:L{i}").ToList();
        var albums = Enumerable.Range(0, 70).Select(i => $"A{i}").ToList();
        var playlists = new List<(string Id, string Snap, List<string> Uris)>
        {
            ("P0", "s0", Enumerable.Range(0, 120).Select(i => $"spotify:track:Q{i}").ToList()),
            ("P1", "s1", new List<string> { "spotify:track:L3", "spotify:track:X1" }),
        };
        JsonNode Track(string uri, string? albumId = null) => new JsonObject
        {
            ["type"] = "track", ["uri"] = uri, ["name"] = uri, ["duration_ms"] = 1000L, ["track_number"] = 1, ["disc_number"] = 1,
            ["artists"] = new JsonArray(new JsonObject { ["id"] = "ar", ["name"] = "Artist" }),
            ["album"] = new JsonObject { ["id"] = albumId ?? "al", ["name"] = "Album", ["release_date"] = "2020", ["artists"] = new JsonArray(), ["images"] = new JsonArray() },
        };
        JsonNode Page<T>(string baseUrl, List<T> all, int offset, Func<T, JsonNode> item)
        {
            var items = new JsonArray(all.Skip(offset).Take(50).Select(x => (JsonNode?)item(x)).ToArray());
            return new JsonObject
            {
                ["items"] = items, ["total"] = all.Count,
                ["next"] = offset + 50 < all.Count ? $"https://api.spotify.com/v1{baseUrl}&offset={offset + 50}" : null,
            };
        }
        _fakeApi = url =>
        {
            var u = url.Replace("https://api.spotify.com/v1", "");
            int offset = u.Contains("offset=") ? int.Parse(u[(u.IndexOf("offset=") + 7)..].Split('&')[0]) : 0;
            if (u.StartsWith("/me/tracks")) return Page("/me/tracks?limit=50", liked, offset, x => new JsonObject { ["added_at"] = "2024-01-01T00:00:00Z", ["track"] = Track(x) });
            if (u.StartsWith("/me/albums")) return Page("/me/albums?limit=50", albums, offset, a => new JsonObject
            {
                ["added_at"] = "2024-01-01T00:00:00Z",
                ["album"] = new JsonObject
                {
                    ["id"] = a, ["name"] = a, ["release_date"] = "2020", ["artists"] = new JsonArray(new JsonObject { ["name"] = "Band" }), ["images"] = new JsonArray(),
                    ["tracks"] = new JsonObject { ["items"] = new JsonArray(Enumerable.Range(0, 3).Select(k => (JsonNode?)Track($"spotify:track:{a}t{k}", a)).ToArray()) },
                },
            });
            if (u.StartsWith("/me/playlists")) return Page("/me/playlists?limit=50", playlists, offset, p => new JsonObject
            {
                ["id"] = p.Id, ["name"] = p.Id, ["snapshot_id"] = p.Snap, ["owner"] = new JsonObject { ["id"] = "me" },
            });
            if (u == "/me") return new JsonObject { ["id"] = "me" };
            if (u.StartsWith("/playlists/"))
            {
                var p = playlists.First(x => u.StartsWith($"/playlists/{x.Id}/"));
                return Page($"/playlists/{p.Id}/items?limit=50&additional_types=track", p.Uris, offset, x => new JsonObject { ["track"] = Track(x) });
            }
            return null;
        };

        int expected() => liked.Concat(albums.SelectMany(a => Enumerable.Range(0, 3).Select(k => $"spotify:track:{a}t{k}")))
                                   .Concat(playlists.SelectMany(p => p.Uris)).Distinct().Count();
        bool ok = true;
        async Task Step(string name)
        {
            _fakeRequests = 0;
            var r = await ImportAsync(full: false);
            bool good = r != null && r.Tracks.Count == expected() && r.LikedUris!.SequenceEqual(liked)
                        && r.SavedAlbums!.Select(a => a.Id).SequenceEqual(albums)
                        && r.Playlists.Select(p => p.Id + ":" + p.Uris.Count).SequenceEqual(playlists.Select(p => p.Id + ":" + p.Uris.Count));
            ok &= good;
            log.Add($"{(good ? "PASS" : "FAIL")} {name}: {_fakeRequests} requests, {r?.Tracks.Count} songs (expected {expected()})");
            if (r != null) Cache = r;
        }

        Cache = new SpotifyLibraryCache();
        await Step("first sync (reads everything)");
        await Step("nothing changed");
        liked.InsertRange(0, new[] { "spotify:track:N1", "spotify:track:N2" });
        await Step("two songs liked");
        liked.Remove("spotify:track:L100");
        await Step("one song unliked (Liked Songs read again)");
        albums.Insert(0, "A_new");
        await Step("album saved");
        albums.Remove("A40");
        await Step("album removed (albums read again)");
        playlists[1] = ("P1", "s1b", new List<string> { "spotify:track:L3", "spotify:track:X1", "spotify:track:X2" });
        await Step("playlist changed (only it is read)");
        playlists.Add(("P2", "s2", new List<string> { "spotify:track:Y1" }));
        await Step("playlist added");
        log.Add(ok ? "ALL PASSED" : "SOME FAILED");
        return string.Join(Environment.NewLine, log);
    }
#endif

    /// <summary>
    /// Pages a newest-first list until the first item whose key is in <paramref name="known"/> (all of it when
    /// known is null). Returns the new items and Spotify's total (-1 if unknown).
    /// </summary>
    private async Task<(List<JsonNode> Items, int Total)> NewestFirstAsync(string firstUrl, Func<JsonNode, string?> key,
                                                                          IReadOnlyCollection<string>? known, Action<int>? progress)
    {
        var knownSet = known == null ? null : new HashSet<string>(known);
        var list = new List<JsonNode>();
        int total = -1, guard = 0;
        string? url = firstUrl;
        while (url != null && guard++ < 400)
        {
            var r = await GetAsync(url);
            if (!r.Ok || r.Json == null) throw new InvalidOperationException($"Spotify answered {(int)r.Status}.");
            if (total < 0) total = r.Json["total"]?.GetValue<int>() ?? -1;
            if (r.Json["items"] is JsonArray items)
                foreach (var it in items)
                {
                    if (it == null) continue;
                    if (knownSet != null && key(it) is { } k && knownSet.Contains(k)) return (list, total);
                    list.Add(it);
                    if (list.Count % 250 == 0) progress?.Invoke(list.Count);
                }
            url = r.Json["next"]?.GetValue<string>();
        }
        return (list, total);
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
            try { await art.EnsureFromUrlAsync(j.Key, SmallArtUrl(j.ArtUrl)); } finally { gate.Release(); }
        }));
        if (jobs.Count > 0) _library.SetSpotifyTracks(Cache.Tracks.Select(ToTrack)); // refresh shelves with art
    }

    // Spotify cover URLs name their size: ab67616d0000b273 = 640 px, ab67616d00001e02 = 300 px.
    private const string Cover640 = "ab67616d0000b273", Cover300 = "ab67616d00001e02";

    /// <summary>The 640 px version of a Spotify cover URL (the same URL when its size can't be told).</summary>
    public static string? LargeArtUrl(string? url) => url?.Replace(Cover300, Cover640);

    /// <summary>The 300 px version of a Spotify cover URL (older caches stored the 640 px one).</summary>
    public static string? SmallArtUrl(string? url) => url?.Replace(Cover640, Cover300);

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
        // The ~300 px cover (for shelves); the 640 px one is derived from it when a big view needs it.
        string? image = null;
        if (album?["images"] is JsonArray imgs && imgs.Count > 0)
            image = imgs.OrderBy(i => Math.Abs((i?["width"]?.GetValue<int>() ?? 0) - 300)).First()?["url"]?.GetValue<string>();
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

    /// <summary>The storable form of a Spotify song (for the library cache and the saved queue).</summary>
    public static SpotifyTrackDto ToDto(Track t) => new()
    {
        Uri = t.Path, Title = t.Title, Artist = t.Artist, AlbumArtist = t.AlbumArtist, Album = t.Album,
        AlbumId = t.SpotifyAlbumUri?.Split(':').Last() ?? "", Year = t.Year, TrackNumber = t.TrackNumber,
        DiscNumber = t.DiscNumber, DurationMs = (long)t.Duration.TotalMilliseconds, ArtUrl = t.ArtUrl,
        Added = t.DateAdded, Genre = t.Genre,
    };

    /// <summary>Searches all of Spotify (songs and albums). Null when Spotify can't be reached.</summary>
    public async Task<SpotifySearchResults?> SearchAsync(string query)
    {
        var q = Uri.EscapeDataString(query.Trim());
        var r = await GetAsync($"/search?q={q}&type=track,album&limit=10");
        if (!r.Ok || r.Json == null) return null;
        var results = new SpotifySearchResults();
        if (r.Json["tracks"]?["items"] is JsonArray tracks)
            foreach (var t in tracks)
                if (ParseTrack(t, null, DateTime.Now) is { } d)
                    results.Songs.Add(new SpotifySearchSong(ToTrack(d), ImageUrl(t?["album"], 64)));
        if (r.Json["albums"]?["items"] is JsonArray albums)
            foreach (var a in albums)
            {
                var uri = a?["uri"]?.GetValue<string>();
                if (uri == null) continue;
                var release = a!["release_date"]?.GetValue<string>() ?? "";
                var artists = a["artists"] is JsonArray ar
                    ? string.Join(", ", ar.Select(x => x?["name"]?.GetValue<string>()).Where(x => !string.IsNullOrEmpty(x)))
                    : "";
                results.Albums.Add(new SpotifySearchAlbum(uri, a["name"]?.GetValue<string>() ?? "", artists,
                    release.Length >= 4 && int.TryParse(release[..4], out var y) ? y : 0,
                    a["total_tracks"]?.GetValue<int>() ?? 0, ImageUrl(a, 300), ImageUrl(a, 640)));
            }
        return results;
    }

    /// <summary>Search-as-you-type suggestions: a few artists, songs and albums, best matches first.</summary>
    public async Task<List<SpotifySuggestion>?> SuggestAsync(string query)
    {
        var q = Uri.EscapeDataString(query.Trim());
        var r = await GetAsync($"/search?q={q}&type=artist,track,album&limit=4");
        if (!r.Ok || r.Json == null) return null;
        var list = new List<SpotifySuggestion>();
        if (r.Json["artists"]?["items"] is JsonArray artists)
            foreach (var a in artists.Take(3))
            {
                var name = a?["name"]?.GetValue<string>();
                if (string.IsNullOrEmpty(name)) continue;
                list.Add(new SpotifySuggestion("Artist", name, "Artist", ImageUrl(a, 64), name));
            }
        if (r.Json["tracks"]?["items"] is JsonArray tracks)
            foreach (var t in tracks.Take(4))
                if (ParseTrack(t, null, DateTime.Now) is { } d)
                {
                    var song = new SpotifySearchSong(ToTrack(d), ImageUrl(t?["album"], 64));
                    list.Add(new SpotifySuggestion("Song", d.Title, d.Artist, song.ImageUrl, song));
                }
        if (r.Json["albums"]?["items"] is JsonArray albums)
            foreach (var a in albums.Take(3))
            {
                var uri = a?["uri"]?.GetValue<string>();
                if (uri == null) continue;
                var release = a!["release_date"]?.GetValue<string>() ?? "";
                var artistNames = a["artists"] is JsonArray ar
                    ? string.Join(", ", ar.Select(x => x?["name"]?.GetValue<string>()).Where(x => !string.IsNullOrEmpty(x)))
                    : "";
                var album = new SpotifySearchAlbum(uri, a["name"]?.GetValue<string>() ?? "", artistNames,
                    release.Length >= 4 && int.TryParse(release[..4], out var y) ? y : 0,
                    a["total_tracks"]?.GetValue<int>() ?? 0, ImageUrl(a, 300), ImageUrl(a, 640));
                list.Add(new SpotifySuggestion("Album", album.Name, artistNames, ImageUrl(a, 64), album));
            }
        return list;
    }

    /// <summary>The smallest image of an album that is at least <paramref name="minWidth"/> wide (or the largest).</summary>
    private static string? ImageUrl(JsonNode? album, int minWidth)
    {
        if (album?["images"] is not JsonArray imgs || imgs.Count == 0) return null;
        var sized = imgs.Select(i => (Url: i?["url"]?.GetValue<string>(), W: i?["width"]?.GetValue<int>() ?? 0))
                        .Where(i => i.Url != null).OrderBy(i => i.W).ToList();
        return (sized.FirstOrDefault(i => i.W >= minWidth).Url ?? sized.LastOrDefault().Url);
    }

    /// <summary>
    /// Every track of a Spotify album, for "Show full album" in the album spotlight. Not added to the library.
    /// Empty when Spotify can't be reached.
    /// </summary>
    public async Task<List<Track>> GetAlbumTracksAsync(string albumUri)
    {
        var list = new List<Track>();
        var id = albumUri.Split(':').Last();
        var r = await GetAsync($"/albums/{id}");
        if (!r.Ok || r.Json == null) return list;
        var album = r.Json;
        void Add(JsonNode? t) { if (ParseTrack(t, album, DateTime.Now) is { } d) list.Add(ToTrack(d)); }
        if (album["tracks"]?["items"] is JsonArray first) foreach (var t in first) Add(t);
        var next = album["tracks"]?["next"]?.GetValue<string>();
        if (next != null) await foreach (var t in PagesAsync(next)) Add(t);
        return list;
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
