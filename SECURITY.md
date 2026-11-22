# Security

## Reporting a vulnerability

Open a private security advisory on GitHub, or email the maintainer listed in the
repository profile. Please do not open a public issue for a security problem.

## Antivirus false positives

Float Island is regularly flagged by behaviour-based antivirus products (Trend Micro,
Kaspersky, Avast, ...) and by SmartScreen. This is expected for this kind of program,
and it is a **false positive**. Here is exactly what the app does and why it trips
heuristics.

### What the app actually does

| Behaviour | Why | What it does *not* do |
|---|---|---|
| Activates WASAPI **process loopback** on `Spotify.exe` | to drive the audio-reactive UI from the music | the audio is analysed in memory and discarded; nothing is written to disk or sent anywhere |
| Enumerates processes (`Process.GetProcessesByName("Spotify")`) | to find the Spotify process to attach to | no other process is inspected or modified |
| Creates a hidden, topmost, click-through-capable layered window with no taskbar button | that is what an overlay notch is | it does not capture the screen or the keyboard |
| Reads and displays the Windows media session (SMTC) | to show the track and offer transport buttons | it does not read other apps' data |
| Writes an `HKCU\...\Run` value | only when you turn on *Launch at startup* in the menu | nothing is written at first run |
| Calls `lrclib.net` | to fetch lyrics for the current track | it sends only title/artist/album/duration; no identifiers, no telemetry |

The combination - *an unsigned binary that quietly overlays the desktop, captures
another process's audio, and can add itself to autostart* - is indistinguishable from a
spyware pattern to a heuristic engine. That is why it is flagged.

### If your antivirus blocks it

1. **Verify before trusting.** Build from source (see `CONTRIBUTING.md`) or compare the
   release zip against the `SHA256SUMS.txt` attached to the same release:
   ```powershell
   Get-FileHash SonoIsland-win-x64.zip -Algorithm SHA256
   ```
2. **Whitelist the folder** in your AV product (e.g. Trend Micro:
   *Settings -> Exceptions -> Add folder*), or pause protection while installing.
3. **Report the false positive** to the vendor so everyone benefits:
   - Microsoft: <https://www.microsoft.com/wdsi/filesubmission>
   - Trend Micro: <https://www.trendmicro.com/en_us/about/legal/report-false-positive.html>

### For maintainers

- A **code-signing certificate** removes most of this. An OV/EV certificate (or Azure
  Trusted Signing) lets you sign the executable in the release workflow; signed binaries
  carry reputation and are no longer treated as suspicious.
- Keep the heuristics exposure honest and documented: do not add silent autostart, do not
  add screen or keyboard capture, do not add telemetry.
- Always publish `SHA256SUMS.txt` alongside the zips.
