# Changelog

## Unreleased

- New: **Settings surface** inside the island - drag sliders for **Size** (0.80-1.40x,
  scales the whole island including type) and **Opacity**, a live accent swatch strip
  (Album / Green / Blue / Pink / Amber / Mono) and a **Monitor** stepper. Persists as
  `uiScale`, `opacity`, `accent`, `monitorIndex`.
- New: `tools/sign.ps1` Authenticode-signs the binaries (dev cert / `.pfx` / thumbprint)
  with no SDK needed, timestamped; the release workflow signs the zips when the
  `SIGNING_PFX_BASE64` secret is set.
- New: in-island settings replace the WPF settings dialog entirely.
- Removed: the WPF renderer, its window, tray service, converters and artwork-cache
  (WPF/media-imaging). The app is now a single Skia/Win32 layered-window build; the exe
  brings up the overlay directly with no renderer flag. `ArtworkCache` is gone - artwork
  is decoded straight to `SKImage` in `SkiaMode` and in `--art-probe`.
- Fix: SMTC reads that threw ("Thread is running or terminated") now tear the manager
  down and re-request it (with backoff) instead of going quiet until restart. The
  background refresh also retries three times.
- Fix: *Launch at startup* wrote a bare `dotnet.exe` when the app ran through the
  runtime; it now registers the runtime plus the assembly so it actually relaunches.
- New: global media hotkeys - `Ctrl+Alt+Space` play/pause, `Ctrl+Alt+Left/Right`
  prev/next, with a short visual peep. Toggle with the *Media hotkeys* menu row.
- New: **Lyrics sync** menu row (+/- 0.5 s, middle resets) writes `lyricsOffsetMs` and
  applies the nudge to both the karaoke sweep and the scrolling page.
- New: hide while a fullscreen app is in the foreground (`hideWhenFullscreen`), polled
  twice a second and compared against the whole monitor rect.
- Menu: three more rows (Lyrics sync, Media hotkeys, Settings); the body sizes to the
  row count (316 x 198-232) instead of a fixed 216.
- Fix: `RebuildMenu` ran off the end of a 7-slot array after the new rows; the app stayed
  alive but drew nothing. Menu array is sized to the row list now.
- Fix: "ran it, nothing appeared". The island peeks for ~4.5 s on launch with a
  *Waiting for Spotify...* pill, launch is now logged (show/hide/audio), and the log names
  the SMTC session it found.
- Fix: the overlay no longer swallows mouse clicks - the empty part of the surface is
  hit-tested as `HTTRANSPARENT`, so only the island body is clickable.
- New: launching the app a second time asks the running island to show itself instead of
  exiting silently.
- New: `--skia-demo [seconds]` draws a live sample player on the real layered window
  (no Spotify needed) and logs window visibility + a bitmap alpha probe.
- Docs: troubleshooting section, and a concrete note for Trend Micro AV blocks.
- Visual pass over every Skia surface: album-tinted halo, glassy body highlight,
  solid white transport button, progress knob, bottom-edge playback line, audio bars
  on the collapsed pill and player, and a cleaner page indicator.
- Rebuilt the quick-settings menu (316x216): grouped icon rows, inline on/off switches,
  the active output device, chevrons and a version chip.
- Lyrics page: album-tinted active-line marker, a soft wash glow and slightly larger type.
- New `docs/banner.png` hero and tight screenshot crops; `tools/make-banner.ps1`.
- `--skia-shot` now renders a synthetic cover (no third-party artwork) and a realistic
  seven-row menu.

## 2.1.0

- Added an experimental Skia renderer (`--skia`): a raw Win32 layered window drawn
  with SkiaSharp instead of a WPF `AllowsTransparency` window, which WPF composites
  in software.
- Island surfaces: collapsed LINE pill, MUSIC player, LYRICS page, MENU and VOLUME.
- Karaoke lyric sweep, album-tint wash, page dots, title marquee, hover states.
- Media output switcher: the volume surface (Core Audio) and a MENU row that cycles
  the default playback device.
- Spring-morphed body geometry, fade+shrink show/hide.
- `install.ps1` publishes the app and creates Start Menu / Desktop shortcuts.
- `--selftest` for pure-logic assertions.

## 2.0.0

- Attached top-edge notch, spotify-only SMTC, synced lyrics (LRCLIB / local LRC),
  and a real audio-reactive visualizer driven by per-process WASAPI loopback.
- Settings window, tray icon, launch-on-startup.
