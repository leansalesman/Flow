using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Flow.Services;

public enum RepeatMode { Off, All, One }
public enum ReplayGainMode { Off, Track, Album }
/// <summary>How Spotify songs play: through the Spotify app (default) or Flow's built-in librespot engine.</summary>
public enum SpotifyEngine { SpotifyApp, BuiltIn }

public sealed class AppSettings
{
    public List<string> MusicFolders { get; set; } = new();

    public double Volume { get; set; } = 0.8;
    public bool Muted { get; set; }
    public bool Shuffle { get; set; }
    public RepeatMode Repeat { get; set; } = RepeatMode.Off;

    public bool EqEnabled { get; set; }
    public string EqPreset { get; set; } = "Flat";
    public double[] EqGains { get; set; } = new double[10];

    public double CrossfadeSeconds { get; set; }
    public bool Gapless { get; set; } = true;
    public ReplayGainMode ReplayGain { get; set; } = ReplayGainMode.Off;

    public bool MinimizeToTray { get; set; }
    public bool ResumeOnStart { get; set; } = true;
    public bool FadeChromeWhilePlaying { get; set; } = true;
    /// <summary>Appearance: "Minimal" (translucent), "Dark", "Light", "Auto", "Metal" (Brushed Metal) or "DarkMetal" (Dark Brushed Metal).</summary>
    public string Theme { get; set; } = "Minimal";
    /// <summary>Check GitHub for a newer Flow once a day (Settings, About).</summary>
    public bool AutoCheckUpdates { get; set; } = true;
    public DateTime? LastUpdateCheck { get; set; }

    public List<string> LastQueue { get; set; } = new();
    /// <summary>Spotify songs in the last queue that aren't in the library (from Search / full album), so they can be restored.</summary>
    public List<Flow.Spotify.SpotifyTrackDto> LastQueueSpotify { get; set; } = new();
    public int LastIndex { get; set; } = -1;
    public double LastPosition { get; set; }

    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 1180;
    public double WindowHeight { get; set; } = 760;
    public bool WindowMaximized { get; set; }

    public string LibraryView { get; set; } = "Grid";
    public string SortField { get; set; } = "ArtistName";
    public bool SortDescending { get; set; }
    public string LibrarySource { get; set; } = "All";
    public string VisualizerStyle { get; set; } = "MirroredBlocks";
    /// <summary>Cover artwork size: "Small", "Medium" or "Large".</summary>
    public string LibraryArtSize { get; set; } = "Medium";
    public string PlaylistArtSize { get; set; } = "Medium";
    /// <summary>Album spotlight / artist page text size: "Small" (original), "Medium" (default) or "Large".</summary>
    public string SpotlightTextSize { get; set; } = "Medium";
    /// <summary>Playlists view text size: "Small" (original), "Medium" (default) or "Large".</summary>
    public string PlaylistTextSize { get; set; } = "Medium";

    // Spotify
    public string SpotifyClientId { get; set; } = "";
    public bool SpotifyImportLiked { get; set; } = true;
    public bool SpotifyImportAlbums { get; set; } = true;
    public bool SpotifyImportPlaylists { get; set; } = true;
    public DateTime? SpotifyLastSync { get; set; }

    // Built-in Spotify playback (librespot, experimental)
    public SpotifyEngine SpotifyEngine { get; set; } = SpotifyEngine.SpotifyApp;
    public string LibrespotDeviceName { get; set; } = "Flow";
    public int LibrespotBitrate { get; set; } = 320;            // 96 | 160 | 320
    public bool LibrespotNormalisation { get; set; }
    public bool LibrespotStartWithFlow { get; set; }            // false = start on the first Spotify play
}

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private readonly string _path;
    public AppSettings Current { get; private set; } = new();
    public string DataDir { get; }

    public SettingsService()
    {
        DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flow");
        Directory.CreateDirectory(DataDir);
        _path = Path.Combine(DataDir, "settings.json");
    }

    public void Load()
    {
        try
        {
            if (File.Exists(_path))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Json) ?? new AppSettings();
        }
        catch { Current = new AppSettings(); }
        if (Current.EqGains is not { Length: 10 }) Current.EqGains = new double[10];
    }

    public void Save()
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Json));
            File.Move(tmp, _path, true);
        }
        catch (Exception ex) { App.Log(ex); }   // never lose the saved place silently
    }
}
