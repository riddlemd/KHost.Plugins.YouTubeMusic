// Run by the helper app in music.youtube.com's own JavaScript world (WKContentWorld.page), once per
// command, through callAsyncJavaScript with `command` and `argument` passed as values. The page's
// own world is what makes #movie_player's API (playVideo, getPlayerState, setVolume) and
// window._lact reachable; the DOM-only branches are for a player not built yet.
// It must evaluate to a JSON string, which the helper hands to the plugin unchanged.
//
// The idle keepalive (window._lact = Date.now()) is Pear Desktop's
// (https://github.com/pear-devs/pear-desktop, MIT, Copyright (c) th-ch), from its renderer.

(function (command, argument) {
  'use strict';

  if (location.hostname !== 'music.youtube.com')
    return JSON.stringify({ page: false, url: location.href });

  var player = document.querySelector('#movie_player');
  var video = document.querySelector('#movie_player video') || document.querySelector('video');
  var hasApi = !!player && typeof player.getPlayerState === 'function';

  function visible(element) {
    return !!element && typeof element.getClientRects === 'function' && element.getClientRects().length > 0;
  }

  // "Are you still there?" — its confirm button resumes playback.
  function stillThereButton() {
    var dialog = document.querySelector('ytmusic-you-there-renderer');
    if (!visible(dialog)) return null;
    var button = dialog.querySelector('button, tp-yt-paper-button, yt-button-renderer');
    return visible(button) ? button : null;
  }

  function keepAlive() {
    // YouTube compares this against the clock to decide nobody is there.
    window._lact = Date.now();
    var button = stillThereButton();
    if (button) button.click();
    return !!button;
  }

  function text(root, selector) {
    var element = root && root.querySelector(selector);
    return element ? (element.textContent || '').trim() : '';
  }

  // ytcfg's own flag; null while the page has not built it, which is "unknown", not "signed out".
  function signedIn() {
    try {
      var value = window.ytcfg && typeof window.ytcfg.get === 'function' ? window.ytcfg.get('LOGGED_IN') : undefined;
      return typeof value === 'boolean' ? value : null;
    } catch (e) {
      return null;
    }
  }

  function state() {
    var metadata = navigator.mediaSession ? navigator.mediaSession.metadata : null;
    var bar = document.querySelector('ytmusic-player-bar');
    var playerState = hasApi
      ? player.getPlayerState()
      : (video && video.src ? (video.ended ? 0 : (video.paused ? 2 : 1)) : -1);
    var duration = hasApi ? player.getDuration() : (video && isFinite(video.duration) ? video.duration : 0);

    return {
      page: true,
      player: !!player,
      state: playerState,
      paused: video ? video.paused : null,
      ad: !!player && player.classList.contains('ad-showing'),
      title: (metadata && metadata.title) || text(bar, '.title'),
      artist: (metadata && metadata.artist) || '',
      position: video ? video.currentTime : 0,
      duration: duration || 0,
      volume: video ? video.volume : (hasApi ? player.getVolume() / 100 : null),
      stillThere: !!stillThereButton(),
      signedIn: signedIn(),
      levelHeld: window.__khostLevelHeld === true,
      url: location.href,
      visibility: document.visibilityState
    };
  }

  function done(ok) {
    return { page: true, ok: !!ok };
  }

  var result;

  // Always answers, error or not: a skip that threw after skipping must not be retried.
  try {
    switch (command) {
      case 'state':
        result = state();
        break;
      case 'play':
        keepAlive();
        if (hasApi) player.playVideo(); else if (video) video.play();
        result = done(hasApi || video);
        break;
      case 'pause':
        if (hasApi) player.pauseVideo(); else if (video) video.pause();
        result = done(hasApi || video);
        break;
      case 'next':
        var next = document.querySelector('ytmusic-player-bar .next-button');
        if (hasApi) player.nextVideo(); else if (next) next.click();
        result = done(hasApi || next);
        break;
      case 'level':
        var level = Math.min(1, Math.max(0, Number(argument) || 0));
        // Lets go of the level the helper held a fresh launch at (--initial-level).
        window.__khostLevelHeld = false;
        // The player keeps whole percent and re-applies it on every track; the element takes the
        // exact value, which a fade's quiet steps need (1% is only -40 dB).
        if (hasApi) {
          if (level > 0 && typeof player.isMuted === 'function' && player.isMuted()) player.unMute();
          player.setVolume(Math.round(level * 100));
        }
        if (video) video.volume = level;
        result = done(hasApi || video);
        break;
      case 'keepAlive':
        result = done(true);
        result.dismissed = keepAlive();
        break;
      default:
        result = { page: true, ok: false, error: 'unknown command ' + command };
    }
  } catch (e) {
    result = { page: true, ok: false, error: String((e && e.message) || e) };
  }

  return JSON.stringify(result);
})
