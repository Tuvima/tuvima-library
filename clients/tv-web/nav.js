/*
 * Tuvima TV web client - remote-control input.
 *
 * Maps the keys of a TV remote (including Tizen and webOS codes) to a few actions, and moves focus between
 * elements marked `.focusable` by direction (spatial navigation). The focused element always shows a ring.
 */
(function (global) {
  'use strict';

  var TV = (global.TV = global.TV || {});

  // Key codes. Standard arrows and Enter work everywhere; the rest are the platform back and media keys.
  var KEY_ACTIONS = {
    37: 'left', 38: 'up', 39: 'right', 40: 'down',
    13: 'enter', 29443: 'enter',                        // Enter; Samsung OK on older remotes
    8: 'back', 27: 'back', 166: 'back',                 // Backspace, Escape, Browser Back (browsers and emulators)
    10009: 'back',                                      // Tizen Return
    461: 'back',                                        // webOS Back
    415: 'play', 19: 'pause', 413: 'stop',              // Tizen and webOS media keys
    10252: 'playpause', 179: 'playpause',               // Tizen Play/Pause, standard Media Play/Pause
    417: 'forward', 412: 'rewind'
  };

  var KEY_NAMES = {
    ArrowLeft: 'left', ArrowUp: 'up', ArrowRight: 'right', ArrowDown: 'down',
    Enter: 'enter', Escape: 'back', GoBack: 'back', BrowserBack: 'back', XF86Back: 'back',
    MediaPlay: 'play', MediaPause: 'pause', MediaPlayPause: 'playpause', MediaStop: 'stop',
    MediaFastForward: 'forward', MediaRewind: 'rewind'
  };

  var keyHandler = null;
  var backStack = [];
  var scope = null;

  function actionFor(event) {
    if (event.key && KEY_NAMES[event.key]) { return KEY_NAMES[event.key]; }
    return KEY_ACTIONS[event.keyCode] || null;
  }

  function isTextInput(element) {
    return !!element && element.tagName === 'INPUT' && /^(text|search|url|password|number|tel|email)$/i.test(element.type || 'text');
  }

  function visible(element) {
    if (!element || element.disabled || element.hasAttribute('data-disabled')) { return false; }
    var rect = element.getBoundingClientRect();
    if (rect.width === 0 && rect.height === 0) { return false; }
    return global.getComputedStyle(element).visibility !== 'hidden';
  }

  function candidates() {
    var root = scope || document;
    return Array.prototype.filter.call(root.querySelectorAll('.focusable'), visible);
  }

  function centre(rect) {
    return { x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 };
  }

  // Picks the element in `direction` with the smallest weighted distance; the sideways offset counts double so
  // the focus prefers staying in its row or column.
  function findNext(from, direction) {
    var origin = from.getBoundingClientRect();
    var o = centre(origin);
    var best = null;
    var bestScore = Infinity;
    candidates().forEach(function (element) {
      if (element === from) { return; }
      var rect = element.getBoundingClientRect();
      var c = centre(rect);
      var dx = c.x - o.x;
      var dy = c.y - o.y;
      var primary;
      var secondary;
      if (direction === 'left') {
        if (rect.right > origin.left + 1 && c.x >= o.x) { return; }
        primary = -dx; secondary = Math.abs(dy);
      } else if (direction === 'right') {
        if (rect.left < origin.right - 1 && c.x <= o.x) { return; }
        primary = dx; secondary = Math.abs(dy);
      } else if (direction === 'up') {
        if (rect.bottom > origin.top + 1 && c.y >= o.y) { return; }
        primary = -dy; secondary = Math.abs(dx);
      } else {
        if (rect.top < origin.bottom - 1 && c.y <= o.y) { return; }
        primary = dy; secondary = Math.abs(dx);
      }
      if (primary <= 0) { return; }
      var score = primary + secondary * 2;
      if (score < bestScore) { best = element; bestScore = score; }
    });
    return best;
  }

  function focus(element) {
    if (!element) { return false; }
    var current = document.querySelector('.focusable.focused');
    if (current && current !== element) { current.classList.remove('focused'); }
    element.classList.add('focused');
    try {
      element.focus({ preventScroll: true });
    } catch (e) {
      element.focus();
    }
    if (typeof element.scrollIntoView === 'function') {
      try {
        element.scrollIntoView({ block: 'nearest', inline: 'nearest' });
      } catch (e) {
        element.scrollIntoView(false);
      }
    }
    if (typeof element.tvFocused === 'function') { element.tvFocused(); }
    return true;
  }

  function current() {
    var active = document.activeElement;
    if (active && active.classList && active.classList.contains('focusable') && visible(active)) { return active; }
    return document.querySelector('.focusable.focused');
  }

  function move(direction) {
    var from = current();
    if (!from || !visible(from)) {
      return focus(candidates()[0]);
    }
    if (typeof from.tvKey === 'function' && from.tvKey(direction) === true) { return true; }
    var next = findNext(from, direction);
    if (next) { return focus(next); }
    return false;
  }

  function activate() {
    var from = current();
    if (!from) { return false; }
    if (typeof from.tvKey === 'function' && from.tvKey('enter') === true) { return true; }
    if (isTextInput(from)) { return false; }
    from.click();
    return true;
  }

  function goBack() {
    for (var i = backStack.length - 1; i >= 0; i--) {
      if (backStack[i]() !== false) { return true; }
    }
    return false;
  }

  function onKeyDown(event) {
    var action = actionFor(event);
    if (!action) { return; }
    var from = document.activeElement;
    var typing = isTextInput(from);

    // Inside a text box the arrows and Backspace edit text; Up and Down still leave it, and Back leaves an empty box.
    if (typing) {
      if (action === 'left' || action === 'right') { return; }
      if (action === 'back' && (event.keyCode === 8 || event.key === 'Backspace') && from.value) { return; }
      if (action === 'enter') {
        if (typeof from.tvKey === 'function' && from.tvKey('enter') === true) { event.preventDefault(); }
        return;
      }
    }

    if (keyHandler && keyHandler(action, event) === true) {
      event.preventDefault();
      return;
    }

    var handled = false;
    if (action === 'left' || action === 'right' || action === 'up' || action === 'down') {
      handled = move(action);
      if (typing && handled) { from.blur(); }
      handled = true; // arrows never scroll the page themselves
    } else if (action === 'enter') {
      handled = activate();
    } else if (action === 'back') {
      // Unhandled Back is left to the platform (it may leave the app from the first screen).
      handled = goBack();
    }
    if (handled) { event.preventDefault(); }
  }

  global.document.addEventListener('keydown', onKeyDown, true);

  /** Small element builder shared by the screens: tag, class names, optional text. Text is always set as text, never as HTML. */
  TV.el = function (tag, className, text) {
    var element = document.createElement(tag);
    if (className) { element.className = className; }
    if (text !== undefined && text !== null) { element.textContent = text; }
    return element;
  };

  TV.nav = {
    /** Limits movement to the elements inside `container` (a dialog, for example). Pass null to use the whole page. */
    setScope: function (container) { scope = container || null; },
    scope: function () { return scope; },
    focus: focus,
    current: current,
    first: function (container) {
      var root = container || scope || document;
      var found = Array.prototype.filter.call(root.querySelectorAll('.focusable'), visible)[0];
      return focus(found);
    },
    /** Remembers which element has the focus, by its data-key, so a screen can restore it after it redraws. */
    remember: function () {
      var element = current();
      return element ? element.getAttribute('data-key') : null;
    },
    restore: function (container, key) {
      var root = container || document;
      if (key) {
        var match = Array.prototype.filter.call(root.querySelectorAll('.focusable'), function (element) {
          return element.getAttribute('data-key') === key && visible(element);
        })[0];
        if (match) { return focus(match); }
      }
      return TV.nav.first(root);
    },
    /** While set, the handler sees every action first; return true to say it was used. Used by the player. */
    setKeyHandler: function (handler) { keyHandler = handler || null; },
    /** Back handlers run newest first; a handler returns false to pass the key down. */
    pushBack: function (handler) { backStack.push(handler); return handler; },
    popBack: function (handler) {
      var index = backStack.lastIndexOf(handler);
      if (index >= 0) { backStack.splice(index, 1); }
    },
    clearBack: function () { backStack = []; },
    keyActions: KEY_ACTIONS,
    keyNames: KEY_NAMES,
    actionFor: actionFor
  };
})(window);
