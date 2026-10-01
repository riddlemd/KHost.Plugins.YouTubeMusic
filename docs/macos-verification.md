# macOS verification checklist

## Verified here

macOS 26.6.2 (arm64), Safari 26.6.2, 2026-10-01. A scratch harness drove the real provider and the
real helper controller against a **test build** of the app (bundle id
`com.khost.youtube-music-helper-test`, so the real id's data store was never created), launched from
a scratch `bin/`, **signed out**, venue level 0.05, with a second socket connection sampling the
page's volume every 100ms and pulling it to silence above 0.08 (never tripped).

- **Launch**: Start to returning 1.47–1.63s (cold, plugin-launched, `--background`, at the
  playlist). The page was held at 0 until the plugin's first level, then rose in equal-decibel steps
  to 0.05. Highest level heard over the whole run: 0.05.
- **One Dock icon**: LaunchServices listed one Foreground app, *YouTube Music Test*, for the bundle;
  WebKit's own processes are UIElement (no Dock tile). No Google Chrome process was started.
- **Pause** (1.5s fade): 0.05 → 0.0001 in 16 equal-decibel steps, paused, level restored to 0.05;
  `PauseAsync` 1.61s. **Resume** returned in 0.01s and rose 0 → 0.05 over ~1.6s.
- **Skip**: from an advert ("Lynkuet Audio Ad", no track named while it ran) onto the next song.
- **Volume**: `SetVolumeAsync(0.03)` read back 0.03 from the page.
- **Hidden** (`hide`, window ordered out) for 130s: played on in real time (position 11.6s →
  131.6s), page visibility `hidden`, window not visible.
- **Host quit**: the harness exited without closing anything; the helper was gone within ~100ms,
  its socket removed, no crash report. (The first run crashed on that exit: the helper logged to
  stderr through `FileHandle`, which raises on a closed pipe. Fixed; logging is `write(2)` now.)
- **Relaunch**: a new host run launched a fresh copy and played (1.47s); the install step reported
  `Updated` for the rebuilt app and `Unchanged` when nothing had changed.
- **Opened by the user**: `open` on the app gave one process; a second `open` added none; running the
  executable directly with `--khost-stdio` printed `{"event":"busy"}` and exited 3. The host then
  drove that copy over the socket (Start 1.81s, list loaded into it held at silence, peak 0.05), and
  the copy **stayed up** when the host exited. Its socket is `srw-------`.
- **Dock click**: after `hide`, `open` on the running app (the reopen a Dock click sends) showed the
  window again.
- **Sign-in read**: the page reported `signedIn: false`; the setup status read *NotSignedIn*.

## For the owner

Use the real app (bundle id `com.khost.youtube-music-helper`) through KHost, with no account plugins
loaded. Its data store starts empty: the sign-in done in the earlier prototype lived under a
different bundle id and does not carry over.

### 1. Sign in
- [ ] Plugins page: the button reads **Sign in to YouTube Music**. Press it: the YouTube Music window
      comes up in front at music.youtube.com, with one *YouTube Music* Dock icon.
- [ ] Sign in with the Premium account (the Google sign-in opens in a *Sign in* window and lands back
      in the player). The button turns to *Set up YouTube Music again* within a few seconds.
- [ ] ⌘Q the app, restart KHost, press Play: it comes up still signed in, with no adverts.
- [ ] A private playlist / Liked Music (`list=LM`) plays.

### 2. Sixty minutes signed in, through KHost
- [ ] Break music on for 60+ minutes untouched, the window hidden (closed with its red button):
      no adverts, no "Are you still there?", no `paused by itself` in the log, no sign-out flash.
- [ ] Track changes are named on the screen, with no Stopped flicker between songs.
- [ ] The window fully covered by the LocalScreen window for 10+ minutes: tracks advance.

### 3. The Dock
- [ ] Exactly one *YouTube Music* icon while it plays; clicking it shows the player; the red close
      button hides it and the music plays on; ⌘Q quits it.
- [ ] Quit KHost: the app quits with it. Open the app from Finder first instead, then start KHost
      and Play: KHost drives that copy, and quitting KHost leaves it running.
- [ ] Whether the cold launch for Play stays behind the karaoke screen (it is ordered back and not
      activated; checked here only with no fullscreen window up).

### 4. Edges
- [ ] Sign out inside the app while break music plays: one flash, *YouTube Music: signed out —
      press Set up to sign in again*; the music keeps playing; the button reads *Sign in to YouTube
      Music*.
- [ ] ⌘Q the app mid-show and press Play: one new copy starts, behind, at the list.
- [ ] Ship check: the release zip (Windows target, made on any OS) contains `helper/YouTube
      Music.app`; installed on a Mac it plays, and `bin/khost.youtube-music/YouTube Music.app` appears.
- [ ] An Intel Mac (the app is universal; only arm64 was run here).
