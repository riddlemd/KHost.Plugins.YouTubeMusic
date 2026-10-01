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

## 4. Edges
- [ ] Quit the plugin's Chrome mid-show and press Play: one new app window, no "Restore pages?".
- [ ] With the app window open on the home page (nothing loaded), Play: that window is navigated
      to the playlist, and no second app window opens.
- [ ] Ship check: the catalog's blank-Rid release is the Windows target. Install that zip on a Mac
      and confirm the plugin loads and plays (the macOS controller is compiled into both targets).
