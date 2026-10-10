# KHost.Plugins.YouTubeMusic

Break-music provider for [KHost](https://github.com/riddlemd/KHost). It puts YouTube Music on between singers and fades it
out when one starts.

The plugin **drives YouTube Music on this machine; it does not carry the audio**: the app installed
in Microsoft Edge on Windows, and on macOS KHost's own **YouTube Music** app, a small WebKit window
the plugin ships and launches. The sound comes out of that app, so the host cannot route it to a
screen or a Cast device. There is no API key and no OAuth: the
plugin uses the signed-in page on the machine.

**Windows and macOS.** On Linux the plugin loads, says so on the Plugins page, and plays nothing; the
intended route there is the macOS design again, the same helper built on WebKitGTK.
Each platform is a controller behind one seam (`IYouTubeMusicController`); the provider's decisions
and the fade curve are shared.

## How it works on Windows

- **Its own Edge profile.** The plugin launches Edge with a dedicated `--user-data-dir` (default
  `%LOCALAPPDATA%\KHost\youtube-music-profile`, overridable in settings). It never touches the
  host's own browsing profile, which also keeps its volume and media controls separate.
- **Control and now-playing** go through Windows' media transport (SMTC). The app's session
  shows up as `music.youtube.com-<hash>!App`. The plugin matches that shape and never matches a
  plain `MSEdge` tab. Play, pause and next are sent to that session alone, unlike media keys,
  which go to whatever app has focus. Title and artist come from the same session.
- **Volume and fades** go through the Windows volume mixer (Core Audio, built-in COM interop, no
  package). The plugin sets the level of its own profile's Edge audio-service process. The host
  sets no level of its own, since every output runs through the room's mixer, so the app plays at
  full. Windows puts back whatever level it last kept for `msedge.exe` on each new audio session, so
  the plugin pushes full level whenever the app's media session appears. Fades are described under
  [Fades](#fades).
- **Playlist.** Paste a YouTube Music (or YouTube) playlist link. It is used only when the app
  has nothing loaded, because the plugin then opens the app straight at that list. After a singer
  the bed resumes where it stopped and is never restarted from the top. A single-track link is
  refused, since the bed would end after one song.
- **Shuffle cannot be set from outside.** Neither a link nor the media transport can turn on
  YouTube Music's shuffle. Turn it on in the app once and it stays on for that list.

## How it works on macOS

- **KHost's own YouTube Music app.** The plugin carries `YouTube Music.app` (bundle id
  `com.khost.youtube-music-helper`; Swift, universal, source in `helpers/macos/`). It is
  music.youtube.com in a WebKit view and nothing else: **one Dock icon**, no browser, no consent
  prompt. Clicking the Dock icon shows the window; closing the window hides it and the music plays
  on; ⌘Q quits it.
- **Installed into the host's shared `bin/`** at `<KHost>/bin/khost.youtube-music/YouTube Music.app`
  when the plugin loads, and only when its files differ. A running copy is never replaced under
  itself. It launches from there because a plugin update replaces the plugin folder whole, and a
  stable path keeps it the same app to the Dock.
- **Its own signed-in data store.** The Google sign-in lives in the app and nowhere else (paths
  under [Cleanup](#cleanup-macos)). It presents itself as this Mac's Safari, which is what lets
  Google's sign-in run in it.
- **Control** is a JSON-lines channel on the app's stdin and stdout. The plugin starts the app with
  `--khost-stdio`, so when the host ends the pipe closes and the app quits with it.
- **One copy per Mac.** The app also listens on an owner-only Unix socket in
  `~/Library/Caches/com.khost.youtube-music-helper/`, so a copy opened from the Dock or Finder can
  still be driven. Such a copy does **not** quit with the host. A second copy started while one
  runs exits. A home folder path too long for a socket gets none, and only a copy the plugin
  launched is driven.
- **Everything else is the page's own** (`helpers/macos/youtube-music-page.js`): play, pause, next,
  volume, now-playing, adverts and the sign-in state. Nothing else on the Mac gets quieter.
- **A cold launch is held at silence** (`--initial-level 0`) until the plugin's first level command,
  so an autoplaying list starts at the bottom of the fade-in.
- **"Are you still there?" is headed off** by marking the page active every 5 minutes while it
  plays, and on every play (`window._lact = Date.now()`, from
  [Pear Desktop](https://github.com/pear-devs/pear-desktop), MIT, © th-ch).
- **Signed out still plays**, with adverts. If the sign-in is lost while the venue wants music, the
  console flashes once and the setup button turns to *Sign in to YouTube Music*.

## Fades

Every fade runs on one equal-decibel curve, a step every 100ms, over the length set in KHost's
*App Settings → Break music → Fade* (0 cuts at once both ways). The plugin reads it afresh at every
fade, so a change applies from the next one.

- **Pause, and stop when a singer starts:** the level steps down to silence, the app pauses, and
  full level is put back so the next start is not silent. A cancelled fade still pauses and
  restores.
- **Start and resume:** the level is set to silence *before* play, play is pressed, then it steps
  up to full. A fresh launch starts sounding on its own, so it is cut to silence the moment it is
  heard and rises from there. A refused play puts full level back before the error reaches the
  host. Where the mixer has nothing to hold yet (Edge before its first sound), it plays with no
  rise, and full level is retried each second until the mixer has the session.
- **The rise does not hold the host:** start and resume return once play is pressed. A pause
  during the rise cuts it short and fades down from wherever it got; a resume during a fade-out
  lets the pause land, then rises from silence. One fade runs at a time, and the full-level push
  when the session appears stands aside for it.
- **"Are you still there?" recovery is not faded**: the level was never lowered, so play is
  pressed at it.

## One-time setup (Windows)

1. Enable the plugin on KHost's Plugins page and restart KHost.
2. Press **Set up YouTube Music**. Edge opens in the plugin's profile at music.youtube.com.
3. Sign in, then install the app from the *App available* icon in Edge's address bar. Edge has no
   way to install it programmatically. The button changes to *Set up YouTube Music again* once
   the plugin sees the install, and **Open YouTube Music** appears.
4. Choose **YouTube Music** as the venue's break-music provider.

## One-time setup (macOS)

1. Enable the plugin on KHost's Plugins page and restart KHost. The YouTube Music app is installed
   into KHost's `bin/` as the plugin loads; nothing else needs installing.
2. Press **Sign in to YouTube Music** (it reads *Set up YouTube Music again* once signed in). The
   YouTube Music window opens at music.youtube.com.
3. Sign in there, once. It stays signed in across quits, restarts and plugin updates.
4. Choose **YouTube Music** as the venue's break-music provider. The first Play opens the app
   behind the karaoke screen at the playlist.

The button reads *YouTube Music app missing from this plugin build* (and Play fails with
`KH-YTMUSIC-NO-HELPER`) only for a build made without the app; see [Building](#building).

**YouTube Premium is strongly recommended.** Without it, adverts play in the room. The plugin
spots YouTube's generic advert entries ("Video Ad", artist "YouTube Ads …") and shows no
now-playing card while one runs. An advert titled with the advertiser's own name looks like a
song and will be named on Windows; on macOS the page itself says it is an advert.

## Settings

| Setting | Default | |
|---|---|---|
| Playlist | blank | A YouTube Music link. Blank resumes whatever the app has loaded. |
| Open YouTube Music if it is not already running | on | |
| Press play again when YouTube Music pauses by itself | on | See below. |
| Edge profile folder | blank | Windows only. Blank uses `%LOCALAPPDATA%\KHost\youtube-music-profile`. macOS has nothing to set: the app keeps its own data store. |

A saved change applies at once, with no restart of KHost: the next start, launch or unexpected pause
reads the new value, and a new playlist link or profile folder re-checks the setup warnings.

## Kiosk notes (Windows)

- The app window can sit behind the karaoke screen. Edge is launched with
  `--disable-features=CalculateNativeWinOcclusion` and `--disable-backgrounding-occluded-windows`
  so it does not throttle the page as "hidden". It also gets
  `--autoplay-policy=no-user-gesture-required` so a fresh launch can start sounding.
- These switches apply only when they start the profile's browser process. If the profile's Edge
  was already open without them, close it once.
- `--hide-crash-restore-bubble` stops a killed kiosk from greeting the room with "Restore pages?".

## Limitations

- **No Cast.** The sound leaves Edge, or the YouTube Music app, on this machine.
- **The idle prompt is best-effort.** After a long stretch with nobody touching the page, YouTube
  Music pauses behind "Are you still there?". The plugin sees a pause it did not ask for (within
  ~50ms on Windows, ~1s on macOS) and presses play again a second later. A track ending and the next one buffering is not
  such a pause, and is held back rather than shown as Stopped. This also overrides a host who
  presses pause in the app's own window. Turn the setting off if that matters more.
- **Skip relies on YouTube Music's own media-session handlers** on Windows, which a YouTube
  update can break. On macOS it is the player's own `nextVideo()`, which YouTube can rename too: the
  page script is the part to fix when a YouTube Music update breaks control.
- **macOS: a skip during an advert skips the advert's song as well**, as the app's own Next does.
- **macOS: the sign-in state is a guess until the page has said.** Before the app has run this
  session, a data store that exists is taken as signed in.
- **A playlist change applies the next time the app has nothing loaded.** On Windows the media
  transport cannot navigate the app; on macOS an open window with nothing playing is pointed at the
  list.
- **Terms.** YouTube and YouTube Music terms are for personal, non-commercial use. Playing them in
  a venue is the venue's call, as is its public-performance licensing.

## Building

The plugin compiles against the `KHost.Abstractions` / `KHost.Common` packages, which are not on
nuget.org. Pack them into the local feed from a KHost checkout (`./build/pack-contracts.sh`, see
KHost's AGENTS.md).

```bash
dotnet build KHost.Plugins.YouTubeMusic.slnx
dotnet test tests/KHost.Plugins.YouTubeMusic.Tests
```

There are two builds, released separately; the catalog hands each host the one for its platform:

- **`net10.0`**, released as `--rid macos`. It carries the app in `helper/YouTube Music.app` beside
  the plugin's files.
- **`net10.0-windows10.0.19041.0`**, released as `--rid win`. It carries the WinRT projection for the
  media transport (embedded with CsWinRT, so it cannot clash with another plugin's WinRT runtime,
  as the Spotify provider's would). It builds **only on Windows**. A Windows release is built with
  `-p:SkipMacHelper=true`, so it has no `helper/`.

How the app gets into a build:

- **On a Mac with the Xcode command line tools** (`xcode-select --install`), it is compiled from
  `helpers/macos/` by `helpers/macos/build.sh`.
- **Anywhere else** (or with `-p:MacHelperFromSource=false`), the build ships the copy committed under
  `helpers/macos/prebuilt/`, and fails if `prebuilt/sources.sha256` no longer matches the sources.
  `-p:SkipMacHelper=true` builds without the app at all.
- **After changing anything in `helpers/macos/`**, run `helpers/macos/build.sh --prebuilt` on a Mac
  and commit `helpers/macos/prebuilt/` with the change. `.gitattributes` keeps that folder
  byte-exact on every checkout.

## Installing

From a host: Plugins → Available, then restart KHost. By hand: unzip the release for this platform
(`-macos` or `-win`) into its own folder under KHost's `plugins/`, enable it on the Plugins page,
and restart.

## Cleanup (macOS)

Quit the app first (⌘Q, or quit KHost if KHost started it). Everything it keeps is under its bundle
id, `com.khost.youtube-music-helper`:

- `~/Library/WebKit/com.khost.youtube-music-helper` — the website data (local storage, IndexedDB)
- `~/Library/HTTPStorages/com.khost.youtube-music-helper.binarycookies` — the cookies: the sign-in
- `~/Library/Caches/com.khost.youtube-music-helper` — WebKit's cache, and `khost.sock` while it runs
- `~/Library/Preferences/com.khost.youtube-music-helper.plist` — the window's position
  (`defaults delete com.khost.youtube-music-helper`)
- `$(getconf DARWIN_USER_CACHE_DIR)com.khost.youtube-music-helper` and
  `$(getconf DARWIN_USER_TEMP_DIR)com.khost.youtube-music-helper`
- `<KHost>/bin/khost.youtube-music/` — the installed app (put back on the next start while the
  plugin is enabled)

Deleting the first two signs it out.

## Credits

Built on Windows' Global System Media Transport Controls and Core Audio session APIs, on
Chromium's per-app media sessions for installed web apps, and on WebKit on macOS. The idle keepalive is Pear Desktop's (MIT, Copyright (c) th-ch,
https://github.com/pear-devs/pear-desktop). YouTube Music is a trademark of Google.
