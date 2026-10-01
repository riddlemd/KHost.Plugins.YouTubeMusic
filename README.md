# KHost.Plugins.YouTubeMusic

Break-music provider for [KHost](../KHost). It puts YouTube Music on between singers and fades it
out when one starts.

The plugin **drives the YouTube Music app installed in Microsoft Edge; it does not carry the
audio**. The sound comes out of Edge, so the host cannot route it to a screen or a Cast device,
and `RendersThroughHost` is false. There is no API key and no OAuth: the plugin uses the signed-in
app already on the machine.

**Windows only for now.** On macOS and Linux the plugin loads, says so on the Plugins page, and
plays nothing. The controller seam (`IYouTubeMusicController`) is where those backends will go.

## How it works

- **Its own Edge profile.** The plugin launches Edge with a dedicated `--user-data-dir` (default
  `%LOCALAPPDATA%\KHost\youtube-music-profile`, overridable in settings). It never touches the
  host's own browsing profile, which also keeps its volume and media controls separate.
- **Control and now-playing** go through Windows' media transport (SMTC). The app's session
  shows up as `music.youtube.com-<hash>!App`. The plugin matches that shape and never matches a
  plain `MSEdge` tab. Play, pause and next are sent to that session alone, unlike media keys,
  which go to whatever app has focus. Title and artist come from the same session.
- **Volume and fades** go through the Windows volume mixer (Core Audio, built-in COM interop, no
  package). The plugin sets the level of its own profile's Edge audio-service process. The venue
  volume lands there. When a singer starts, the bed fades down on an equal-decibel curve over
  *Fade out* milliseconds (default 1500), then pauses, then the level is put back for next time.
- **Playlist.** Paste a YouTube Music (or YouTube) playlist link. It is used only when the app
  has nothing loaded, because the plugin then opens the app straight at that list. After a singer
  the bed resumes where it stopped and is never restarted from the top. A single-track link is
  refused, since the bed would end after one song.
- **Shuffle cannot be set from outside.** Neither a link nor the media transport can turn on
  YouTube Music's shuffle. Turn it on in the app once and it stays on for that list.

## One-time setup

1. Enable the plugin on KHost's Plugins page and restart KHost.
2. Press **Set up YouTube Music**. Edge opens in the plugin's profile at music.youtube.com.
3. Sign in, then install the app from the *App available* icon in Edge's address bar. Edge has no
   way to install it programmatically. The button changes to *Set up YouTube Music again* once
   the plugin sees the install, and **Open YouTube Music** appears.
4. Choose **YouTube Music** as the venue's break-music provider.

**YouTube Premium is strongly recommended.** Without it, adverts play in the room. The plugin
spots YouTube's generic advert entries ("Video Ad", artist "YouTube Ads …") and shows no
now-playing card while one runs. An advert titled with the advertiser's own name looks like a
song and will be named.

## Settings

| Setting | Default | |
|---|---|---|
| Playlist | blank | A YouTube Music link. Blank resumes whatever the app has loaded. |
| Open YouTube Music if it is not already running | on | |
| Fade out when a singer starts (ms) | 1500 | 0 pauses at once. |
| Press play again when YouTube Music pauses by itself | on | See below. |
| Edge profile folder | blank | Blank uses `%LOCALAPPDATA%\KHost\youtube-music-profile`. |

## Kiosk notes

- The app window can sit behind the karaoke screen. Edge is launched with
  `--disable-features=CalculateNativeWinOcclusion` and `--disable-backgrounding-occluded-windows`
  so it does not throttle the page as "hidden". It also gets
  `--autoplay-policy=no-user-gesture-required` so a fresh launch can start sounding.
- These switches apply only when they start the profile's browser process. If the profile's Edge
  was already open without them, close it once.
- `--hide-crash-restore-bubble` stops a killed kiosk from greeting the room with "Restore pages?".

## Limitations

- **No Cast.** The sound leaves Edge on this machine.
- **The idle prompt is best-effort.** After a long stretch with nobody touching the page, YouTube
  Music pauses behind "Are you still there?". The plugin sees a pause it did not ask for (within
  ~50ms) and presses play again a second later. Whether that clears the prompt every time is
  still being soak-tested. This also overrides a host who presses pause in the app's own window.
  Turn the setting off if that matters more.
- **Skip relies on YouTube Music's own media-session handlers**, which YouTube has broken before.
- **A playlist change applies the next time the app opens with nothing loaded.** The media
  transport cannot navigate the app.
- **Terms.** YouTube and YouTube Music terms are for personal, non-commercial use. Playing them in
  a venue is the venue's call, as is its public-performance licensing.

## Building

The plugin compiles against the published `KHost.Abstractions` / `KHost.Common` packages. While
they are unreleased, pack them into the local feed from a KHost checkout
(`./build/pack-contracts.sh`, see KHost's AGENTS.md).

```bash
dotnet build KHost.Plugins.YouTubeMusic.slnx
dotnet test tests/KHost.Plugins.YouTubeMusic.Tests
```

Both targets build on any OS (`EnableWindowsTargeting`). `net10.0-windows10.0.19041.0` is the one
that works. It carries the WinRT projection for the media transport, so ship that build.
`-p:DeployToKHost=true` copies it into a sibling KHost checkout's runtime `plugins/` folder.

## Installing

Copy the Windows build output (entry dll, `manifest.json`, `WinRT.Runtime.dll`,
`Microsoft.Windows.SDK.NET.dll`, `.deps.json`) into a folder under KHost's `plugins/` directory,
enable it on the Plugins page, and restart KHost. Windows verification steps are in
[docs/windows-verification.md](docs/windows-verification.md).

## Credits

Built on Windows' Global System Media Transport Controls and Core Audio session APIs, and on
Chromium's per-app media sessions for installed web apps. YouTube Music is a trademark of Google.
