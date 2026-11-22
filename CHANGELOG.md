# Changelog

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
