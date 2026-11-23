// LG webOS remote key table. Classic script, loaded before the shared client in the packaged app only.
// The shared client (nav.js) turns these codes into its own actions (Back closes the open side panel, then hides the
// player's overlay, then leaves screens and the app through webOS.platformBack / window.close; Rewind and Fast-forward
// skip back and forward by the profile's seconds). This file records the webOS codes in one place, hands them to nav.js
// (which reads global.TV_PLATFORM_KEYS when it loads) and exposes them for diagnostics.
(function (global) {
  'use strict';

  var KEYS = { back: 461, play: 415, pause: 19, stop: 413, rewind: 412, fastForward: 417 };

  var isWebOS = !!(global.webOS || global.webOSSystem ||
    (global.navigator && /web0s|webos/i.test(global.navigator.userAgent || '')));

  global.TV_PLATFORM_KEYS = {
    461: 'back',       // Back
    412: 'rewind',     // Rewind
    417: 'forward',    // Fast-forward
    415: 'play',       // Play
    19: 'pause',       // Pause
    413: 'stop'        // Stop
  };

  global.TV_WEBOS = { isWebOS: isWebOS, keys: KEYS };
})(window);
