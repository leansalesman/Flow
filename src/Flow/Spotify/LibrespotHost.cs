using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Flow.Audio;
using Flow.Infrastructure;
using Flow.Interop;
using Flow.Services;

namespace Flow.Spotify;

public enum LibrespotState { Missing, NotSetUp, SigningIn, Stopped, Starting, Ready, Error }

/// <summary>
/// Runs librespot (built-in Spotify playback, experimental) as a hidden background process. librespot signs in
/// to the user's Spotify Premium account, shows up in Spotify Connect under <see cref="AppSettings.LibrespotDeviceName"/>,
/// and writes decoded audio (44.1 kHz stereo float) to stdout, which feeds <see cref="Input"/> and Flow's own
/// audio engine. Live playback only: audio is never written to disk.
/// </summary>
public sealed class LibrespotHost : ObservableObject, IDisposable
{
    public const int SampleRate = 44100;   // librespot's fixed output rate (playback/src/lib.rs)
    public const int Channels = 2;
    private const int OAuthPort = 5588;

    private readonly SettingsService _settings;
    private readonly Dispatcher _ui;
    private readonly string _systemCache, _audioCache, _logPath;
    private readonly object _logLock = new();
    private IntPtr _job;
    private Process? _proc;
    private bool _stopping;
    private readonly List<DateTime> _failures = new();
    private int _restartDelay = 2;
    private CancellationTokenSource? _restartCts;
    private string? _lastError;

    public LibrespotHost(SettingsService settings, Dispatcher ui)
    {
        _settings = settings;
        _ui = ui;
        var root = Path.Combine(settings.DataDir, "librespot");
        _systemCache = Path.Combine(root, "system");
        _audioCache = Path.Combine(root, "cache");
        _logPath = Path.Combine(settings.DataDir, "librespot.log");
        Input = new LiveInput(SampleRate, Channels);
        RefreshIdleState();
    }

    public static string ExePath => Path.Combine(AppContext.BaseDirectory, "librespot.exe");
    public static bool IsAvailable => File.Exists(ExePath);
    public bool HasCredentials => File.Exists(Path.Combine(_systemCache, "credentials.json"));

    /// <summary>The decoded audio stream from librespot, ready to hand to the audio engine.</summary>
    public LiveInput Input { get; }

    /// <summary>The sign-in page librespot asked to open (in case the browser didn't open by itself).</summary>
    public string? SignInUrl { get; private set; }

