# Flow: Built-in Spotify Playback (librespot) + Lightweight Pass

Brief for Claude Code working in this repo (`src/Flow`, C# / WPF / .NET 10, win-x64).
Read the whole file before starting. Work in the phases below, one commit per phase.

## Goal

Add an **optional** Spotify playback engine to Flow that does not need the
Spotify desktop app running. Flow launches **librespot** as a small hidden
background process. librespot signs in to the user's Spotify Premium account,
appears in Spotify Connect as a device named "Flow", and streams decoded audio
to Flow over a pipe. Flow plays that audio through its own `AudioEngine`, so
Spotify tracks get Flow's EQ, volume, and visualizer with no loopback capture.

The existing behavior (controlling the Spotify app) stays the **default**. The
new engine is a choice in **Settings → Spotify → Playback engine**.

The owner (Joseph) understands librespot is unofficial, uses Spotify's desktop
client identity for sign-in, is not permitted by Spotify's terms, and can break
when Spotify changes things. He has chosen to add it as an opt-in option for
personal use. Don't re-litigate this; just label it "Experimental" in the UI.

Hard rule: **live playback only.** Never write decoded Spotify audio to disk,
never add export/record/download features, never bypass Premium.

## Reference

- librespot: https://github.com/librespot-org/librespot (MIT). Latest release
  at time of writing: v0.8.0. Options: https://github.com/librespot-org/librespot/wiki/Options
- Spotifast (Rust app that embeds librespot, MIT): https://github.com/crmne/spotifast
  Useful for ideas only; Flow uses the librespot **CLI binary**, not the crate.

## How Flow works today (don't break it)

- `Spotify/SpotifyService.cs`: PKCE sign-in with the user's own Client ID,
  DPAPI token in `%LOCALAPPDATA%\Flow\spotify.token`, `SendAsync` Web API
  wrapper, library import into `spotify_library.json`, Deezer genre lookup.
- `Spotify/SpotifyPlayback.cs`: plays a "run" of URIs on a Connect device via
  `PUT /me/player/play`, polls `/me/player` every 1 s, raises `ItemChanged`,
  `RunEnded`, `ForeignItem`, `PlayingChanged`. `EnsureDeviceAsync` launches the
  Spotify app (`spotify:`) when no device exists; `PickDeviceAsync` prefers the
  active device, then one named like the PC, then any "Computer".
- `Services/PlaybackService.cs`: owns the queue; `StartSpotifyAsync`,
  `ResumeSpotify`, volume/mute forwarding to `_sp.SetVolume`, and
  `LoopbackTap` start/stop for the visualizer during Spotify playback.
- `Audio/AudioEngine.cs`: WASAPI shared output, `Pipeline` mixes
  current/next/outgoing `TrackSource`s → `Equalizer.Process` →
  `Analyzer.Write` → volume + soft clip. Engine rate = device mix rate
  (usually 48 kHz).
- `Interop/SingleInstance.cs`, `TrayIcon.cs`, `SmtcService.cs` exist already.

## Phase 0: Get librespot.exe

librespot does not reliably ship Windows binaries; build it.

1. Install Rust (https://rustup.rs) with the MSVC toolchain.
2. `git clone --branch v0.8.0 https://github.com/librespot-org/librespot`
3. Open its root `Cargo.toml`, check the `[features]` table, and build with
   **no audio backends except the always-available `pipe`**, plus a rustls
   TLS feature (names change between versions, so read them; at v0.8 they look
   like `rustls-tls-native-roots`). Example:
   `cargo build --release --no-default-features --features rustls-tls-native-roots`
   If that fails to link or a TLS feature is required under another name, use
   whatever the Cargo.toml lists. Verify with `librespot.exe --help` and
   `librespot.exe --backend ?` (must list `pipe`).
4. Add `tools/build-librespot.ps1` that does steps 2 and 3 and copies the
   result to `tools/librespot/librespot.exe`.
5. Ship it next to Flow: in `Flow.csproj` add the exe as `<None>` with
   `CopyToOutputDirectory=PreserveNewest` and `ExcludeFromSingleFileBundle=true`
   so it lands beside `Flow.exe` in `publish/`. `.gitignore` already ignores
   `*.exe`; keep it out of git and document the build step in README.
6. Add `THIRD-PARTY-NOTICES.md` with librespot's MIT license text.

## Phase 1: Settings

`Services/SettingsService.cs` → `AppSettings`:

```csharp
public SpotifyEngine SpotifyEngine { get; set; } = SpotifyEngine.SpotifyApp; // SpotifyApp | BuiltIn
public string LibrespotDeviceName { get; set; } = "Flow";
public int LibrespotBitrate { get; set; } = 320;            // 96 | 160 | 320
public bool LibrespotNormalisation { get; set; } = false;
public bool LibrespotStartWithFlow { get; set; } = false;   // false = start on first Spotify play
```

Settings → Spotify section (SettingsView + SettingsViewModel), shown only when
Spotify is connected:

- **Playback engine**: "Spotify app (default)" / "Built-in (librespot), Experimental".
- When Built-in is selected:
  - Status line: Not set up / Signing in… / Ready as "Flow" / Stopped / Error: …
  - **Set up playback** button (runs the OAuth step in Phase 2).
  - **Sign out of playback** (stops the process, deletes librespot's credential
    cache folder). Confirm with `FlowDialog.Confirm`.
  - Device name, Bitrate (96/160/320), Volume normalisation, Start with Flow.
  - One line of grey help text: "Plays Spotify inside Flow without the Spotify
    app. Unofficial; may stop working when Spotify changes things. Premium only."
- If `librespot.exe` is missing next to `Flow.exe`, disable the option and say
  "librespot.exe not found next to Flow.exe".

Switching engines while Spotify is playing: pause, deactivate the old path,
then the next play uses the new engine. No restart.

## Phase 2: `Spotify/LibrespotHost.cs` (process lifecycle)

New class, one instance owned by `App`, passed to `PlaybackService` and
`SettingsViewModel`. Responsibilities:

**Paths**
- Exe: `Path.Combine(AppContext.BaseDirectory, "librespot.exe")`.
- System cache (credentials): `%LOCALAPPDATA%\Flow\librespot\system`.
- Audio cache: `%LOCALAPPDATA%\Flow\librespot\cache`, limited with
  `--cache-size-limit 1G` (librespot's own encrypted cache; this is not export).
- Log: `%LOCALAPPDATA%\Flow\librespot.log` (stderr only, rotate at ~1 MB,
  never log tokens).

**Sign-in (one time)**
- `HasCredentials` = the system cache folder contains librespot's saved
  credentials file (check what v0.8 writes, e.g. `credentials.json`).
- `SetUpAsync()`: run librespot with `--enable-oauth --oauth-port 5588`
  (plus the cache flags), read stderr/stdout lines, find the
  `https://accounts.spotify.com/authorize...` URL it prints, open it with
  `Process.Start(new ProcessStartInfo(url){ UseShellExecute = true })`. When
  the credentials file appears, stop that process and report Ready. Timeout
  4 minutes. Port busy → clear error message.
- Verify by running it once manually first: confirm the exact log line, and
  that a later launch **without** `--enable-oauth` signs in from the cache.
  If v0.8 needs `--username` to pick cached credentials, store the username it
  logs after sign-in.

**Run**
Arguments (build with `ArgumentList`, not a string):
```
--name "<LibrespotDeviceName>" --device-type computer
--backend pipe --format F32 --bitrate <LibrespotBitrate>
--system-cache <system> --cache <cache> --cache-size-limit 1G
--initial-volume 100 --volume-ctrl fixed
--disable-discovery --quiet
[--enable-volume-normalisation]
```
- `--backend pipe` with no `--device` writes raw interleaved PCM to **stdout**:
  44,100 Hz, 2 channels, 32-bit float (because of `--format F32`). Confirm
  the rate and channel count against librespot's source/docs at v0.8 and fail
  loudly if different. Logs go to stderr.
- `--volume-ctrl fixed` + `--initial-volume 100`: librespot outputs full
  scale; Flow's own volume is the master.
- `--disable-discovery`: no zeroconf; the device registers through the
  signed-in account and shows up in `/me/player/devices`.
- `ProcessStartInfo`: `UseShellExecute=false`, `CreateNoWindow=true`,
  redirect stdout (binary) and stderr, `StandardOutput.BaseStream` read on a
  dedicated background thread (not the thread pool), `BELOW_NORMAL` is NOT
  appropriate for audio; leave default priority.
- Put the child in a **Windows Job Object** with
  `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` so it dies if Flow crashes or is killed.
  P/Invoke goes in `Interop/NativeMethods.cs`.
- Restart policy: if it exits unexpectedly, restart with backoff 2 s, 4 s,
  8 s… capped at 60 s; after 5 failures in 10 minutes set Error status and
  stop retrying until the user plays again or presses Set up.
- Start: when `LibrespotStartWithFlow` is on, after startup settles (reuse the
  20 s settle timer in `App.StartApp`); otherwise lazily on the first Spotify
  play. Stop on app exit (`App.OnExit`) and when the engine is switched back
  to Spotify app or the user signs out.
- Expose: `Status`, `IsReady`, `event Action<string> StatusChanged`,
  `LiveInput Input` (Phase 3), `Task<bool> EnsureRunningAsync()`.

## Phase 3: Audio into Flow's engine

New `Audio/LiveInput.cs`:
- A `BufferedWaveProvider` (or a lock-free float ring buffer) fed by
  LibrespotHost's stdout reader, format `WaveFormat.CreateIeeeFloatWaveFormat(44100, 2)`.
- Wrapped as `ISampleProvider` and resampled to the engine rate with
  `WdlResamplingSampleProvider` when the engine isn't 44.1 kHz.
- Buffer: ~2 s capacity, `DiscardOnBufferOverflow = true`,
  `ReadFully = true` (silence on underrun so WASAPI never stops).
- `Flush()` to drop buffered audio. Call it whenever Flow sends play, seek,
  next, or previous, so stale audio isn't heard after the command.
- Counts frames consumed, for an accurate position (see Phase 4).

`AudioEngine.Pipeline`:
- Add `SetLiveInput(ISampleProvider? input)`. When set, `Read` takes samples
  from the live input instead of `_current`/`_next` (no crossfade/gapless for
  live input), then the existing chain runs unchanged: `Equalizer.Process` →
  `Analyzer.Write` → volume + soft clip.
- Clearing local tracks (`Stop`/`Load`) must also clear the live input, and
  setting a live input must stop local track sources.
- Reading the pipe must never block the WASAPI callback.

## Phase 4: Wire playback

`Spotify/SpotifyPlayback.cs`:
- Give it the engine choice (read `SettingsService` each call).
- **Built-in mode, `EnsureDeviceAsync`**: call `LibrespotHost.EnsureRunningAsync()`,
  then poll `/me/player/devices` up to ~15 s for the device whose name equals
  `LibrespotDeviceName`. Never launch the Spotify app. Never use the
  `Process.Start(uri)` local-app fallback in this mode.
- **Built-in mode, `PickDeviceAsync`**: pick the librespot device by name,
  ignoring whatever else is active.
- `PlayAsync` stays the same (Web API `PUT /me/player/play?device_id=...`).
- **Volume**: in Built-in mode don't call `/me/player/volume`; Flow's
  `AudioEngine.Volume` already applies.
- Position: in Built-in mode, prefer frames-consumed from `LiveInput`
  (reset on each play/seek) over the extrapolated Web API position; keep the
  poll for track changes and run end, but slow it to every 3 s.

`Services/PlaybackService.cs`:
- In Built-in mode: before `_sp.PlayAsync`, call
  `_engine.SetLiveInput(librespot.Input)`, `librespot.Input.Flush()`, and
  `_engine.Play()`. Do **not** start `_loopback`.
- Pause/resume: pause the engine output and send Web API pause/resume (both).
- Seek/next/previous: Web API command + `Input.Flush()`.
- When leaving Spotify for a local track (`DeactivateAsync`), pause librespot
  via Web API and `_engine.SetLiveInput(null)`.
- App-mode code path stays exactly as it is.

Windows integration: `SmtcService` (media flyout/keys) already follows Flow's
`PlaybackService`, so it should just work; verify.

## Phase 5: Test checklist

Run each and note results in the PR/commit message:
1. Engine = Spotify app: everything behaves exactly like v1.7.0.
2. Built-in: Set up playback → browser approval → status "Ready as Flow".
3. Close the Spotify app entirely. Play a Spotify album in Flow: audio plays,
   EQ changes affect it, visualizer moves, **loopback is not running**.
4. Seek, next, previous, pause/resume, volume, mute: no stale audio, no pops.
5. Album run plays through to the end and Flow's queue advances to the next item
   (including from Spotify track to a local file and back).
6. Phone's Spotify app shows "Flow" as a device and can control it.
7. Kill `Flow.exe` from Task Manager: `librespot.exe` disappears too.
8. Kill `librespot.exe`: Flow shows a status message and restarts it.
9. Sign out of playback: credentials folder gone, option shows "Not set up".
10. Unplug/change the audio output device mid-song: audio continues
    (existing `ReinitOutput` path).
11. Memory and CPU: compare Task Manager with the Spotify app path; record
    numbers for both.

## Phase 6: Lightweight pass (independent of librespot)

Findings from a code review of v1.7.0. Each is a separate small commit.

1. **Visualizer Off still runs every frame.** `Visualizer/MusicVisualizer.cs`
   stays subscribed to `CompositionTarget.Rendering` when `VisualStyle == Off`,
   which keeps WPF rendering at the monitor's refresh rate. Unsubscribe when
   Off, and also when the window is minimized (a minimized window is still
   `IsVisible`; listen to `MainWindow.StateChanged`).
2. **Cap the visualizer at 60 fps.** Skip `Tick` when less than 16.6 ms has
   passed since the last drawn frame (Spotifast and Winamp use a fixed 60 Hz
   step). Skip the FFT in `SpectrumAnalyzer.Compute` when the ring buffer is
   silent.
3. **Loopback only when visible.** `PlaybackService.StartSpotifyAsync` and
   `ResumeSpotify` start `LoopbackTap` even when the visualizer is hidden or
   Off. Start it only while the visualizer is visible and not Off; stop it
   otherwise. (Built-in mode never uses it.)
4. **Less Spotify polling in app mode.** When the active device is this PC's
   Spotify app, follow it with
   `Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager`
   (session `SourceAppUserModelId` contains "Spotify"): `MediaPropertiesChanged`,
   `PlaybackInfoChanged`, `TimelinePropertiesChanged`. No network. Keep Web
   API polling only for remote devices, every 3 s, with existing local
   extrapolation.
5. **Incremental sync.** `SpotifyService.SyncAsync` re-imports everything every
   12 h. Liked Songs and saved albums come newest first: stop paging at the
   first URI already in the cache, and compare the response `total` with the
   cached count to detect removals (full re-import only then). Store each
   playlist's `snapshot_id` in `SpotifyPlaylist` and skip playlists whose
   snapshot hasn't changed.
6. **Smaller art downloads.** `ParseTrack` picks the largest image (640 px).
   Store both the ~300 px and ~640 px URLs; download 300 px for thumbnails
   during sync, and fetch the large one lazily when the album page or Now
   Playing needs it. In `ArtworkCache`, don't upscale: cap the large file at
   the source size.
7. **No duplicate Spotify library in memory.** After `LoadCache` converts
   `Cache.Tracks` into `Track`s, the DTO list stays in memory. Persist Spotify
   tracks in `LibraryDb` (SQLite) instead of the JSON, or release the DTO
   list after conversion and reload it only for sync.
8. **Request wrapper (ported idea from Spotifast `src/api/client.rs`).** In
   `SpotifyService.SendAsync`: one shared cooldown (`DateTime _cooldownUntil`)
   that every request waits on after a 429, honoring `Retry-After` up to 30 s;
   return immediately when the 429 body contains `"QUOTA_EXCEEDED"` with a
   friendly status; retry a GET once after 800 ms on 5xx; cap concurrent API
   requests at 6 with a `SemaphoreSlim`.
9. **Idle timers.** `PlaybackService._timer` ticks every 200 ms forever; stop
   it while nothing is playing and restart on play.
10. **Release build trim.** Wrap `App.Probe.cs`, `App.PerfProbe.cs`,
    `MemoryProbe`, `--test-genres` and the other developer switches in
    `#if DEBUG` so they're not in release builds.
11. **Optional, exe size.** Flow.exe is 161 MB because the .NET runtime is
    bundled. Add a second publish profile, framework-dependent
    (`--self-contained false`), for machines with the .NET 10 Desktop Runtime
    installed. Keep the self-contained one as the default release. Don't use
    trimming or Native AOT (not supported for WPF).

## Conventions

- Keep the existing code style (file-scoped namespaces, `ObservableObject`,
  `RelayCommand`, `DiagLog`/`SpotifyLog`, `App.Log` for exceptions).
- No em dashes in UI text, comments, or docs.
- Never log or display tokens or credential file contents.
- Update `README.md`: Spotify section gets a "Built-in playback
  (Experimental)" subsection and the librespot build step; bump `<Version>` in
  `Flow.csproj` to 1.8.0.
- Build: `dotnet publish src/Flow -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish`
  then confirm `publish/librespot.exe` exists beside `publish/Flow.exe`.
