# KHost.Plugins.YouTubeMusic

Break-music provider for [KHost](../KHost). It puts YouTube Music on between singers and fades it
out when one starts.

The plugin **drives YouTube Music in a browser on this machine; it does not carry the audio**:
the app installed in Microsoft Edge on Windows, the app installed in Google Chrome on macOS. The sound
comes out of the browser, so the host cannot route it to a screen or a Cast device, and
`RendersThroughHost` is false. There is no API key and no OAuth: the plugin uses the signed-in
page already on the machine.

**Windows and macOS.** On Linux the plugin loads, says so on the Plugins page, and plays nothing.
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
  volume lands there. When a singer starts, the bed fades down on an equal-decibel curve over
  *Fade out* milliseconds (default 1500), then pauses, then the level is put back for next time.
- **Playlist.** Paste a YouTube Music (or YouTube) playlist link. It is used only when the app
  has nothing loaded, because the plugin then opens the app straight at that list. After a singer
  the bed resumes where it stopped and is never restarted from the top. A single-track link is
  refused, since the bed would end after one song.
- **Shuffle cannot be set from outside.** Neither a link nor the media transport can turn on
  YouTube Music's shuffle. Turn it on in the app once and it stays on for that list.

## How it works on macOS

- **Google Chrome is required**, and runs on its own profile: `--user-data-dir` defaults to
  `~/Library/Application Support/KHost/youtube-music-chrome` (the same *profile folder* setting
  overrides it). The host's own Chrome, if open, is a separate process and is never scripted.
- **The window** is the YouTube Music app installed in that profile, launched with
  `--app-id=cinhimbnkkaeohfgghhklpknlkffjgod` (the same id Edge uses) through `open -n -g`, so
  Chrome is its own app rather than a child of KHost. Chrome draws an installed app's window in an
  *app shim* (`~/Applications/Chrome Apps.localized/YouTube Music.app`), which gives it a
  **YouTube Music** icon in the Dock, in ⌘-Tab and in Mission Control; clicking it raises the
  break-music window. A playlist goes in through `--app-launch-url-for-shortcuts-menu-item`, which
  keeps the window the installed app. Same kiosk switches as Windows, plus
  `--disable-renderer-backgrounding` and `--disable-background-timer-throttling`.
- **Before the app is installed** it still plays, in an anonymous `--app=` window. That window has
  no icon of its own: it sits under a second *Google Chrome* Dock icon, and clicking that icon
  opens a new Chrome window rather than showing the player. Installing the app is what fixes that,
  so the setup button says *Install the YouTube Music app* until the profile has it (the app's
  folder under `Default/Web Applications/Manifest Resources/`).
