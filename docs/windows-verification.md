# Windows verification checklist

Nothing below has run on Windows yet. The portable logic is unit-tested on macOS; the SMTC,
Core Audio and process code is only compiled. Run on a Windows 11 machine with Edge, and use a test
KHost **with no account plugins loaded**. Record pass/fail and any log line quoted.

## 1. Build and install
- [ ] `dotnet build KHost.Plugins.YouTubeMusic.slnx -c Release`: 0 warnings, 0 errors.
- [ ] `dotnet test tests/KHost.Plugins.YouTubeMusic.Tests` is green on Windows too.
- [ ] Copy `src/KHost.Plugins.YouTubeMusic/bin/Release/net10.0-windows10.0.19041.0/*` into
      `<host>/plugins/khost.youtube-music/`. Confirm the folder holds **no** `KHost.Abstractions.dll`
      / `KHost.Common.dll`, and **does** hold `WinRT.Runtime.dll` and `Microsoft.Windows.SDK.NET.dll`.
- [ ] Enable it on the Plugins page and restart. The row shows no load error.

## 2. Setup button
- [ ] Fresh profile: the row warns "not installed in this plugin's Edge profile"; *Open* is hidden.
- [ ] *Set up YouTube Music* opens an ordinary Edge window on music.youtube.com, in
      `%LOCALAPPDATA%\KHost\youtube-music-profile` (check `edge://version` → Profile path). The
      host's own Edge profile is untouched.
- [ ] Sign in, then install via *App available*. Within ~5s the button reads *Set up YouTube Music
      again* and *Open YouTube Music* appears. If not, note what exists under
      `<profile>\Default\Web Applications\Manifest Resources\` and whether `Preferences` names
      `cinhimbnkkaeohfgghhklpknlkffjgod`.
- [ ] Rename `msedge.exe`'s folder in a VM (or test on a machine without Edge): the button
      reads *Microsoft Edge not found* and is disabled.

## 3. Start, playlist and session match
- [ ] Set a playlist link, close the app, press Play on the console. The **installed app window**
      opens (not a tab, not an anonymous `--app` window) **at that playlist and starts playing**
      without a click. This checks `--app-id` + `--app-launch-url-for-shortcuts-menu-item`, the
      `watch?list=` URL, and autoplay.
- [ ] Log says `Found YouTube Music's media session (music.youtube.com-…!App)`. Play something in a
      normal Edge tab at the same time: the console ignores it.
- [ ] Blank playlist with the app already holding a paused queue: Play resumes it.
- [ ] Console names title and artist; the screen card appears if the venue has it on.

## 4. Transport
- [ ] Pause / Resume from the console act on the app only (another player in focus is untouched).
- [ ] Skip while playing → next track named within ~1s; **no** flicker to Stopped on the console
      or the card during the skip (the ~200ms closed/empty transient is debounced).
- [ ] Skip while paused → next track **plays**.
- [ ] Pause in the app's own window → console shows Paused and, with recovery on, play resumes
      after ~1s (expected; documented). With recovery off it stays paused.

## 5. Volume and fade
- [ ] Pull the app's slider in Windows *Volume mixer* down (say 15%) and quit Edge. Start break
      music: the slider is back at 100% once it sounds, and the host's own Edge slider is untouched.
- [ ] Start a singer's song: the bed fades smoothly (no step, no early cut) over ~1.5s, pauses,
      then the mixer slider returns to 100% **with no audible blip**. The song start is
      delayed only by the fade.
- [ ] After the song, the bed resumes **where it stopped**, rising to 100%.
- [ ] Fade setting 0 → instant pause. Log has no "Could not set YouTube Music's level" lines.
- [ ] Restart Edge's audio (kill the `audio.mojom.AudioService` utility in Task Manager) and
      start or resume: the level still lands (the process lookup is redone).

## 6. Adverts (signed in **without** Premium)
- [ ] While "Video Ad" / "YouTube Ads …" plays: console names no track and the card is down.
      When the song returns, it is named again. Note any advert format that slipped through.

## 7. Idle prompt soak (45+ minutes)
- [ ] Leave break music playing untouched for 60+ min with recovery on. Note each time
      `paused by itself` appears, and whether `Pressed play … after it paused by itself` actually
      cleared "Are you still there?" (music audible again) or the app re-paused.

## 8. Kiosk / occlusion
- [ ] With the app window fully covered by the LocalScreen window (and with the app minimised),
      play 10+ minutes: no stutter, tracks advance, now-playing updates.
- [ ] Kill Edge mid-play and restart from the console: no "Restore pages?" bubble; one app window.
- [ ] On the kiosk (explorer replaced, if applicable), check the media session is found at all.
