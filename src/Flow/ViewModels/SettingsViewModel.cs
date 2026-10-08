using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Input;
using Flow.Audio;
using Flow.Infrastructure;
using Flow.Interop;
using Flow.Library;
using Flow.Services;
using Microsoft.Win32;

namespace Flow.ViewModels;

public sealed class EqBand : ObservableObject
{
    private readonly Action<int, double> _changed;
    public EqBand(int index, double gain, Action<int, double> changed)
    {
        Index = index;
        _gain = gain;
        _changed = changed;
        var f = Equalizer.Frequencies[index];
        Label = f >= 1000 ? $"{f / 1000:0.#}k" : $"{f:0}";
    }

    public int Index { get; }
    public string Label { get; }

    private double _gain;
    public double Gain
    {
        get => _gain;
        set
        {
            value = Math.Round(Math.Clamp(value, -12, 12) * 2) / 2;
            if (Set(ref _gain, value)) _changed(Index, value);
        }
    }

    public void SetSilently(double g) { _gain = g; OnPropertyChanged(nameof(Gain)); }
}

public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly LibraryService _lib;
    private readonly PlaybackService _pb;
    private readonly AudioEngine _engine;
    private readonly Action<string> _toast;
    private bool _applyingPreset;

    /// <summary>Appearance (Settings → Appearance): applied live and remembered.</summary>
    public string Theme
    {
        get => ThemeService.Parse(_settings.Current.Theme).ToString();
        set
        {
            var mode = ThemeService.Parse(value);
            if (mode.ToString() == Theme) return;
            _settings.Current.Theme = mode.ToString();
            _settings.Save();
            (System.Windows.Application.Current as App)?.Theme?.SetMode(mode);
            OnPropertyChanged();
        }
    }

    public SettingsViewModel(SettingsService settings, LibraryService lib, PlaybackService pb, Flow.Spotify.SpotifyService spotify, Action<string> toast)
    {
        Spotify = spotify;
        ConnectSpotifyCommand = new RelayCommand(async () =>
        {
            settings.Save();
            if (await Spotify.ConnectAsync())
            {
                toast("Connected to Spotify — importing your library…");
                await Spotify.SyncAsync();
            }
        }, () => !Spotify.IsBusy);
        SyncSpotifyCommand = new RelayCommand(async () => await Spotify.SyncAsync(), () => Spotify.IsConnected && !Spotify.IsBusy);
        DisconnectSpotifyCommand = new RelayCommand(() =>
        {
            if (Views.FlowDialog.Confirm("Disconnect Spotify?",
                    "Your Spotify songs and playlists will be removed from Flow. Nothing changes in your Spotify account.", "Disconnect"))
                Spotify.Disconnect();
        }, () => Spotify.IsConnected);
        OpenSpotifyDashboardCommand = new RelayCommand(() => { try { Flow.Spotify.SpotifyService.OpenDashboard(); } catch { } });
        Librespot = (System.Windows.Application.Current as App)?.Librespot;
        SetUpLibrespotCommand = new RelayCommand(async () =>
        {
            if (Librespot == null) return;
            var err = await Librespot.SetUpAsync();
            toast(err ?? "Built-in Spotify playback is ready");
        }, () => Librespot != null && LibrespotAvailable && Librespot.State != Flow.Spotify.LibrespotState.SigningIn);
        SignOutLibrespotCommand = new RelayCommand(() =>
        {
            if (Librespot == null) return;
            if (Views.FlowDialog.Confirm("Sign out of built-in playback?",
                    "Flow stops its Spotify playback engine and deletes its saved sign-in. Your Spotify library in Flow stays.", "Sign out"))
                Librespot.SignOut();
        }, () => Librespot?.HasCredentials == true);
        OpenLibrespotSignInCommand = new RelayCommand(() =>
        {
            var url = Librespot?.SignInUrl;
            if (url != null) try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        });
        CopyRedirectCommand = new RelayCommand(() =>
        {
            try { System.Windows.Clipboard.SetText(SpotifyRedirectUri); toast("Redirect URI copied"); } catch { }
        });
        _settings = settings;
        _lib = lib;
        _pb = pb;
        _engine = pb.Engine;
        _toast = toast;
        var s = settings.Current;

        foreach (var f in s.MusicFolders) Folders.Add(f);
        for (int i = 0; i < 10; i++) EqBands.Add(new EqBand(i, s.EqGains[i], OnBandChanged));

        _engine.Equalizer.SetGains(s.EqGains);
        _engine.Equalizer.Enabled = s.EqEnabled;
        _engine.CrossfadeSeconds = s.CrossfadeSeconds;
        _engine.Gapless = s.Gapless;

        AddFolderCommand = new RelayCommand(AddFolder);
        RemoveFolderCommand = new RelayCommand(p => { if (p is string f) RemoveFolder(f); });
        RescanCommand = new RelayCommand(() => { _ = _lib.ScanAsync(); _toast("Rescanning your library…"); });
        ApplyPresetCommand = new RelayCommand(p => { if (p is string name) ApplyPreset(name); });
        ToggleOpenWithCommand = new RelayCommand(ToggleOpenWith);
        OpenDataFolderCommand = new RelayCommand(() =>
        {
            try { System.Diagnostics.Process.Start("explorer.exe", $"\"{_settings.DataDir}\""); } catch { }
        });
        _isOpenWithRegistered = SafeIsRegistered();
    }

    public ICommand AddFolderCommand { get; }
    public ICommand RemoveFolderCommand { get; }
    public ICommand RescanCommand { get; }
    public ICommand ApplyPresetCommand { get; }
    public ICommand ToggleOpenWithCommand { get; }
    public ICommand OpenDataFolderCommand { get; }

    public ObservableCollection<string> Folders { get; } = new();
    public ObservableCollection<EqBand> EqBands { get; } = new();
    public IEnumerable<string> Presets => Equalizer.Presets.Keys;

    // ---- Updates (About) ----

    private FlowUpdate? _update;
    private bool _checkingUpdate, _downloadingUpdate;

    public ICommand CheckUpdatesCommand => _checkUpdatesCommand ??= new RelayCommand(async () => await CheckUpdatesAsync(),
        () => !_checkingUpdate && !_downloadingUpdate);
    public ICommand InstallUpdateCommand => _installUpdateCommand ??= new RelayCommand(async () => await InstallUpdateAsync(),
        () => _update != null && !_downloadingUpdate);
    public ICommand OpenReleaseNotesCommand => _openNotesCommand ??= new RelayCommand(() =>
    {
        var url = _update?.NotesUrl ?? "https://github.com/leansalesman/Flow/releases";
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    });
    private ICommand? _checkUpdatesCommand, _installUpdateCommand, _openNotesCommand;

    public bool IsUpdateAvailable => _update != null;

    private string _updateStatus = "";
    public string UpdateStatus { get => _updateStatus; private set => Set(ref _updateStatus, value); }

    private double _updateProgress;
    public double UpdateProgress { get => _updateProgress; private set => Set(ref _updateProgress, value); }
    public bool IsDownloadingUpdate => _downloadingUpdate;

    public bool AutoCheckUpdates
    {
        get => _settings.Current.AutoCheckUpdates;
        set { _settings.Current.AutoCheckUpdates = value; _settings.Save(); OnPropertyChanged(); }
    }

    /// <summary>Called by the daily check when it finds a newer Flow.</summary>
    public void ShowAvailableUpdate(FlowUpdate update)
    {
        _update = update;
        UpdateStatus = $"Flow {update.Version} is available (you have {UpdateService.CurrentVersion}).";
        OnPropertyChanged(nameof(IsUpdateAvailable));
    }

    private async Task CheckUpdatesAsync()
    {
        var app = System.Windows.Application.Current as App;
        if (app == null) return;
        _checkingUpdate = true;
        UpdateStatus = "Checking for updates…";
        try
        {
            var update = await app.Updates.CheckAsync();
            _settings.Current.LastUpdateCheck = DateTime.Now;
            _settings.Save();
            if (update == null)
            {
                _update = null;
                OnPropertyChanged(nameof(IsUpdateAvailable));
                UpdateStatus = $"You're up to date (Flow {UpdateService.CurrentVersion}).";
            }
            else ShowAvailableUpdate(update);
        }
        catch (Exception ex)
        {
            DiagLog.Write("Update check failed: " + ex.Message);
            UpdateStatus = "Couldn't reach GitHub to check for updates. Try again later.";
        }
        finally { _checkingUpdate = false; System.Windows.Input.CommandManager.InvalidateRequerySuggested(); }
    }

    /// <summary>Downloads and verifies the setup, starts it silently, then closes Flow; the setup reopens Flow.</summary>
    private async Task InstallUpdateAsync()
    {
        var app = System.Windows.Application.Current as App;
        if (_update == null || app == null) return;
        if (string.IsNullOrEmpty(_update.SetupUrl)) { OpenReleaseNotesCommand.Execute(null); return; }
        _downloadingUpdate = true;
        OnPropertyChanged(nameof(IsDownloadingUpdate));
        UpdateStatus = $"Downloading Flow {_update.Version}…";
        try
        {
            var progress = new Progress<double>(p => UpdateProgress = p);
            var setup = await app.Updates.DownloadAsync(_update, progress);
            UpdateStatus = "Installing… Flow will reopen in a moment.";
            UpdateService.StartInstall(setup, resume: _pb.IsPlaying);
            await Task.Delay(400);
            (System.Windows.Application.Current.MainWindow as MainWindow)?.ExitApp();
        }
        catch (Exception ex)
        {
            DiagLog.Write("Update failed: " + ex.Message);
            UpdateStatus = "Update failed: " + ex.Message;
            _downloadingUpdate = false;
            OnPropertyChanged(nameof(IsDownloadingUpdate));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    public string Version =>
        "Flow " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0") + "  ·  Windows 11 x64";

    /// <summary>"Created by …" line in About, read from the assembly's Authors/Company metadata.</summary>
    public string Author =>
        "Created by " + (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? "Joseph Martinez");

    public string Copyright =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "";

    public void AddFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Add a music folder", Multiselect = true };
        if (dlg.ShowDialog() != true) return;
        AddFolders(dlg.FolderNames);
    }

    public void AddFolders(IEnumerable<string> folders)
    {
        bool added = false;
        foreach (var f in folders)
        {
            if (_settings.Current.MusicFolders.Contains(f, StringComparer.OrdinalIgnoreCase)) continue;
            _settings.Current.MusicFolders.Add(f);
            Folders.Add(f);
            added = true;
        }
        if (!added) return;
        _settings.Save();
        _lib.RefreshWatchers();
        _ = _lib.ScanAsync();
        _toast("Scanning your music…");
    }

    private void RemoveFolder(string f)
    {
        _settings.Current.MusicFolders.RemoveAll(x => string.Equals(x, f, StringComparison.OrdinalIgnoreCase));
        Folders.Remove(f);
        _settings.Save();
        _lib.RefreshWatchers();
        _ = _lib.ScanAsync();
    }

    // ---- Equalizer ----

    public bool EqEnabled
    {
        get => _settings.Current.EqEnabled;
        set
        {
            _settings.Current.EqEnabled = value;
            _engine.Equalizer.Enabled = value;
            OnPropertyChanged();
        }
    }

    public string EqPreset
    {
        get => _settings.Current.EqPreset;
        private set { _settings.Current.EqPreset = value; OnPropertyChanged(); }
    }

    private void OnBandChanged(int index, double gain)
    {
        _settings.Current.EqGains[index] = gain;
        _engine.Equalizer.SetGain(index, gain);
        if (!_applyingPreset)
        {
            EqPreset = "Custom";
            if (!EqEnabled) EqEnabled = true;
        }
    }

    private void ApplyPreset(string name)
    {
        if (!Equalizer.Presets.TryGetValue(name, out var gains)) return;
        _applyingPreset = true;
        for (int i = 0; i < 10; i++)
        {
            _settings.Current.EqGains[i] = gains[i];
            EqBands[i].SetSilently(gains[i]);
        }
        _engine.Equalizer.SetGains(gains);
        _applyingPreset = false;
        EqPreset = name;
        if (!EqEnabled && name != "Flat") EqEnabled = true;
    }

    // ---- Playback ----

    public double CrossfadeSeconds
    {
        get => _settings.Current.CrossfadeSeconds;
        set
        {
            value = Math.Round(Math.Clamp(value, 0, 12));
            _settings.Current.CrossfadeSeconds = value;
            _engine.CrossfadeSeconds = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CrossfadeText));
        }
    }

    public string CrossfadeText => CrossfadeSeconds <= 0 ? "Off" : $"{CrossfadeSeconds:0} s";

    public bool Gapless
    {
        get => _settings.Current.Gapless;
        set { _settings.Current.Gapless = value; _engine.Gapless = value; OnPropertyChanged(); }
    }

    public ReplayGainMode ReplayGain
    {
        get => _settings.Current.ReplayGain;
        set { _settings.Current.ReplayGain = value; OnPropertyChanged(); _pb.RefreshReplayGain(); }
    }

    // ---- General ----

    public bool MinimizeToTray
    {
        get => _settings.Current.MinimizeToTray;
        set { _settings.Current.MinimizeToTray = value; OnPropertyChanged(); }
    }

    public bool ResumeOnStart
    {
        get => _settings.Current.ResumeOnStart;
        set { _settings.Current.ResumeOnStart = value; OnPropertyChanged(); }
    }

    public bool FadeChromeWhilePlaying
    {
        get => _settings.Current.FadeChromeWhilePlaying;
        set { _settings.Current.FadeChromeWhilePlaying = value; OnPropertyChanged(); }
    }

    private bool _isOpenWithRegistered;
    public bool IsOpenWithRegistered { get => _isOpenWithRegistered; private set => Set(ref _isOpenWithRegistered, value); }

    private static bool SafeIsRegistered()
    {
        try { return FileAssociations.IsRegistered(); } catch { return false; }
    }

    private void ToggleOpenWith()
    {
        try
        {
            if (IsOpenWithRegistered)
            {
                FileAssociations.Unregister();
                _toast("Flow removed from the “Open with” menu");
            }
            else
            {
                FileAssociations.Register();
                _toast("Flow added to the “Open with” menu for audio files");
            }
        }
        catch (Exception ex) { _toast("Couldn't update file associations: " + ex.Message); }
        IsOpenWithRegistered = SafeIsRegistered();
    }

    // ---- Spotify ----

    public Flow.Spotify.SpotifyService Spotify { get; }
    public ICommand ConnectSpotifyCommand { get; }
    public ICommand SyncSpotifyCommand { get; }
    public ICommand DisconnectSpotifyCommand { get; }
    public ICommand OpenSpotifyDashboardCommand { get; }
    public ICommand CopyRedirectCommand { get; }

    public string SpotifyRedirectUri => Flow.Spotify.SpotifyService.RedirectUri;

    // ---- Built-in Spotify playback (librespot, experimental) ----

    public Flow.Spotify.LibrespotHost? Librespot { get; }
    public bool LibrespotAvailable => Flow.Spotify.LibrespotHost.IsAvailable;
    public ICommand SetUpLibrespotCommand { get; }
    public ICommand SignOutLibrespotCommand { get; }
    public ICommand OpenLibrespotSignInCommand { get; }

    /// <summary>"SpotifyApp" or "BuiltIn". Switching pauses Spotify; the next play uses the new engine.</summary>
    public string SpotifyEngine
    {
        get => _settings.Current.SpotifyEngine.ToString();
        set
        {
            var engine = Enum.TryParse<Flow.Services.SpotifyEngine>(value, out var e) ? e : Flow.Services.SpotifyEngine.SpotifyApp;
            if (engine == Flow.Services.SpotifyEngine.BuiltIn && !LibrespotAvailable) engine = Flow.Services.SpotifyEngine.SpotifyApp;
            if (engine == _settings.Current.SpotifyEngine) { OnPropertyChanged(); return; }
            _pb.SwitchSpotifyEngine(engine);
            _settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsBuiltInEngine));
        }
    }

    public bool IsBuiltInEngine => _settings.Current.SpotifyEngine == Flow.Services.SpotifyEngine.BuiltIn;

    public string LibrespotDeviceName
    {
        get => _settings.Current.LibrespotDeviceName;
        set
        {
            var name = string.IsNullOrWhiteSpace(value) ? "Flow" : value.Trim();
            if (name == _settings.Current.LibrespotDeviceName) return;
            _settings.Current.LibrespotDeviceName = name;
            _settings.Save();
            Librespot?.ApplySettings();
            OnPropertyChanged();
        }
    }

    public string LibrespotBitrate
    {
        get => _settings.Current.LibrespotBitrate.ToString();
        set
        {
            int b = int.TryParse(value, out var v) && v is 96 or 160 or 320 ? v : 320;
            if (b == _settings.Current.LibrespotBitrate) return;
            _settings.Current.LibrespotBitrate = b;
            _settings.Save();
            Librespot?.ApplySettings();
            OnPropertyChanged();
        }
    }

    public bool LibrespotNormalisation
    {
        get => _settings.Current.LibrespotNormalisation;
        set
        {
            if (value == _settings.Current.LibrespotNormalisation) return;
            _settings.Current.LibrespotNormalisation = value;
            _settings.Save();
            Librespot?.ApplySettings();
            OnPropertyChanged();
        }
    }

    public bool LibrespotStartWithFlow
    {
        get => _settings.Current.LibrespotStartWithFlow;
        set { _settings.Current.LibrespotStartWithFlow = value; _settings.Save(); OnPropertyChanged(); }
    }

    public string SpotifyClientId
    {
        get => _settings.Current.SpotifyClientId;
        set { _settings.Current.SpotifyClientId = (value ?? "").Trim(); OnPropertyChanged(); }
    }

    public bool SpotifyImportLiked
    {
        get => _settings.Current.SpotifyImportLiked;
        set { _settings.Current.SpotifyImportLiked = value; OnPropertyChanged(); }
    }

    public bool SpotifyImportAlbums
    {
        get => _settings.Current.SpotifyImportAlbums;
        set { _settings.Current.SpotifyImportAlbums = value; OnPropertyChanged(); }
    }

    public bool SpotifyImportPlaylists
    {
        get => _settings.Current.SpotifyImportPlaylists;
        set { _settings.Current.SpotifyImportPlaylists = value; OnPropertyChanged(); }
    }

    public string SupportedFormats => string.Join("  ", AudioFormats.Extensions.Select(e => e.TrimStart('.').ToUpperInvariant()).Distinct());
}
