# Changelog

## 2.2.3

- New: menu row **Hide when fullscreen** - the behaviour existed but was only reachable by
  editing `settings.json`. Turn it off and the island stays up over games and fullscreen
  video; turn it back on and it steps out of the way again. The row reads its state
  (`On`/`Off`), saves immediately, and turning it off brings the island back on the next
  frame instead of waiting for the half-second fullscreen poll.
- Fix: a second launch (shortcut, Start menu, Windows search) could not find the running
  island - `FindWindow` matched class *and* title, and the overlay's title did not survive
  creation, so the lookup found nothing and the launch silently did nothing. The window now
  gets its title set explicitly and is matched by class only.
- App registration so it shows up as an installed app: Start menu shortcut (both
  `Float Island` and `FloatIsland` spellings), `App Paths\FloatIsland.exe`, an
  *Applications* entry, and an *Uninstall* entry for Settings > Apps.

## 2.2.2

- Fix: **lyrics started in sync then slid off the vocal.** The lyric sheet was chosen by
  declared run time alone, and LRCLIB has rows that match the studio cut's duration while
  their timestamps belong to a different edit (official video, extended mix) - so line 1
  landed on the voice and everything after drifted, up to ~50 s by the outro. Candidates are
  now ranked on run time *and* where the sheet's own last timestamp falls
  (`LrclibProvider.FitScore`). Measured on a live track: the pick for
  *Last Friday Night (T.G.I.F.)* moved from a sheet ending at 4:43.69 (track is 3:50.73) to
  one ending at 3:41.96.
- New: if no correctly timed sheet exists, the timestamps are stretched onto the real run
  time instead of drifting (logged as `LRCLIB: only sheet available ... stretched by`).
- Karaoke rows (instrumental/karaoke LRCLIB entries) carry a small fit penalty so a vocal
  sheet at the same run time is preferred.
- Fix: a clock resync can no longer move the lyric clock backwards - it only advances it -
  so a republished timeline value that lands behind the extrapolated clock cannot snap the
  sweep back inside a line. Paused playback no longer extrapolates the clock at all
  (`SkiaMode.LivePosition`).
- `--lyrics-probe 'Title|Artist|Album|seconds'` accepts a track length, which is how the
  sheet ranking is exercised without Spotify holding that track.

## 2.2.1

- Fix: **lyrics ran ~3 s behind the audio.** Windows/Spotify only refresh the media
  timeline every few seconds (measured ~4.5 s), so reading `TimelineProperties.Position`
  straight gave a position up to 3 s stale. The live position is now reconstructed from
  `LastUpdatedTime` (capped at 8 s as a sanity bound) and re-anchored about once a second
  while playing, so the karaoke sweep and the line index track the voice.
- Fix: **a few seconds of lag when a track changed.** A track change inside the same
  session raises `MediaPropertiesChanged`, which nothing was listening to - the island kept
  the previous song (and its lyrics) until the next playback/timeline event. Now subscribed,
  plus a follow-up read ~0.6 s later for the window right after a skip where Spotify can
  still report the previous track's metadata.
- New: the collapsed pill **widens with a long lyric** (up to 640 of the 720 DIP surface)
  instead of clipping at a fixed 452; `--skia-shot` renders a `collapsed-long` sample that
  logs the measured width as proof.
- Lyrics sync steps are 0.1 s (were 0.5 s), clamped to ±5 s, since the clock fix leaves
  only a small residue to trim.
- Fix: seeking moved our own clock immediately instead of waiting for the next SMTC event.
- Fix: `--selftest`, `--skia-shot`, `--skia-bench`, `--skia-demo` and `--make-icon` ran
  behind the single-instance mutex, so on a machine with the island already up they exited
  silently and *looked* like they passed. They now run before the mutex is taken - which is
  also what the CI smoke test needs.
- `--smtc-probe` now logs `LastUpdatedTime`, its age and the corrected position, which is
  how the ~4.5 s re-publish cadence above was measured.

## 2.2.0

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
