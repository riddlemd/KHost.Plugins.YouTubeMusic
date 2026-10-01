// Sent to music.youtube.com through Chrome's "execute javascript" Apple Event, once per call, with
// the arguments spliced in at the bottom by PageScript. It must evaluate to a string: Chrome hands
// back a string as-is and anything else as "missing value".
//
// That event runs in an ISOLATED world. The DOM, <video> and navigator.mediaSession are shared, but
// the page's own objects are not: #movie_player's API (playVideo, getPlayerState, setVolume) and
// window._lact are invisible there. So `page` is re-run in the page's main world through a <script>
// element. The page enforces Trusted Types, so the script text goes through a policy created here,
// and carries the page's own nonce. Where that fails, `page` runs here, on the DOM alone.
//
// The idle keepalive (window._lact = Date.now()) is Pear Desktop's
// (https://github.com/pear-devs/pear-desktop, MIT, Copyright (c) th-ch), from its renderer.

(function (command, argument, appOnly) {
  'use strict';

  var RESULT_ATTRIBUTE = 'data-khost-youtube-music';

  // Runs in whichever world it is given; reads the page's API only where it can see it.
  function page(command, argument, resultAttribute) {
    var player = document.querySelector('#movie_player');
    var video = document.querySelector('#movie_player video') || document.querySelector('video');
    var hasApi = !!player && typeof player.getPlayerState === 'function';
    var world = typeof window.ytcfg === 'object' ? 'main' : 'isolated';

    // Its own copy: page is re-run from its source text, where nothing outside it exists.
    function isApp() {
      return window.matchMedia('(display-mode: standalone)').matches
        || window.matchMedia('(display-mode: minimal-ui)').matches;
    }

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
      // YouTube compares this against the clock to decide nobody is there. Only the main world
      // can set it; the isolated world gets its own window.
      if (world === 'main') window._lact = Date.now();
      var button = stillThereButton();
      if (button) button.click();
      return !!button;
    }

    function text(root, selector) {
      var element = root && root.querySelector(selector);
      return element ? (element.textContent || '').trim() : '';
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
        world: world,
        app: isApp(),
        player: !!player,
        state: playerState,
        paused: video ? video.paused : null,
        ad: !!player && player.classList.contains('ad-showing'),
        title: (metadata && metadata.title) || text(bar, '.title'),
        artist: (metadata && metadata.artist) || '',
        position: video ? video.currentTime : 0,
        duration: duration || 0,
        volume: hasApi ? player.getVolume() / 100 : (video ? video.volume : null),
        stillThere: !!stillThereButton()
      };
    }

    function done(ok) {
      return { page: true, world: world, app: isApp(), ok: !!ok };
    }

    var result;

    // Always answers, error or not: a missing answer makes the caller re-run the command on the
    // DOM alone, and a skip that threw after skipping would then skip twice.
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
          // The player keeps whole percent and re-applies it on every track; the element takes the
          // exact value, which a fade's quiet steps need (1% is only -40 dB).
          if (hasApi) player.setVolume(Math.round(level * 100));
          if (video) video.volume = level;
          result = done(hasApi || video);
          break;
        case 'keepAlive':
          result = done(true);
          result.dismissed = keepAlive();
          break;
        case 'navigate':
          location.assign(String(argument));
          result = done(true);
          break;
        default:
          result = { page: true, world: world, ok: false, error: 'unknown command ' + command };
      }
    } catch (e) {
      result = { page: true, world: world, ok: false, error: String((e && e.message) || e) };
    }

    var json = JSON.stringify(result);
    if (resultAttribute) document.documentElement.setAttribute(resultAttribute, json);
    return json;
  }

  // An --app window reports itself as standalone; a tab in the setup window does not.
  function isApp() {
    return window.matchMedia('(display-mode: standalone)').matches
      || window.matchMedia('(display-mode: minimal-ui)').matches;
  }

  if (location.hostname !== 'music.youtube.com' || (appOnly && !isApp()))
    return JSON.stringify({ page: false });

  var root = document.documentElement;

  try {
    var policy = window.__khostYouTubeMusicPolicy;
    if (!policy && window.trustedTypes)
      policy = window.__khostYouTubeMusicPolicy = window.trustedTypes.createPolicy(
        'khost-youtube-music', { createScript: function (source) { return source; } });

    var source = '(' + page.toString() + ')(' + JSON.stringify(command) + ',' + JSON.stringify(argument)
      + ',' + JSON.stringify(RESULT_ATTRIBUTE) + ');';
    var script = document.createElement('script');
    var nonced = document.querySelector('script[nonce]');
    if (nonced && nonced.nonce) script.nonce = nonced.nonce;
    script.textContent = policy ? policy.createScript(source) : source;

    root.removeAttribute(RESULT_ATTRIBUTE);
    (document.head || root).appendChild(script);
    script.remove();

    var reply = root.getAttribute(RESULT_ATTRIBUTE);
    root.removeAttribute(RESULT_ATTRIBUTE);
    if (reply) return reply;
  } catch (e) {
    // Falls through to the DOM-only answer below.
  }

  try {
    return page(command, argument, null);
  } catch (e) {
    return JSON.stringify({ page: true, ok: false, error: String((e && e.message) || e) });
  }
})/*ARGUMENTS*/;
