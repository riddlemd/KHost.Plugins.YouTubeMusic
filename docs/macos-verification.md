# macOS verification checklist

Verified on this Mac (macOS 26.6.2, Chrome 154.0.8037.58, signed out, 2026-10-01) through a
scratch harness driving the real provider and controller, with the host's own Chrome open the
whole time:

- Setup button opened Chrome on a fresh scratch profile in 41ms; status went *not set up* →
  *Ready*, and *Open YouTube Music* appeared.
- Start with the setup window still open: the app window opened at the playlist and played
  (1.06s from Start to returning), and the setup window's YouTube Music tab was not driven.
- An advert ("Ebenezer | Official Trailer 2", artist "Paramount Pictures") was recognised from the
  player (`ad-showing`); no track was named for its whole length.
- Pause seen as Paused 64ms after the call, resume seen as Playing in 81ms (both mid-advert); skip
  returned on the next track in 234ms.
- Fade (1.5s setting) from venue level 0.30: equal-decibel steps down to 0.0005, paused at 1.73s,
  level restored to 0.30 afterwards; `StopAsync` took 1.93s. Restart resumed where it stopped.
- Minimised for 150s: the page reported `hidden` and played on in real time (0:07 → 2:38).
- Every Apple Event went to the plugin's Chrome by pid; the host's own Chrome was never addressed.

The app's own Dock icon (Chrome 154.0.8037.58, 2026-10-01, screen locked, so read through
LaunchServices, Accessibility and Apple Events rather than screenshots; throwaway profiles, muted
or at 3%):

- Before: an `--app=` window on a second profile adds a second, identical *Google Chrome* Dock
  item. A Dock click on it (activate + reopen) opened a **New Tab** window on that profile and
  left the player where it was. The pinned Chrome item stays bound to the host's own Chrome.
- Install from the address-bar icon is two steps in this version (*Next*, then *Install*). It
  wrote `Default/Web Applications/Manifest Resources/cinhimbnkkaeohfgghhklpknlkffjgod` (absent
  from a profile that never installed it, though Chrome preinstalls seven others there) and the
  shim `~/Applications/Chrome Apps.localized/YouTube Music.app` (bundle id
  `com.google.Chrome.app.cinhimbnkkaeohfgghhklpknlkffjgod`, pointing at that user-data-dir). The
  tab became an app window **with no shim** until that Chrome was quit.
- A cold `--app-id` launch through `open -n -g` brought up the shim within 1s: a *YouTube Music*
  Dock item, type Foreground (⌘-Tab), owning the window in Accessibility. A Dock click on it
  raised YouTube Music and opened nothing; its Window menu lists the player. The profile's own
  *Google Chrome* item is still there and still opens a New Tab window.
- `--app-launch-url-for-shortcuts-menu-item=<url>` opened the app at that URL.
- Pid targeting is unchanged: Apple Events to the `SingletonLock` pid counted the app window and
  ran the page script in it. Through the real provider: Start 1.02s, pause 11ms, resume 68ms,
  skip 214ms onto the next track, Stop with a 1.5s fade 1.91s, restart resumed in 101ms.
- An uninstalled profile reads *Install the YouTube Music app*, keeps *Open* visible, and still
  plays through `--app=`. Both `--app=` and `--app-id` cold launches take focus despite `-g`.
- A second user-data-dir installing the same app got `YouTube Music 1.app`, not a collision.

Not checked here, for the owner. Use a test KHost **with no account plugins loaded**.

## 1. Consent from KHost itself
This Mac's terminal already had Automation consent for Chrome, so the prompt was never seen.
- [ ] On an account (or after `tccutil reset AppleEvents`) where KHost has never been asked,
      press *Set up YouTube Music*: macOS asks whether **the app KHost was started from** (the
      terminal for `dotnet run`, KHost itself if packaged) may control Google Chrome. Allow → log
      says `KHost may control Google Chrome`.
- [ ] Refuse instead: the button turns to *Allow KHost to control Google Chrome*, pressing it opens
      System Settings → Automation, and Play fails with `KH-YTMUSIC-NOT-PERMITTED`.
- [ ] A packaged KHost signed with the hardened runtime needs the
      `com.apple.security.automation.apple-events` entitlement and an
      `NSAppleEventsUsageDescription`; without them macOS refuses silently with no prompt.

## 2. Signed in
- [ ] Sign in with a Premium account in the setup window, close it, restart KHost: Play starts the
      playlist with no adverts, and the sign-in survives a Chrome quit.
- [ ] A private playlist / Liked Music (`list=LM`) plays.

## 3. Long run
- [ ] 60+ minutes untouched: no "Are you still there?"; the log shows no `paused by itself`. (The
      5-minute keepalive was not reached in the 4-minute harness run; the same command ran on
      every play and was seen to land.)
- [ ] App window fully covered by the LocalScreen window (not just minimised) for 10+ minutes:
      tracks advance and are named.

## 4. The Dock icon
- [ ] On the live profile, press *Install the YouTube Music app*, install from the address bar,
      quit that Chrome, press Play: a **YouTube Music** icon appears and clicking it shows the
      player over the karaoke screen; the button reads *Set up YouTube Music again*.
- [ ] Whether that window comes up over the LocalScreen window on a cold launch (focus was taken
      from Finder here, as it was with the old `--app=` window).

## 5. Edges
- [ ] Quit the plugin's Chrome mid-show and press Play: one new app window, no "Restore pages?".
- [ ] With the app window open on the home page (nothing loaded), Play: that window is navigated
      to the playlist, and no second app window opens.
- [ ] Ship check: the catalog's blank-Rid release is the Windows target. Install that zip on a Mac
      and confirm the plugin loads and plays (the macOS controller is compiled into both targets).
