# Flow — the minimal and beautiful music player

A lightweight, translucent music player for Windows 11 (x64), built with C# / WPF on .NET 10 —
a modern take on the classic mid-2000s iTunes feel, for local files **and** your Spotify library.

Created by **Joseph Martinez**.

**Download:** grab `Flow-win-x64.zip` from the [Releases](../../releases) page, unzip, run `Flow.exe`
(portable and self-contained — nothing to install). Windows SmartScreen may warn because the app isn't
code-signed: choose **More info → Run anyway**.

## Features
- **Translucent window** — Flow draws no background of its own, so a system backdrop such as the WindHawk
  *Translucent Windows* mod shows through the whole app.
- **Now Playing** — centered album art, timeline, Previous / Play-Pause / Next (tap to skip, **hold to
  rewind / fast-forward**), shuffle, repeat, favorite, volume. Artist and album are links into the library.
- **9 visualizer styles + Off** — Mirrored Blocks, Radial Ring, Mirror Mountains, Spectrum Bars,
  Oscilloscope, Spectrogram, Block Rain, Particle Field, Bass Halo. Cycle with the button top-right
  (right-click = previous). Uses your Windows accent colors.
- **Library** — album-cover shelves or a sortable track table (Title, Artist, Album, Length, Year, Genre,
  Source, Plays, Date Added); instant search; **All / Local / Spotify** filter.
- **Genre chips** — toggle one or more genres to filter shelves and table; Play / Shuffle then use only
  those genres.
- **Interactive library** — click an artist for their page (albums, appears-on, all songs), an album to
  spotlight it, a genre to filter by it.
- **Player bar** — iTunes-style controls with an "LCD" display on every page except Now Playing.
- **Playlists** — create, rename, reorder (drag), import/export `.m3u8`; smart lists: Favorites,
  Recently Added, Most Played, Recently Played; Spotify playlists you own.
- **Up Next queue** — play next, add to queue, drag to reorder.
- **Audio** — 10-band EQ with presets, crossfade (0–12 s), gapless playback, ReplayGain (track/album).
- **Windows integration** — media keys, Windows 11 media flyout & lock screen, taskbar thumbnail
  buttons + progress, optional tray icon, drag & drop, single instance, optional "Open with" registration.
- **Formats** — MP3, AAC/M4A, ALAC, FLAC, WAV, WMA, OGG Vorbis, Opus, AIFF.
- **Lean** — covers decoded at display size with a small shared cache; memory is released when you
  leave the library and when Flow is minimized.

## Spotify (Premium)
Flow can import your Spotify library onto the same shelves as your local music and play it.
Spotify audio is DRM-protected, so it plays through the **Spotify app** (on this PC or any Spotify Connect
device); Flow controls it, keeps one queue across local + Spotify songs, and the visualizer follows the
sound via loopback capture.

One-time setup (Settings → Spotify) — each person uses their own free developer app:
1. Open the [Spotify Developer Dashboard](https://developer.spotify.com/dashboard) → **Create app**.
2. Add the Redirect URI `http://127.0.0.1:43117/callback`, tick **Web API**, save.
3. Paste the app's **Client ID** into Flow and press **Connect Spotify**, then approve in the browser.

What's imported: Liked Songs, saved albums, and playlists you own or collaborate on (Spotify no longer
shares the contents of playlists you only follow). Flow syncs automatically every 12 hours, or press
**Sync now**. Genres come from Deezer's public catalog (Spotify no longer provides them to personal
apps). The sign-in token is stored encrypted for your Windows account; **Disconnect** removes it.

## Keyboard shortcuts
| Key | Action |
|---|---|
| Space | Play / pause |
| ← / → | Seek ∓5 s |
| Ctrl+← / Ctrl+→ | Previous / next |
| ↑ / ↓ | Volume |
| M | Mute |
| Ctrl+S / Ctrl+R | Shuffle / repeat |
| Ctrl+F / Ctrl+L / Ctrl+P | Search / Library / Now Playing |
| Ctrl+O | Open files |
| Esc | Close album / artist page, queue |

## Data
Settings, the library database, cached artwork and the Spotify cache live in `%LOCALAPPDATA%\Flow`.
`spotify.log` there records Spotify playback steps (no tokens). Set `FLOW_DEBUG=1` for a `debug.log`.

## Build
Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).
```
dotnet publish src/Flow -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```
Intermediate build output goes to `%LOCALAPPDATA%\FlowBuild` (see `Directory.Build.props`) so cloud-synced
folders don't lock it.

### Optional: librespot (built-in Spotify playback, experimental)
Flow's optional built-in Spotify engine runs [librespot](https://github.com/librespot-org/librespot) (MIT) as a
background process. It is not in git; build it once before publishing:

1. Install [Rust](https://rustup.rs) (MSVC toolchain) and the Visual Studio 2022 Build Tools with the
   "Desktop development with C++" workload.
2. Run `powershell -ExecutionPolicy Bypass -File tools\build-librespot.ps1`. This builds librespot v0.8.0 with
   only the pipe audio backend and rustls, and copies it to `tools\librespot\librespot.exe`.
3. Publish as above: `librespot.exe` is copied next to `Flow.exe` in `publish\`.

Without `librespot.exe`, Flow builds and runs as usual and the built-in engine is simply unavailable.
See `THIRD-PARTY-NOTICES.md` for librespot's license.

### Developer options
| Command | What it does |
|---|---|
| `Flow.exe --render-visualizers <folder>` | Renders every visualizer style to PNGs (synthetic signal) |
| `Flow.exe --render-library <folder> [artist]` | Renders Library / artist / album / Settings screens off-screen, logs binding errors |
| `Flow.exe --memory-test <file>` | Library + cover memory measurement |
| `Flow.exe --memory-ui <file>` | Runs the real UI off-screen and records memory per step |
| `Flow.exe --test-genres <file>` | Dry-run genre lookups for 40 albums |
