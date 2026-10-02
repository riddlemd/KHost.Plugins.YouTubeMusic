# KHost.Plugins.YouTubeMusic

Break-music provider for [KHost](../KHost). It puts YouTube Music on between singers and fades it
out when one starts.

The plugin **drives YouTube Music on this machine; it does not carry the audio**: the app installed
in Microsoft Edge on Windows, and on macOS KHost's own **YouTube Music** app, a small WebKit window
the plugin ships and launches. The sound comes out of that app, so the host cannot route it to a
screen or a Cast device, and `RendersThroughHost` is false. There is no API key and no OAuth: the
plugin uses the signed-in page on the machine.

**Windows and macOS.** On Linux the plugin loads, says so on the Plugins page, and plays nothing; the
intended route there is the macOS design again, the same helper built on WebKitGTK.
Each platform is a controller behind one seam (`IYouTubeMusicController`); what the reads mean
(skip gaps, adverts, unasked pauses), the fade curve and every decision the provider makes are
shared.

## How it works on Windows

- **Its own Edge profile.** The plugin launches Edge with a dedicated `--user-data-dir` (default
  `%LOCALAPPDATA%\KHost\youtube-music-profile`, overridable in settings). It never touches the
  host's own browsing profile, which also keeps its volume and media controls separate.
- **Control and now-playing** go through Windows' media transport (SMTC). The app's session
  shows up as `music.youtube.com-<hash>!App`. The plugin matches that shape and never matches a
  plain `MSEdge` tab. Play, pause and next are sent to that session alone, unlike media keys,
  which go to whatever app has focus. Title and artist come from the same session.
- **Volume and fades** go through the Windows volume mixer (Core Audio, built-in COM interop, no
  package). The plugin sets the level of its own profile's Edge audio-service process. The venue
  volume lands there. Fades are described under [Fades](#fades).
- **Playlist.** Paste a YouTube Music (or YouTube) playlist link. It is used only when the app
  has nothing loaded, because the plugin then opens the app straight at that list. After a singer
  the bed resumes where it stopped and is never restarted from the top. A single-track link is
  refused, since the bed would end after one song.
- **Shuffle cannot be set from outside.** Neither a link nor the media transport can turn on
  YouTube Music's shuffle. Turn it on in the app once and it stays on for that list.

## How it works on macOS

- **KHost's own YouTube Music app.** The plugin carries `YouTube Music.app` (bundle id
  `com.khost.youtube-music-helper`; Swift, universal arm64 + x86_64, ad-hoc signed; source in
  `helpers/macos/`). It is music.youtube.com in a WebKit view and nothing else: **one Dock icon**
  named *YouTube Music*, no browser, no second Chrome, no consent prompt. Clicking the Dock icon
  shows the window; closing the window hides it and the music plays on; ⌘Q quits it. Its icon is
  never shipped: it is fetched at runtime from music.youtube.com's own web app manifest (the way a
  browser's "install as app" does), cached under its `Caches/` folder, and written onto the
  installed bundle with `NSWorkspace.setIcon` so Finder and the Dock show it before it has run.
