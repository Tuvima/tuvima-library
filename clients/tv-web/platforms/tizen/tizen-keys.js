/*
 * Tizen remote keys. A classic script loaded before the shared client by package.ps1 (it is not part of the shared
 * index.html). Samsung TVs only deliver the media keys to a web app after it registers them. This file records the
 * Samsung codes for Back (Return), Rewind and Fast-forward for nav.js and registers the media keys. Where there is no
 * Tizen runtime (desktop Chrome, other platforms) it only records the codes.
 */
(function (global) {
  'use strict';

  // The Samsung remote's codes for the keys the player uses (the contract's tv-keys table): Return is Back, and the
  // Rewind and Fast-forward keys skip back and forward by the profile's seconds. nav.js reads this table when it loads.
  global.TV_PLATFORM_KEYS = {
    10009: 'back',       // Return
    412: 'rewind',       // MediaRewind
    417: 'forward',      // MediaFastForward
    415: 'play',         // MediaPlay
    19: 'pause',         // MediaPause
    413: 'stop',         // MediaStop
    10252: 'playpause'   // MediaPlayPause
  };

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