    private LibrespotState _state;
    public LibrespotState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value)) return;
            OnPropertyChanged(nameof(IsReady));
            OnPropertyChanged(nameof(StatusText));
            StatusChanged?.Invoke(StatusText);
        }
    }

    public bool IsReady => State == LibrespotState.Ready;

    /// <summary>The running librespot process (changes when it restarts), or 0.</summary>
    public int ProcessId { get { try { return _proc?.Id ?? 0; } catch { return 0; } } }
    public event Action<string>? StatusChanged;
    /// <summary>librespot is sending audio that nothing is playing (another device started playback on "Flow").</summary>
    public event Action? RemoteAudio;

    public string StatusText => State switch
    {
        LibrespotState.Missing => "librespot.exe not found next to Flow.exe",
        LibrespotState.NotSetUp => "Not set up",
        LibrespotState.SigningIn => "Signing in… approve Flow in the browser window",
        LibrespotState.Stopped => "Stopped (starts when you play a Spotify song)",
        LibrespotState.Starting => "Starting…",
        LibrespotState.Ready => $"Ready as \"{DeviceName}\"",
        _ => "Error: " + (_lastError ?? "librespot stopped"),
    };

    private string DeviceName => string.IsNullOrWhiteSpace(_settings.Current.LibrespotDeviceName) ? "Flow" : _settings.Current.LibrespotDeviceName.Trim();

    private void RefreshIdleState()
    {
        if (_proc != null) return;
        State = !IsAvailable ? LibrespotState.Missing : !HasCredentials ? LibrespotState.NotSetUp : LibrespotState.Stopped;
    }

    // ---- One-time sign-in ------------------------------------------------------------------------

    /// <summary>
    /// Runs librespot's OAuth sign-in: it opens the Spotify approval page in the browser and saves its
    /// credentials in Flow's librespot folder. Returns null on success or an error message.
    /// </summary>
    public async Task<string?> SetUpAsync()
    {
        if (!IsAvailable) { RefreshIdleState(); return StatusText; }
        Stop();
        Directory.CreateDirectory(_systemCache);
        SignInUrl = null;
        _lastError = null;
        State = LibrespotState.SigningIn;
        Log("Sign-in started");

        var psi = BaseStartInfo();
        foreach (var a in new[] { "--enable-oauth", "--oauth-port", OAuthPort.ToString(),
                                  "--system-cache", _systemCache, "--disable-audio-cache",
                                  "--name", DeviceName, "--device-type", "computer",
                                  "--backend", "pipe", "--disable-discovery" })
            psi.ArgumentList.Add(a);

        Process p;
        try { p = Process.Start(psi)!; }
        catch (Exception ex) { return Fail("could not start librespot (" + ex.Message + ")"); }
        AddToJob(p);
        string? lastStderr = null;
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) { lastStderr = e.Data; Log(e.Data); } };
        p.BeginErrorReadLine();
        // stdout carries text during sign-in ("Browse to: <url>"); no audio plays here.
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            int i = e.Data.IndexOf("https://accounts.spotify.com/authorize", StringComparison.Ordinal);
            if (i >= 0) { SignInUrl = e.Data[i..].Trim(); Log("Sign-in page opened in the browser"); }
        };
        p.BeginOutputReadLine();

        var deadline = DateTime.UtcNow.AddMinutes(4);
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500);
                if (HasCredentials)
                {
                    Log("Signed in; credentials saved");
                    KillQuietly(p);
                    await Task.Delay(300);
                    State = LibrespotState.Stopped;
                    if (_settings.Current.LibrespotStartWithFlow) _ = EnsureRunningAsync();
                    return null;
                }
                if (p.HasExited)
                {
                    var why = lastStderr ?? $"librespot exited ({p.ExitCode})";
                    if (why.Contains("in use", StringComparison.OrdinalIgnoreCase) || why.Contains("10048"))
                        why = $"port {OAuthPort} is in use by another program; close it and try again";
                    return Fail("sign-in failed: " + Clean(why));
                }
            }
            KillQuietly(p);
            return Fail("sign-in timed out (no approval within 4 minutes)");
        }
        finally { p.Dispose(); }
    }

    /// <summary>Stops librespot and deletes its saved sign-in.</summary>
    public void SignOut()
    {
        Stop();
        try { if (Directory.Exists(_systemCache)) Directory.Delete(_systemCache, true); }
        catch (Exception ex) { App.Log(ex); }
        Log("Signed out; credentials deleted");
        _lastError = null;
        RefreshIdleState();
    }

    // ---- Run ------------------------------------------------------------------------------------

    /// <summary>Starts librespot if needed and waits until it is up. False if it can't run.</summary>
    public async Task<bool> EnsureRunningAsync()
    {
        if (!IsAvailable || !HasCredentials) { RefreshIdleState(); return false; }
        if (_proc == null)
        {
            _failures.Clear();      // a play (or Set up) clears the give-up state
            _restartDelay = 2;
            if (!Start()) return false;
        }
        // librespot exits within a moment when it can't sign in; consider it ready once it has stayed up.
        for (int i = 0; i < 40 && State == LibrespotState.Starting; i++) await Task.Delay(100);
        return State == LibrespotState.Ready;
    }

    private bool Start()
    {
        _restartCts?.Cancel();
        _stopping = false;
        Directory.CreateDirectory(_audioCache);
        var s = _settings.Current;
        int bitrate = s.LibrespotBitrate is 96 or 160 or 320 ? s.LibrespotBitrate : 320;

        var psi = BaseStartInfo();
        var args = new List<string>
        {
            "--name", DeviceName, "--device-type", "computer",
            "--backend", "pipe", "--format", "F32", "--bitrate", bitrate.ToString(),
            "--system-cache", _systemCache, "--cache", _audioCache, "--cache-size-limit", "1G",
            "--initial-volume", "100", "--volume-ctrl", "fixed",
            "--disable-discovery", "--quiet",
        };
        if (s.LibrespotNormalisation) args.Add("--enable-volume-normalisation");
        foreach (var a in args) psi.ArgumentList.Add(a);

        Process p;
        try { p = Process.Start(psi)!; }
        catch (Exception ex) { Fail("could not start librespot (" + ex.Message + ")"); return false; }
        AddToJob(p);
        _proc = p;
        State = LibrespotState.Starting;
        Log($"Started (pid {p.Id}, {bitrate} kbps{(s.LibrespotNormalisation ? ", normalisation" : "")})");

        p.EnableRaisingEvents = true;
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) { _lastError = Clean(e.Data); Log(e.Data); } };
        p.BeginErrorReadLine();
        p.Exited += (_, _) => _ui.BeginInvoke(() => OnExited(p));

        // Raw PCM on stdout, read on a dedicated thread (never the thread pool, never the audio callback).
        // A fresh stream: the previous reader finishes first (one writer at a time), then nothing left over from the
        // previous librespot reaches the new one (see LiveInput.ResetStream).
        var previous = _reader;
        var reader = new Thread(() =>
        {
            previous?.Join(2000);
            Input.ResetStream();
            ReadAudio(p);
        }) { IsBackground = true, Name = "librespot audio", Priority = ThreadPriority.AboveNormal };
        _reader = reader;
        reader.Start();

        _ = MarkReadyAsync(p);
        return true;
    }

    private async Task MarkReadyAsync(Process p)
    {
        await Task.Delay(2500);
        if (_proc == p && !p.HasExited && State == LibrespotState.Starting)
        {
            _lastError = null;
            State = LibrespotState.Ready;
        }
    }

    private void ReadAudio(Process p)
    {
        var buffer = new byte[16384];
        try
        {
            var stream = p.StandardOutput.BaseStream;
            int n;
            var lastNotice = DateTime.MinValue;
            while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                // Audio while Flow isn't listening: something else (e.g. a phone) is playing on the Flow device.
                if (!Input.Attached && (DateTime.UtcNow - lastNotice).TotalSeconds > 2)
                {
                    lastNotice = DateTime.UtcNow;
                    _ui.BeginInvoke(() => RemoteAudio?.Invoke());
                }
                // Only the current process may write (a stopped one can still have bytes in flight).
                if (!ReferenceEquals(_proc, p)) break;
                Input.Write(buffer, n);
                Interlocked.Exchange(ref _lastAudioTicks, DateTime.UtcNow.Ticks);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private void OnExited(Process p)
    {
        if (_proc != p) return;
        int code = -1;
        try { code = p.ExitCode; } catch { }
        _proc = null;
        p.Dispose();
        if (_stopping) { RefreshIdleState(); return; }

        Log($"Exited unexpectedly (code {code})");
        var now = DateTime.UtcNow;
        _failures.Add(now);
        _failures.RemoveAll(t => now - t > TimeSpan.FromMinutes(10));
        if (_failures.Count >= 5)
        {
            Fail((_lastError ?? "librespot keeps stopping") + "; press Set up or play a Spotify song to try again");
            return;
        }
        int delay = _restartDelay;
        _restartDelay = Math.Min(60, _restartDelay * 2);
        _lastError ??= "librespot stopped";
        State = LibrespotState.Error;
        StatusChanged?.Invoke($"Spotify playback engine stopped; restarting in {delay} s");
        var cts = _restartCts = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromSeconds(delay), cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) _ui.BeginInvoke(() => { if (_proc == null && !_stopping && HasCredentials) Start(); });
        });
    }

    private DispatcherTimer? _pendingApply;
    private long _lastAudioTicks;

    /// <summary>librespot is sending audio right now (it goes quiet when paused or stopped).</summary>
    private bool IsStreaming => Input.Attached &&
        DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastAudioTicks) < TimeSpan.FromSeconds(2).Ticks;
    private Thread? _reader;

    /// <summary>
    /// Restarts a running librespot so changed settings (name, bitrate, normalisation) take effect. Never in the
    /// middle of a song: while librespot is streaming the restart waits until playback pauses or stops (pressing
    /// play afterwards starts the song again at the same spot).
    /// </summary>
    public void ApplySettings()
    {
        if (_proc == null) { RefreshIdleState(); return; }
        if (IsStreaming)
        {
            if (_pendingApply != null) return;
            Log("Settings changed; restarting when playback pauses");
            _pendingApply = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = TimeSpan.FromSeconds(2) };
            _pendingApply.Tick += (_, _) =>
            {
                if (IsStreaming) return;
                _pendingApply?.Stop();
                _pendingApply = null;
                if (_proc != null) { Stop(); Start(); }
            };
            _pendingApply.Start();
            return;
        }
        Stop();
        Start();
    }

    public void Stop()
    {
        _restartCts?.Cancel();
        _stopping = true;
        var p = _proc;
        _proc = null;
        if (p != null)
        {
            KillQuietly(p);
            p.Dispose();
            Log("Stopped");
        }
        Input.Flush();
        RefreshIdleState();
    }

    public void Dispose()
    {
        Stop();
        if (_job != IntPtr.Zero) { CloseHandle(_job); _job = IntPtr.Zero; }
    }

    // ---- Helpers --------------------------------------------------------------------------------

    private static ProcessStartInfo BaseStartInfo() => new(ExePath)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        WorkingDirectory = AppContext.BaseDirectory,
    };

    private string Fail(string message)
    {
        _lastError = message;
        Log("Error: " + message);
        State = LibrespotState.Error;
        return "Built-in Spotify playback: " + message;
    }

    /// <summary>Puts the process in a job that kills it when Flow's handle closes (including crashes).</summary>
    private void AddToJob(Process p)
    {
        try
        {
            if (_job == IntPtr.Zero)
            {
                _job = NativeMethods.CreateJobObject(IntPtr.Zero, null);
                var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                NativeMethods.SetInformationJobObject(_job, NativeMethods.JobObjectExtendedLimitInformation, ref info,
                    Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
            }
            if (!NativeMethods.AssignProcessToJobObject(_job, p.Handle))
                Log($"Could not attach to job object (error {Marshal.GetLastWin32Error()})");
        }
        catch (Exception ex) { App.Log(ex); }
    }

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private static void KillQuietly(Process p)
    {
        try { if (!p.HasExited) { p.Kill(entireProcessTree: true); p.WaitForExit(2000); } } catch { }
    }

    /// <summary>librespot log lines carry a timestamp/level prefix; keep the message.</summary>
    private static string Clean(string line)
    {
        int i = line.IndexOf("] ", StringComparison.Ordinal);
        return (i >= 0 && i < 60 ? line[(i + 2)..] : line).Trim();
    }

    /// <summary>librespot.log: stderr lines and lifecycle steps. Never tokens; rolls over at ~1 MB.</summary>
    private void Log(string line)
    {
        if (line.Contains("access_token", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Bearer ", StringComparison.Ordinal) ||
            line.Contains("code=", StringComparison.Ordinal)) return;
        lock (_logLock)
        {
            try
            {
                var fi = new FileInfo(_logPath);
                if (fi.Exists && fi.Length > 1024 * 1024) File.Move(_logPath, _logPath + ".old", true);
                File.AppendAllText(_logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {line}\r\n");
            }
            catch { }
        }
    }
}