- **Installed into the host's shared `bin/`** at
  `<KHost>/bin/khost.youtube-music/YouTube Music.app` (`IHostDirectories.BinDirectory`, a folder
  distinctly this plugin's). It is copied there when the plugin loads, and only when its files
  differ from what is there; a running copy is never replaced under itself (the next start after it
  quits picks the update up). It launches from there rather than from the plugin folder because a
  plugin update replaces that folder whole, and a stable path keeps it the same app to the Dock.
- **Its own signed-in data store.** WebKit keeps a persistent store per bundle id, so the Google
  sign-in lives in the app and nowhere else (paths under [Cleanup](#cleanup-macos)). It presents
  itself as this Mac's Safari (the version read from `/Applications/Safari.app`), which is what lets
  Google's sign-in run in it; sign-in popups open in a window of their own and land back in the
  player. App Nap is held off for as long as it runs, so a hidden window keeps time.
- **The control channel is the app's stdin and stdout**, one JSON object per line each way
  (`state`, `play`, `pause`, `next`, `level`, `keepAlive`, `load`, `show`, `hide`, `quit`).
  The plugin starts the app's executable itself with `--khost-stdio`, so the pipe *is* the lifetime:
  when the host ends, however it ends, the pipe closes and the app quits with it. There is no port
  and no token.
- **One copy per Mac.** The app also listens on a Unix socket,
  `~/Library/Caches/com.khost.youtube-music-helper/khost.sock` (owner-only, and the peer's uid is
  checked), so a copy the host opened from the Dock or Finder can still be driven: the plugin looks
  there before launching, and drives that copy instead. Such a copy says it was opened by the user
  and does **not** quit with the host. A second copy started while one runs says `busy` and exits;
  opened by hand, it brings the running one forward. A home folder whose path makes the socket longer
  than macOS allows (104 bytes) gets no socket, and only a copy the plugin launched is driven.
- **Everything else is the page's own**, run in the page's JavaScript world from
  `helpers/macos/youtube-music-page.js`: play, pause and next through the player's API, volume (the
  player's whole percent plus the `<video>` element's exact level, so the fade's quiet steps land),
  now-playing from `navigator.mediaSession`, adverts from the player's `ad-showing` class, the
  sign-in from the page's own `LOGGED_IN` flag. Nothing else on the Mac gets quieter.
- **A cold launch is held at silence.** The plugin launches the app behind the karaoke screen at the
  playlist with `--initial-level 0`: every media element on the page is held at that level until the
  plugin's first level command, so a list that autoplays starts at the bottom of the fade-in rather
  than at the player's own volume. Pointing a copy that is already up at the list holds it the same way.
- **Now-playing is polled once a second.** "Are you still there?" is headed off by marking the page
  active every 5 minutes while it plays, and on every play (`window._lact = Date.now()`, from
  [Pear Desktop](https://github.com/pear-devs/pear-desktop), MIT, © th-ch); play also presses the
  dialog's confirm button if it is up.
- **Signed out still plays**, with adverts. If the sign-in is lost while the venue wants music, the
  console flashes once — *YouTube Music: signed out — press "Sign in to YouTube Music" on the Plugins
  page* — and the music carries on; the setup button turns to *Sign in to YouTube Music*.
- **A sign-in that strands the player is handed back.** YouTube Music's own *Sign in* runs Google's
  sign-in in the player itself, and Google can finish it on a Google page (myaccount.google.com, say)
  without the trip through www.youtube.com that signs YouTube in. When the player finishes loading
  such a page, the app loads YouTube Music's own sign-in entry, which completes with no prompt while
  the Google session is fresh and lands back on music.youtube.com. If that strands too, it goes to
  the home page and tries no more.

## Fades

Every fade runs on one equal-decibel curve, a step every 100ms, over the *Fade length* setting
(default 1500ms; 0 cuts at once both ways). The host passes no fade length to pause or resume, so
the plugin applies its own setting to all of them.

- **Pause, and stop when a singer starts:** the level steps down to silence, the app pauses, and
  the venue level is put back so the next start is not silent. A cancelled fade still pauses and
  restores.
- **Start and resume:** the level is set to silence *before* play, play is pressed, then it steps
  up to the venue level. A fresh launch starts sounding on its own, so it is cut to silence the
  moment it is heard and rises from there. A refused play puts the venue level back before the
  error reaches the host. Where the mixer has nothing to hold yet (Edge before its first sound),
  it plays at the venue level with no rise.
- **The rise does not hold the host:** start and resume return once play is pressed. A pause
  during the rise cuts it short and fades down from wherever it got; a resume during a fade-out
  lets the pause land, then rises from silence. One fade runs at a time. A venue volume change
  during a fade is where that fade ends (or what the pause restores).
- **"Are you still there?" recovery is not faded**: the level was never lowered, so play is
  pressed at it, as before.

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
| Fade length (ms) | 1500 | Down on pause and when a singer starts, up on play and resume. 0 cuts at once. Stored as `fadeMilliseconds`. |
| Press play again when YouTube Music pauses by itself | on | See below. |
| Edge profile folder | blank | Windows only. Blank uses `%LOCALAPPDATA%\KHost\youtube-music-profile`. macOS has nothing to set: the app keeps its own data store. |

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
- **Skip relies on YouTube Music's own media-session handlers** on Windows, which YouTube has
  broken before. On macOS it is the player's own `nextVideo()`, which YouTube can rename too: the
  page script is the part to fix when a YouTube Music update breaks control.
- **macOS: a skip during an advert skips the advert's song as well**, as the app's own Next does.
- **macOS: the sign-in state is a guess until the page has said.** Before the app has run this
  session, a data store that exists is taken as signed in.
- **macOS: the app has the generic app icon.** It carries no YouTube artwork.
- **A playlist change applies the next time the app has nothing loaded.** On Windows the media
  transport cannot navigate the app; on macOS an open window with nothing playing is pointed at the
  list.
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

Both targets build on any OS (`EnableWindowsTargeting`). `net10.0-windows10.0.19041.0` carries
the WinRT projection for the media transport and is the Windows build; `net10.0` is the rest.
**Both carry the macOS app**, in `helper/YouTube Music.app` beside the plugin's files, because the
blank-Rid release a Mac installs is the Windows target.

- **On a Mac with the Xcode command line tools** (`xcode-select --install`), the build compiles the
  app from `helpers/macos/` through `helpers/macos/build.sh` (`xcrun swiftc` for arm64 and x86_64,
  `lipo`, an ad-hoc `codesign`), once per target framework and only when the sources changed.
- **Anywhere else** (or with `-p:MacHelperFromSource=false`), it ships the copy committed under
  `helpers/macos/prebuilt/`, and only while `helpers/macos/prebuilt/sources.sha256` still matches the
  sources; otherwise the build fails rather than ship a stale app. `-p:SkipMacHelper=true` builds
  without the app at all, for a build that will not play on a Mac.
- **After changing anything in `helpers/macos/`**, run `helpers/macos/build.sh --prebuilt` on a Mac
  and commit `helpers/macos/prebuilt/` with the change. The Swift build is reproducible, so a
  rebuild from unchanged sources is byte-identical. `PrebuiltHelperTests` fails until the stamp
  matches, so a release made on Windows can never carry an app older than its sources.
- `.gitattributes` keeps `helpers/macos/` byte-exact on every checkout; a CRLF conversion would
  fail the stamp.

The built app is about 400 KB. A build copies this OS's target into a sibling KHost checkout's
runtime `plugins/` folder when one exists, as the other plugins do; stop that host first, since it
loads plugins only at startup.

## Installing

Copy the Windows build output (entry dll, `manifest.json`, `WinRT.Runtime.dll`,
`Microsoft.Windows.SDK.NET.dll`, `.deps.json`, `helper/`) into a folder under KHost's `plugins/`
directory, enable it on the Plugins page, and restart KHost; the same folder runs on a Mac.

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
