# Changelog

## Unreleased

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
