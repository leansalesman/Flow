# Flow — Appearance themes plan

Selectable in **Settings → Appearance**. Switching is instant (no restart) and remembered.

| Theme | Window | Accent | Notes |
|---|---|---|---|
| **Minimal** (default, today's look) | Translucent (WindHawk shows through) | Windows accent | Unchanged |
| **Dark** | Solid dark | Windows accent | Standard dark mode |
| **Light** | Solid light | Windows accent | Standard light mode |
| **Automatic** | Solid | Windows accent | Follows Windows' app mode: Dark ↔ Light, live |
| **Brushed Metal** | Solid, brushed-aluminum | Classic aqua blue | Full mid-2000s iTunes-style makeover |

## Phase 1 — Theme groundwork
- `AppSettings.Theme` (`Minimal` / `Dark` / `Light` / `Auto` / `Metal`), default `Minimal`.
- `ThemeService` becomes palette-driven: each theme supplies its colors/brushes (text, surfaces, strokes,
  overlay, menus, scrims, play button, shelves, visualizer colors). The existing `DynamicResource` keys stay,
  so every screen restyles live.
- Translucency per theme: Minimal keeps the transparent window + DWM frame extension; solid themes paint an
  opaque window background (and drop the DWM extension), so WindHawk is not needed for them.
- Per-theme **style dictionaries** (merged on top of `Controls.xaml`) so a theme can reshape controls, not just
  recolor them — needed for Brushed Metal.
- Settings: an Appearance section with small preview swatches for each theme.
- Audit the few hard-coded colors (≈10) and move them to theme keys.
- Verify with `--render-library` renders of every screen in every theme, and `--perf-switch` for speed.

## Phase 2 — Dark
- Neutral near-black background (#1C1C1E-ish), slightly lighter panels/sidebar, subtle dividers.
- Album-overlay blur and scrims retuned for an opaque background.

## Phase 3 — Light (+ Automatic)
- White / light-gray surfaces, dark text, softer shadows; check every icon, slider, chip, menu and the
  visualizer for contrast.
- Automatic listens to Windows' app-mode setting and swaps Dark ↔ Light live.

## Phase 4 — Brushed Metal (full makeover, Now Playing keeps big art + visualizer)
- Procedurally generated brushed-aluminum texture (no Apple artwork or logos), metal title bar with
  gradient and embossed text.
- Round glossy transport buttons (previous / play-pause / next) and pill-shaped volume slider.
- **LCD display**: the player bar's center becomes the classic pale-blue/green LCD with song, artist,
  album and a progress bar.
- **Source list** sidebar: blue-gray with the classic selection gradient.
- Library table: blue-and-white striped rows, glossy column headers; album shelves in metal-framed rows.
- Now Playing: same big album art + visualizer, framed in metal / LCD styling; visualizer colors to match.
- Scrollbars, buttons, menus and dialogs restyled in the Aqua spirit.

Each phase ships as its own numbered GitHub release.
