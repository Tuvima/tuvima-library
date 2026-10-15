/*
 * Tizen remote keys. A classic script loaded before the shared client by package.ps1 (it is not part of the shared
 * index.html). Samsung TVs only deliver the media keys to a web app after it registers them. Return (10009), the
 * media key codes and the arrows are already mapped by nav.js, so this file only registers keys. Where there is no
 * Tizen runtime (desktop Chrome, other platforms) it does nothing.
 */
(function (global) {
  'use strict';
  var tizen = global.tizen;
  if (!tizen || !tizen.tvinputdevice || typeof tizen.tvinputdevice.registerKey !== 'function') return;

  var KEYS = ['MediaPlayPause', 'MediaPlay', 'MediaPause', 'MediaStop', 'MediaFastForward', 'MediaRewind'];
  for (var i = 0; i < KEYS.length; i++) {
    try {
      tizen.tvinputdevice.registerKey(KEYS[i]);
    } catch (error) {
      // A key this model does not offer is skipped; the others still work.
    }
  }
})(window);