- **Control is Apple Events to that one Chrome process, by pid** (read from the profile's
  `SingletonLock`). Chrome's `execute javascript` runs a script in the page, and everything goes
  through it: play, pause, next, volume, now-playing (`navigator.mediaSession`), adverts (the
  player's `ad-showing` class) and the playlist (navigating the app window). Addressing Chrome by
  name, or JXA's `Application(pid)`, would reach whichever Chrome LaunchServices picks, which can
  be the host's own. The app shim draws the window but does not answer scripts: the browser
  process behind it does, and its pid is still the one in `SingletonLock`.
- That script runs in an *isolated world*, where the page's player API is invisible. It re-runs
  itself in the page's own world through a `<script>` element (a Trusted Types policy and the
  page's nonce); if YouTube ever closes that door it falls back to the DOM alone (`<video>`, the
  player bar's buttons), losing only the idle keepalive. It lives in one file,
  `src/KHost.Plugins.YouTubeMusic/Mac/youtube-music-page.js`.
- **Volume and fades are the page's own**, so nothing else on the Mac gets quieter. The player
  keeps whole percent, so each step also sets the `<video>` element's exact level; the fade uses
  the same equal-decibel curve as Windows.
- **Now-playing is polled once a second** (about 10ms a read): Chrome raises nothing a plugin can
  hear. Adverts are recognised by the player itself, not by their titles, so an advert titled
  like a song ("Movie | Official Trailer") is not named on the screen.
- **"Are you still there?"** is headed off by marking the page active every 5 minutes while it
  plays, and on every play (`window._lact = Date.now()`, from
  [Pear Desktop](https://github.com/pear-devs/pear-desktop), MIT, © th-ch). Play also presses the
  dialog's confirm button if it is up.

## One-time setup (Windows)

1. Enable the plugin on KHost's Plugins page and restart KHost.
2. Press **Set up YouTube Music**. Edge opens in the plugin's profile at music.youtube.com.
3. Sign in, then install the app from the *App available* icon in Edge's address bar. Edge has no
   way to install it programmatically. The button changes to *Set up YouTube Music again* once
   the plugin sees the install, and **Open YouTube Music** appears.
4. Choose **YouTube Music** as the venue's break-music provider.

## One-time setup (macOS)

1. Install Google Chrome. Enable the plugin on KHost's Plugins page and restart KHost.
2. Press **Set up YouTube Music**. Chrome opens on the plugin's profile at music.youtube.com, and
   macOS asks once whether KHost (or the terminal it was started from) may control Google
   Chrome. Allow it. If it was refused, the button reads *Allow KHost to control Google Chrome*
   and opens System Settings → Privacy & Security → Automation, where it can be turned on.
3. Sign in (optional, but see Premium below).
4. Install the app: click the install icon at the right of Chrome's address bar (*Install YouTube
   Music*), then **Next** and **Install**; or ⋮ → *Cast, save and share* → *Install page as app*.
   Chrome on macOS has no switch or policy a plugin can use to do this for you. The button changes
   from *Install the YouTube Music app* to *Set up YouTube Music again* once the plugin sees it.
5. **Quit that Chrome** (⌘Q while its window is in front). An app installed into a running Chrome
   stays under Chrome's own icon until it quits; the next Play comes up under the YouTube Music
   icon.
6. Choose **YouTube Music** as the venue's break-music provider. The first Play opens the app at
   the playlist.

The plugin turns on Chrome's *Allow JavaScript from Apple Events* in its own profile before
Chrome starts; nothing needs ticking by hand.

**YouTube Premium is strongly recommended.** Without it, adverts play in the room. The plugin
spots YouTube's generic advert entries ("Video Ad", artist "YouTube Ads …") and shows no
now-playing card while one runs. An advert titled with the advertiser's own name looks like a
song and will be named on Windows; on macOS the page itself says it is an advert.

## Settings

| Setting | Default | |
|---|---|---|
| Playlist | blank | A YouTube Music link. Blank resumes whatever the app has loaded. |
| Open YouTube Music if it is not already running | on | |
| Fade out when a singer starts (ms) | 1500 | 0 pauses at once. |
| Press play again when YouTube Music pauses by itself | on | See below. |
| Browser profile folder | blank | Blank uses `%LOCALAPPDATA%\KHost\youtube-music-profile` (Edge) or `~/Library/Application Support/KHost/youtube-music-chrome` (Chrome). |

## Kiosk notes (Windows)

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
- **Skip relies on YouTube Music's own media-session handlers** on Windows, which YouTube has
  broken before. On macOS it is the player's own `nextVideo()`, which YouTube can rename too: the
  page script is the part to fix when a YouTube Music update breaks control.
- **macOS: a skip during an advert skips the advert's song as well**, as the app's own Next does.
- **macOS: the plugin's Chrome still has a Google Chrome Dock icon of its own** beside the
  YouTube Music one, since the browser process behind the app is a second Chrome. Clicking it opens
  a new Chrome window on the plugin's profile, not the player; use the YouTube Music icon. Both
  quit together.
- **macOS: one YouTube Music app per Mac is the clear case.** If the host's own Chrome also has
  YouTube Music installed, Chrome names the second shim *YouTube Music 1* and both carry the same
  bundle id, so two YouTube Music icons can show at once.
- **A playlist change applies the next time the app has nothing loaded.** On Windows the media
  transport cannot navigate the app; on macOS an open app window with nothing playing is pointed
  at the list.
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
the WinRT projection for the media transport and is the Windows build; `net10.0` drives Chrome on
macOS.
A build copies this OS's target into a sibling KHost checkout's runtime `plugins/` folder when one
exists, as the other plugins do; stop that host first, since it loads plugins only at startup.

## Installing

Copy the Windows build output (entry dll, `manifest.json`, `WinRT.Runtime.dll`,
`Microsoft.Windows.SDK.NET.dll`, `.deps.json`) into a folder under KHost's `plugins/` directory,
enable it on the Plugins page, and restart KHost. On macOS copy the `net10.0` output (entry dll,
`manifest.json`, `.deps.json`) the same way. Windows verification steps are in
[docs/windows-verification.md](docs/windows-verification.md); what is left to check by hand on
macOS is in [docs/macos-verification.md](docs/macos-verification.md).

## Credits

Built on Windows' Global System Media Transport Controls and Core Audio session APIs, on
Chromium's per-app media sessions for installed web apps, and on Chrome's Apple Events scripting
on macOS. The idle keepalive is Pear Desktop's (MIT, Copyright (c) th-ch,
https://github.com/pear-devs/pear-desktop). YouTube Music is a trademark of Google.
