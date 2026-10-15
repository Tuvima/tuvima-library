// LG webOS remote key table. Classic script, loaded before the shared client in the packaged app only.
// The shared client (nav.js) already turns these codes into its own actions (Back leaves screens, then the app
// through webOS.platformBack / window.close; the media keys drive player.js). This file records the webOS codes in
// one place and exposes them for diagnostics. In a desktop browser it does nothing.
(function (global) {
  'use strict';

  var KEYS = { back: 461, play: 415, pause: 19, stop: 413, rewind: 412, fastForward: 417 };

  var isWebOS = !!(global.webOS || global.webOSSystem ||
    (global.navigator && /web0s|webos/i.test(global.navigator.userAgent || '')));

  global.TV_WEBOS = { isWebOS: isWebOS, keys: KEYS };
})(window);
