// app.js — Global JavaScript helpers for the Dashboard

/**
 * Ensures the dark theme class is applied to <body>.
 * Called from MainLayout on first render. Light mode has been removed.
 */
window.setThemeClass = function () {
    document.body.classList.add('app-dark');
};

/**
 * Pop hook for an overlay that owns one history entry (the phone navigation menu, js/phone-nav-menu.js). app.js loads
 * before blazor.web.js, so this listener is registered before Blazor's own popstate listener and can swallow the pop that
 * removes the overlay's entry (stopImmediatePropagation); Blazor then never treats it as a navigation.
 */
window.tuvimaPopHook = null;
window.addEventListener('popstate', function (event) {
    if (window.tuvimaPopHook) window.tuvimaPopHook(event);
});

window.tuvimaPrefersReducedMotion = function () {
    return !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
};

/**
 * Touch swipe for the Home hero carousel. A horizontal swipe of more than 48 px with less vertical travel moves to
 * the next or previous featured item through the carousel's own buttons logic; rotation is held while a finger is down.
 * Mouse and pen input never reach this handler, so desktop behaviour is unchanged. The swipe adds no animation, so it
 * needs no reduced-motion variant.
 */
window.registerHeroSwipe = function (el, dotnet) {
    if (!el || el.__heroSwipe) return;
    const threshold = 48;
    let start = null, swiped = false;
    const report = (method, ...args) => dotnet.invokeMethodAsync(method, ...args)
        .catch(error => console.debug('Hero swipe observer disconnected', error));
    const down = event => {
        if (event.pointerType !== 'touch' || !event.isPrimary) return;
        start = { id: event.pointerId, x: event.clientX, y: event.clientY };
        swiped = false;
        report('OnHeroTouchHold', true);
    };
    const finish = event => {
        if (!start || event.pointerId !== start.id) return;
        const dx = event.clientX - start.x, dy = event.clientY - start.y;
        start = null;
        if (event.type === 'pointerup' && Math.abs(dx) > threshold && Math.abs(dy) < Math.abs(dx)) {
            swiped = true;
            report('OnHeroSwipe', dx < 0 ? 'next' : 'previous');
        }
        report('OnHeroTouchHold', false);
    };
    // A swipe that ends on a link must not also open it.
    const suppressClick = event => {
        if (!swiped) return;
        swiped = false;
        event.preventDefault();
        event.stopPropagation();
    };
    el.addEventListener('pointerdown', down, { passive: true });
    el.addEventListener('pointerup', finish, { passive: true });
    el.addEventListener('pointercancel', finish, { passive: true });
    el.addEventListener('click', suppressClick, true);
    el.__heroSwipe = {
        dispose() {
            el.removeEventListener('pointerdown', down);
            el.removeEventListener('pointerup', finish);
            el.removeEventListener('pointercancel', finish);
            el.removeEventListener('click', suppressClick, true);
        }
    };
};

window.unregisterHeroSwipe = function (el) {
    el?.__heroSwipe?.dispose();
    if (el) el.__heroSwipe = null;
};

window.tuvimaPasskeys = {
    createCredential: async function (optionsJson) {
        if (!window.PublicKeyCredential || !PublicKeyCredential.parseCreationOptionsFromJSON) {
            throw new Error("This browser does not support passkeys.");
        }

        const publicKey = PublicKeyCredential.parseCreationOptionsFromJSON(JSON.parse(optionsJson));
        const credential = await navigator.credentials.create({ publicKey });
        if (!credential || typeof credential.toJSON !== "function") {
            throw new Error("The browser did not return a usable passkey credential.");
        }

        return JSON.stringify(credential.toJSON());
    },
    getCredential: async function (optionsJson) {
        if (!window.PublicKeyCredential || !PublicKeyCredential.parseRequestOptionsFromJSON) {
            throw new Error("This browser does not support passkeys.");
        }

        const publicKey = PublicKeyCredential.parseRequestOptionsFromJSON(JSON.parse(optionsJson));
        const credential = await navigator.credentials.get({ publicKey });
        if (!credential || typeof credential.toJSON !== "function") {
            throw new Error("The browser did not return a usable passkey credential.");
        }

        return JSON.stringify(credential.toJSON());
    }
};

window.tuvimaEditorFocus = function (selector) {
    if (!selector) return;
    window.requestAnimationFrame(function () {
        var target = document.querySelector(selector);
        if (target && typeof target.focus === 'function') {
            target.focus({ preventScroll: true });
        }
    });
};

window.tuvimaEditorScrollTop = function () {
    window.requestAnimationFrame(function () {
        var pane = document.querySelector('.sme-body, .person-editor__main');
        if (pane) pane.scrollTop = 0;
    });
};

window.tuvimaContextSidebarBodyLock = (function () {
    const owners = new Set();
    let previousOverflow = null;
    return {
        set(owner, locked) {
            if (!owner || !document.body) return;
            if (locked) {
                if (owners.has(owner)) return;
                if (owners.size === 0) previousOverflow = document.body.style.overflow;
                owners.add(owner);
                document.body.style.overflow = 'hidden';
                return;
            }
            if (!owners.delete(owner)) return;
            if (owners.size === 0) {
                document.body.style.overflow = previousOverflow ?? '';
                previousOverflow = null;
            }
        },
        release(owner) {
            this.set(owner, false);
        }
    };
})();

window.tuvimaBookmarkCommands = (function () {
    var ownerChannels = new Map();
    function channelName(ownerId) {
        return 'tuvima-bookmark-commands:' + String(ownerId || '').replace(/-/g, '').toLowerCase();
    }
    function registerOwner(ownerId, dotNetRef) {
        if (!ownerId || !dotNetRef || typeof BroadcastChannel === 'undefined') return false;
        unregisterOwner(ownerId);
        var channel = new BroadcastChannel(channelName(ownerId));
        var registration = { channel: channel, dotNetRef: dotNetRef };
        registration.handler = function (event) {
            var message = event && event.data;
            var command = message && message.type === 'command' ? message.command : null;
            if (!command || command.recipientId !== ownerId || !command.senderId || !command.commandId) return;
            Promise.resolve(dotNetRef.invokeMethodAsync('HandleBookmarkCommand', command)).then(function (reply) {
                if (reply && reply.commandId === command.commandId && reply.recipientId === command.senderId)
                    channel.postMessage({ type: 'reply', reply: reply });
            }).catch(function (error) {
                console.debug('Bookmark command owner did not complete the request.', error);
            });
        };
        channel.addEventListener('message', registration.handler);
        ownerChannels.set(String(ownerId), registration);
        return true;
    }
    function unregisterOwner(ownerId) {
        var key = String(ownerId || '');
        var registration = ownerChannels.get(key);
        if (!registration) return;
        registration.channel.removeEventListener('message', registration.handler);
        registration.channel.close();
        ownerChannels.delete(key);
    }
    function send(ownerId, command, timeoutMilliseconds) {
        if (!ownerId || !command || command.recipientId !== ownerId
            || !command.commandId || !command.senderId || typeof BroadcastChannel === 'undefined')
            return Promise.reject(new Error('Bookmark command transport is unavailable.'));
        return new Promise(function (resolve, reject) {
            var channel = new BroadcastChannel(channelName(ownerId));
            var complete = false;
            var timer = window.setTimeout(function () { finish(null, new Error('The bookmark owner did not respond.')); },
                Math.max(1, Number(timeoutMilliseconds) || 8000));
            function finish(reply, error) {
                if (complete) return;
                complete = true;
                window.clearTimeout(timer);
                channel.removeEventListener('message', handler);
                channel.close();
                if (error) reject(error); else resolve(reply);
            }
            function handler(event) {
                var reply = event && event.data && event.data.type === 'reply' ? event.data.reply : null;
                if (reply && reply.commandId === command.commandId && reply.recipientId === command.senderId)
                    finish(reply, null);
            }
            channel.addEventListener('message', handler);
            channel.postMessage({ type: 'command', command: command });
        });
    }
    function getOwnerId() {
        var first = ownerChannels.keys().next();
        return first.done ? null : first.value;
    }
    return { registerOwner: registerOwner, unregisterOwner: unregisterOwner, send: send, getOwnerId: getOwnerId };
})();

window.tuvimaEditorScrollTo = function (selector) {
    window.requestAnimationFrame(function () {
        var target = selector && document.querySelector(selector);
        if (target) target.scrollIntoView({ block: 'start', behavior: window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth' });
    });
};

window.scrollElementToTop = function (element) {
    if (element) element.scrollTop = 0;
};

window.tuvimaScrollElementBy = function (element, amount) {
    if (element) element.scrollBy({ left: amount, behavior: window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth' });
};

window.tuvimaFocusById = function (id, attempts = 8) {
    const target = id && document.getElementById(id);
    if (target) target.focus({ preventScroll: true });
    // A list drawn in the top layer is shown a moment after it renders, so keep trying for a few frames until the element takes focus.
    if (id && attempts > 0 && (!target || document.activeElement !== target)) {
        requestAnimationFrame(() => window.tuvimaFocusById(id, attempts - 1));
    }
};

window.tuvimaPositionSharedEntityPopover = function (anchorId, popoverId) {
    const anchor = anchorId && document.getElementById(anchorId);
    const popover = popoverId && document.getElementById(popoverId);
    if (!anchor || !popover) return;

    const positions = window.__tuvimaSharedEntityPopoverPositions ||= new Map();
    const current = positions.get(popoverId);
    if (current && current.anchorId === anchorId) {
        current.position();
        return;
    }
    if (current) {
        window.removeEventListener('resize', current.position);
        window.removeEventListener('scroll', current.position, true);
        current.popover?.removeEventListener('keydown', current.escapeHandler);
    }

    // DialogContext also observes Escape at the document level. Stop the native
    // event at the selector boundary and route dismissal through the existing
    // close button so the editor stays open and focus returns to its anchor.
    const escapeHandler = (event) => {
        if (event.key !== 'Escape') return;
        event.preventDefault();
        event.stopPropagation();
        popover.querySelector('[aria-label="Close selector"]')?.click();
    };
    popover.addEventListener('keydown', escapeHandler);

    const position = () => {
        if (!anchor.isConnected || !popover.isConnected) return;
        const rect = anchor.getBoundingClientRect();
        const gutter = 12;
        const maxWidth = Math.max(120, Math.min(440, window.innerWidth - (gutter * 2)));
        const width = Math.min(maxWidth, Math.max(Math.min(300, maxWidth), rect.width));
        const left = Math.max(gutter, Math.min((rect.left + rect.right - width) / 2, window.innerWidth - width - gutter));
        const maxHeight = Math.max(120, Math.min(368, window.innerHeight - (gutter * 2)));
        const roomBelow = Math.max(0, window.innerHeight - rect.bottom - (gutter * 2));
        const roomAbove = Math.max(0, rect.top - (gutter * 2));
        const placeBelow = roomBelow >= Math.min(maxHeight, 220) || roomBelow >= roomAbove;
        const availableHeight = Math.max(80, Math.min(maxHeight, placeBelow ? roomBelow : roomAbove));
        const proposedTop = placeBelow ? rect.bottom + 8 : rect.top - availableHeight - 8;
        const top = Math.max(gutter, Math.min(proposedTop, window.innerHeight - availableHeight - gutter));

        popover.style.position = 'fixed';
        popover.style.left = `${left}px`;
        popover.style.top = `${top}px`;
        popover.style.width = `${width}px`;
        popover.style.maxWidth = `calc(100vw - ${gutter * 2}px)`;
        popover.style.maxHeight = `${availableHeight}px`;
        popover.style.zIndex = '1500';
    };

    positions.set(popoverId, { anchorId, position, popover, escapeHandler });
    window.addEventListener('resize', position);
    window.addEventListener('scroll', position, true);
    position();
};

window.tuvimaRemoveSharedEntityPopoverPosition = function (popoverId) {
    const positions = window.__tuvimaSharedEntityPopoverPositions;
    const current = positions && positions.get(popoverId);
    if (current) {
        window.removeEventListener('resize', current.position);
        window.removeEventListener('scroll', current.position, true);
        current.popover?.removeEventListener('keydown', current.escapeHandler);
        positions.delete(popoverId);
    }
    const popover = popoverId && document.getElementById(popoverId);
    if (popover) {
        popover.style.removeProperty('position');
        popover.style.removeProperty('left');
        popover.style.removeProperty('top');
        popover.style.removeProperty('width');
        popover.style.removeProperty('max-width');
        popover.style.removeProperty('max-height');
        popover.style.removeProperty('z-index');
    }
};

// All custom menus use the same click-away contract. AppOverflowMenu handles
// its own dismissal; this covers richer application-owned popout surfaces.
(function installDismissibleSurfaceHandler() {
    document.addEventListener('pointerdown', function (event) {
        document.querySelectorAll('[data-app-dismissible="open"]').forEach(function (surface) {
            const popover = surface.dataset.appPopoverId
                ? document.getElementById(surface.dataset.appPopoverId)
                : null;
            if (!surface.contains(event.target) && !popover?.contains(event.target)) {
                surface.querySelector('[data-app-dismiss]')?.click();
            }
        });
    }, true);

    document.addEventListener('focusin', function (event) {
        document.querySelectorAll('[data-app-dismissible="open"]').forEach(function (surface) {
            const popover = surface.dataset.appPopoverId
                ? document.getElementById(surface.dataset.appPopoverId)
                : null;
            if (!surface.contains(event.target) && !popover?.contains(event.target)) {
                surface.querySelector('[data-app-dismiss]')?.click();
            }
        });
    }, true);

    document.addEventListener('keydown', function (event) {
        if (event.key !== 'Escape') return;
        const openSurface = document.querySelector('[data-app-dismissible="open"]');
        if (!openSurface) return;
        event.preventDefault();
        openSurface.querySelector('[data-app-dismiss-restore]')?.click();
    }, true);
})();

/**
 * Registers a global Ctrl+K (or Cmd+K on Mac) keydown listener that invokes
 * the .NET OpenPalette() method on the provided DotNetObjectReference.
 *
 * Called once from MainLayout.OnAfterRenderAsync.
 *
 * @param {DotNetObjectReference} dotNetRef - Reference to the MainLayout component.
 */
window.registerCtrlK = function (dotNetRefOrStartupToken, ownerOrDotNetRef, ownerKey) {
    var dotNetRef = ownerOrDotNetRef && typeof ownerOrDotNetRef.invokeMethodAsync === 'function'
        ? ownerOrDotNetRef
        : dotNetRefOrStartupToken;
    var nextOwnerKey = ownerOrDotNetRef && typeof ownerOrDotNetRef.invokeMethodAsync === 'function'
        ? ownerKey
        : ownerOrDotNetRef;
    window.unregisterCtrlK();

    window._appCtrlKHandler = function (e) {
        if ((e.ctrlKey || e.metaKey) && e.key === 'k') {
            e.preventDefault();
            dotNetRef.invokeMethodAsync('OpenPalette');
        }
    };
    window._appCtrlKOwnerKey = nextOwnerKey ?? null;

    document.addEventListener('keydown', window._appCtrlKHandler);
};

window.unregisterCtrlK = function (ownerKey) {
    if (!window._appCtrlKHandler) {
        return;
    }
    if (ownerKey !== undefined && ownerKey !== null && window._appCtrlKOwnerKey !== ownerKey) {
        return;
    }

    document.removeEventListener('keydown', window._appCtrlKHandler);
    window._appCtrlKHandler = null;
    window._appCtrlKOwnerKey = null;
};

// -- Device Context ---------------------------------------------------------

window.tuvimaResponsive = (function () {
    var observer = null;
    var observerTimer = null;
    var lastObservedClass = null;
    var pendingObservedClass = null;
    var notificationVersion = 0;
    var breakpointMediaQuery = null;
    var breakpointMediaHandler = null;
    var viewportResizeObserver = null;
    var pointerMediaQueries = [];
    var observerOwnerKey = null;
    var observerWantsSnapshot = false;

    function readBreakpoint(name, fallback) {
        var value = window.getComputedStyle(document.documentElement)
            .getPropertyValue('--tl-breakpoint-' + name)
            .trim();
        var parsed = Number(value);
        return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
    }

    function isTvRequested() {
        try {
            var query = new URLSearchParams(window.location.search).get('device');
            if (query !== null) {
                if (query.toLowerCase() === 'tv') {
                    window.sessionStorage.setItem('tuvima.device', 'tv');
                } else {
                    window.sessionStorage.removeItem('tuvima.device');
                }
            }
            if (window.sessionStorage.getItem('tuvima.device') === 'tv') return true;
        } catch (_) {
            // Storage can be blocked; fall back to the ?device=tv query and user agent only.
            if (/[?&]device=tv(&|$)/i.test(window.location.search)) return true;
        }
        return /Android TV|BRAVIA|SMART-TV|SmartTV|Tizen|Web0S|webOS/i.test(navigator.userAgent || '');
    }

    function classifyFormFactor() {
        if (isTvRequested()) return 'tv';
        var phone = readBreakpoint('phone', 600);
        var desktop = readBreakpoint('desktop', 1024);
        var width = window.innerWidth;
        if (window.matchMedia) {
            if (window.matchMedia('(max-width: ' + (phone - 0.02) + 'px)').matches) return 'phone';
            if (window.matchMedia('(max-width: ' + (desktop - 0.02) + 'px)').matches) return 'tablet';
            return 'desktop';
        }
        return width < phone ? 'phone' : width < desktop ? 'tablet' : 'desktop';
    }

    function isTouch() {
        return !!window.matchMedia && window.matchMedia('(hover: none), (pointer: coarse)').matches;
    }

    // The editing rule needs the primary pointer exactly: fine (mouse, trackpad) and able to hover.
    // A browser without matchMedia reports neither, which means "cannot edit".
    function hasFinePointer() {
        return !!window.matchMedia && window.matchMedia('(pointer: fine)').matches;
    }

    function canHover() {
        return !!window.matchMedia && window.matchMedia('(hover: hover)').matches;
    }

    function deviceClassFor(formFactor) {
        return formFactor === 'phone' ? 'mobile' : formFactor === 'tv' ? 'television' : 'web';
    }

    function snapshot() {
        var formFactor = classifyFormFactor();
        return { deviceClass: deviceClassFor(formFactor), formFactor: formFactor, touch: isTouch(), finePointer: hasFinePointer(), hover: canHover() };
    }

    function applyAttributes(state) {
        var root = document.documentElement;
        if (!root) return;
        root.setAttribute('data-form-factor', state.formFactor);
        root.setAttribute('data-touch', state.touch ? 'true' : 'false');
    }

    function classifyViewport() {
        var state = snapshot();
        applyAttributes(state);
        return state.deviceClass;
    }

    function register(dotNetRef, ownerKey, withSnapshot) {
        unregister();
        observerWantsSnapshot = !!withSnapshot;
        observer = dotNetRef;
        observerOwnerKey = ownerKey || {};
        lastObservedClass = null;
        window.addEventListener('resize', onResize, { passive: true });
        window.addEventListener('orientationchange', onResize, { passive: true });
        if (window.visualViewport) {
            window.visualViewport.addEventListener('resize', onResize, { passive: true });
        }

        if (window.matchMedia) {
            breakpointMediaQuery = window.matchMedia('(max-width: ' + readBreakpoint('navigation', 840) + 'px)');
            breakpointMediaHandler = onResize;
            if (breakpointMediaQuery.addEventListener) {
                breakpointMediaQuery.addEventListener('change', breakpointMediaHandler);
            } else if (breakpointMediaQuery.addListener) {
                breakpointMediaQuery.addListener(breakpointMediaHandler);
            }
        }

        // Plugging in or removing a mouse changes the primary pointer without any resize.
        if (window.matchMedia) {
            ['(pointer: fine)', '(hover: hover)'].forEach(function (query) {
                var mediaQuery = window.matchMedia(query);
                if (mediaQuery.addEventListener) {
                    mediaQuery.addEventListener('change', onResize);
                    pointerMediaQueries.push(mediaQuery);
                }
            });
        }

        if (window.ResizeObserver && document.documentElement) {
            var owner = observer;
            viewportResizeObserver = new window.ResizeObserver(function () {
                if (observer === owner) onResize();
            });
            viewportResizeObserver.observe(document.documentElement);
        }

        // The viewport may have changed while the initial settings request was in flight.
        notifyObserver();
    }

    function onResize() {
        window.clearTimeout(observerTimer);
        observerTimer = window.setTimeout(function () {
            notifyObserver();
        }, 160);
    }

    function notifyObserver() {
        if (!observer) return;
        var activeObserver = observer;
        var state = snapshot();
        applyAttributes(state);
        var next = observerWantsSnapshot
            ? state.deviceClass + '|' + state.formFactor + '|' + state.touch + '|' + state.finePointer + '|' + state.hover
            : state.deviceClass;
        if (next === pendingObservedClass
            || next === lastObservedClass && pendingObservedClass === null) return;

        var version = ++notificationVersion;
        pendingObservedClass = next;
        try {
            var call = observerWantsSnapshot
                ? activeObserver.invokeMethodAsync('HandleViewportChanged', state)
                : activeObserver.invokeMethodAsync('HandleViewportDeviceClassChanged', state.deviceClass);
            Promise.resolve(call)
                .then(function () {
                    if (observer !== activeObserver || version !== notificationVersion) return;
                    lastObservedClass = next;
                    pendingObservedClass = null;
                })
                .catch(function () {
                    if (observer !== activeObserver || version !== notificationVersion) return;
                    pendingObservedClass = null;
                });
        } catch (_) {
            if (observer === activeObserver && version === notificationVersion) {
                pendingObservedClass = null;
            }
        }
    }

    function unregister(ownerKey) {
        if (ownerKey !== undefined && ownerKey !== observerOwnerKey) return false;
        window.removeEventListener('resize', onResize);
        window.removeEventListener('orientationchange', onResize);
        if (window.visualViewport) {
            window.visualViewport.removeEventListener('resize', onResize);
        }
        if (breakpointMediaQuery && breakpointMediaHandler) {
            if (breakpointMediaQuery.removeEventListener) {
                breakpointMediaQuery.removeEventListener('change', breakpointMediaHandler);
            } else if (breakpointMediaQuery.removeListener) {
                breakpointMediaQuery.removeListener(breakpointMediaHandler);
            }
        }
        pointerMediaQueries.forEach(function (mediaQuery) {
            mediaQuery.removeEventListener('change', onResize);
        });
        pointerMediaQueries = [];
        if (viewportResizeObserver) {
            viewportResizeObserver.disconnect();
        }
        window.clearTimeout(observerTimer);
        observerTimer = null;
        breakpointMediaQuery = null;
        breakpointMediaHandler = null;
        viewportResizeObserver = null;
        pendingObservedClass = null;
        notificationVersion++;
        observer = null;
        observerOwnerKey = null;
        return true;
    }

    return {
        resolveDeviceClass: classifyViewport,
        resolveDeviceSnapshot: function () { var state = snapshot(); applyAttributes(state); return state; },
        registerDeviceClassObserver: register,
        unregisterDeviceClassObserver: unregister,
        navigationBreakpoint: function () { return readBreakpoint('navigation', 840); }
    };
})();

/**
 * Returns responsive presentation density only. Trusted native device identity
 * and class come from the server-issued bearer token and are never read here.
 * @returns {string} "web" or "mobile".
 */
window.detectDeviceClass = function () {
    return window.tuvimaResponsive.resolveDeviceClass();
};

/**
 * Returns the full responsive snapshot and stamps data-form-factor / data-touch on <html>.
 * @returns {{deviceClass: string, formFactor: string, touch: boolean, finePointer: boolean, hover: boolean}}
 */
window.detectDeviceSnapshot = function () {
    return window.tuvimaResponsive.resolveDeviceSnapshot();
};

// -- Spatial navigation (keyboard and TV remote) ----------------------------
// Arrow keys move focus by geometry, Enter activates, Escape/Backspace go back. On the tv form factor it is always
// on; elsewhere it arms on the first arrow key pressed while focus is on a shelf tile. It never acts inside text
// fields, selects, sliders, contenteditable regions or while a modal/dialog is open.

window.tuvimaSpatialNav = (function () {
    var DIRECTIONS = { ArrowLeft: 'left', ArrowRight: 'right', ArrowUp: 'up', ArrowDown: 'down' };
    var FOCUSABLE = 'a[href], button, summary, [tabindex], [role="button"], [role="tab"], [role="link"], [role="menuitem"], input, select, textarea';
    var ROW = '.media-tile-shelf-scroll, [data-spatial-row]';
    var SHELF = '.media-tile-shelf, .media-tile-shelf-track';
    var DOCK = '.layout-shell__intent-dock';
    var MODAL = 'dialog[open], [aria-modal="true"], [role="dialog"], [role="alertdialog"]';
    var EDITABLE_ROLES = { textbox: 1, searchbox: 1, combobox: 1, listbox: 1, slider: 1, spinbutton: 1 };
    var NON_TEXT_INPUTS = { button: 1, submit: 1, reset: 1, checkbox: 1, image: 1 };
    var NATIVE_ACTIVATORS = { A: 1, BUTTON: 1, SUMMARY: 1, INPUT: 1, SELECT: 1, TEXTAREA: 1 };

    function center(rect) {
        return { x: (rect.left + rect.right) / 2, y: (rect.top + rect.bottom) / 2 };
    }

    // Pure geometry: the nearest item in a direction. Distance along the axis plus double the sideways offset.
    function pickNext(from, items, direction) {
        var origin = center(from);
        var horizontal = direction === 'left' || direction === 'right';
        var best = null;
        var bestScore = Infinity;
        items.forEach(function (item) {
            if (item.rect === from) return;
            var point = center(item.rect);
            var along = horizontal ? point.x - origin.x : point.y - origin.y;
            var across = horizontal ? point.y - origin.y : point.x - origin.x;
            if (direction === 'left' || direction === 'up') along = -along;
            if (along <= 1) return;
            var score = along + 2 * Math.abs(across);
            if (score < bestScore) { bestScore = score; best = item; }
        });
        return best;
    }

    function isEditable(el) {
        if (!el || !el.tagName) return false;
        var tag = el.tagName.toUpperCase();
        if (tag === 'TEXTAREA' || tag === 'SELECT' || el.isContentEditable) return true;
        if (tag === 'INPUT') return !NON_TEXT_INPUTS[String(el.type || 'text').toLowerCase()];
        var role = el.getAttribute && el.getAttribute('role');
        return !!(role && EDITABLE_ROLES[role]);
    }

    function isShown(el) {
        return !!el.getClientRects && el.getClientRects().length > 0;
    }

    function hasOpenModal(doc) {
        var modals = doc.querySelectorAll(MODAL);
        for (var i = 0; i < modals.length; i++) {
            if (isShown(modals[i])) return true;
        }
        return false;
    }

    function isCandidate(el) {
        if (isEditable(el) || el.disabled || !isShown(el)) return false;
        if (el.getAttribute('tabindex') === '-1') return false;
        if (el.classList && el.classList.contains('skip-to-content')) return false; // Reachable with Tab; it sits off screen.
        return !el.closest('[inert], [aria-hidden="true"]');
    }

    function registerSpatialNav(root) {
        root = root || document;
        if (root.__tuvimaSpatialNav) return root.__tuvimaSpatialNav;
        var doc = root.ownerDocument || root;
        var armed = false;

        function isTv() {
            return !!doc.documentElement && doc.documentElement.getAttribute('data-form-factor') === 'tv';
        }

        function candidates() {
            var out = [];
            var nodes = root.querySelectorAll(FOCUSABLE);
            for (var i = 0; i < nodes.length; i++) {
                if (isCandidate(nodes[i])) out.push({ el: nodes[i], rect: nodes[i].getBoundingClientRect() });
            }
            return out;
        }

        function reveal(el, horizontal) {
            var behavior = typeof window.tuvimaPrefersReducedMotion === 'function' && window.tuvimaPrefersReducedMotion() ? 'auto' : 'smooth';
            el.focus({ preventScroll: true });
            el.scrollIntoView({ block: horizontal ? 'nearest' : 'center', inline: horizontal ? 'center' : 'nearest', behavior: behavior });
        }

        function moveFocus(event, direction, active) {
            var horizontal = direction === 'left' || direction === 'right';
            var items = candidates();
            var anchored = active && active !== doc.body && active !== doc.documentElement && root.contains(active);
            if (!anchored) {
                // Nothing focused yet: start from the first control in view (top row, then left to right).
                var visible = items.filter(function (item) { return item.rect.bottom > 0 && item.rect.top < (window.innerHeight || Infinity); });
                visible.sort(function (a, b) { return (a.rect.top - b.rect.top) || (a.rect.left - b.rect.left); });
                if (visible.length === 0) return;
                event.preventDefault();
                reveal(visible[0].el, horizontal);
                return;
            }

            var row = horizontal && active.closest ? active.closest(ROW) : null;
            if (row) items = items.filter(function (item) { return item.el.closest(ROW) === row; });
            // The dock is fixed to the bottom of the screen, so it is always the nearest thing below the content.
            // Moving up or down through the page therefore skips it while content remains, and falls back to it at the end.
            var inDock = !!(active.closest && active.closest(DOCK));
            var next = null;
            if (!horizontal && !inDock) {
                var content = items.filter(function (item) { return !item.el.closest(DOCK); });
                next = pickNext(active.getBoundingClientRect(), content, direction);
            }
            if (!next) next = pickNext(active.getBoundingClientRect(), items, direction);
            if (next) {
                event.preventDefault();
                reveal(next.el, horizontal);
            } else if (row) {
                event.preventDefault(); // End of a shelf: stay put instead of scrolling the page sideways.
            }
        }

        function hasExpandedPopup() {
            var nodes = doc.querySelectorAll('[aria-expanded="true"]');
            for (var i = 0; i < nodes.length; i++) {
                if (isShown(nodes[i])) return true;
            }
            return false;
        }

        function onKeyDown(event) {
            if (event.defaultPrevented || event.ctrlKey || event.altKey || event.metaKey) return;
            var key = event.key;
            var direction = DIRECTIONS[key];
            var back = key === 'Escape' || key === 'Backspace';
            if (!direction && !back && key !== 'Enter') return;
            if (direction && event.shiftKey) return;

            var active = doc.activeElement;
            if (isEditable(active) || isEditable(event.target)) return;
            if (hasOpenModal(doc)) return;
            if (active && active.closest && active.closest('[data-spatial-nav="off"]')) return;

            if (!isTv() && !armed) {
                var onShelf = direction && active && active.closest && active.closest(SHELF);
                if (!onShelf) return;
                armed = true;
            }

            if (direction) {
                moveFocus(event, direction, active);
            } else if (key === 'Enter') {
                var role = active && active.getAttribute && active.getAttribute('role');
                var custom = active && !NATIVE_ACTIVATORS[active.tagName] && (role === 'button' || role === 'link' || role === 'tab' || role === 'menuitem' || active.hasAttribute('tabindex'));
                if (custom && active.getAttribute('tabindex') !== '-1') {
                    event.preventDefault();
                    active.click();
                }
            } else if (!hasExpandedPopup() && window.history.length > 1) {
                event.preventDefault();
                window.history.back();
            }
        }

        root.addEventListener('keydown', onKeyDown);
        var unregister = function () {
            root.removeEventListener('keydown', onKeyDown);
            delete root.__tuvimaSpatialNav;
        };
        root.__tuvimaSpatialNav = unregister;
        return unregister;
    }

    return { registerSpatialNav: registerSpatialNav, pickNext: pickNext, isEditable: isEditable };
})();

window.registerSpatialNav = window.tuvimaSpatialNav.registerSpatialNav;

// -- Spatial navigation start
window.registerSpatialNav(document);

// -- Installable web app ----------------------------------------------------

window.tuvimaPwa = (function () {
    var deferredPrompt = null;
    var observers = new Map();
    var dismissed = false;
    var dismissalKey = 'tuvima.installBanner.dismissed';
    try { dismissed = localStorage.getItem(dismissalKey) === 'true'; }
    catch (error) { console.debug('Install preference storage is unavailable.', error); }

    function currentState(manual) {
        if (window.matchMedia('(display-mode: standalone)').matches || window.navigator.standalone === true) return 'installed';
        if (!manual && dismissed) return 'dismissed';
        if (deferredPrompt) return 'available';
        if (/iphone|ipad|ipod/i.test(window.navigator.userAgent)) return 'ios';
        return 'unavailable';
    }
    function notify() {
        observers.forEach(function (entry) {
            entry.ref.invokeMethodAsync('HandleInstallStateChanged', currentState(entry.manual));
        });
    }
    window.addEventListener('beforeinstallprompt', function (event) {
        event.preventDefault(); deferredPrompt = event; notify();
    });
    window.addEventListener('appinstalled', function () { deferredPrompt = null; notify(); });
    window.addEventListener('storage', function (event) {
        if (event.key === dismissalKey) { dismissed = event.newValue === 'true'; notify(); }
    });
    if ('serviceWorker' in navigator) {
        window.addEventListener('load', function () {
            navigator.serviceWorker.register('/service-worker.js', { scope: '/' })
                .catch(function (error) { console.debug('PWA registration failed.', error); });
        });
    }
    return {
        register: function (ref, id, manual) {
            observers.set(id, { ref: ref, manual: manual === true });
            return currentState(manual);
        },
        unregister: function (id) { observers.delete(id); },
        dismiss: function () {
            dismissed = true;
            try { localStorage.setItem(dismissalKey, 'true'); }
            catch (error) { console.debug('Install dismissal retained for this session only.', error); }
            notify();
            return currentState(false);
        },
        install: async function (manual) {
            if (!deferredPrompt) return currentState(manual);
            await deferredPrompt.prompt();
            var choice = await deferredPrompt.userChoice;
            deferredPrompt = null;
            if (choice.outcome !== 'accepted') {
                dismissed = true;
                try { localStorage.setItem(dismissalKey, 'true'); }
                catch (error) { console.debug('Install dismissal retained for this session only.', error); }
            }
            notify();
            return currentState(manual);
        }
    };
})();

// -- Swimlane Scroll Arrows -----------------------------------------------

/**
 * Smoothly scrolls a swimlane element left or right by 75% of its visible width.
 * Called from PosterSwimlane.razor via JS interop.
 *
 * @param {HTMLElement} el  - The .swimlane-scroll container element.
 * @param {string} direction - "left" or "right".
 */
window.scrollSwimlane = function (el, direction) {
    if (!el) return;
    var scrollAmount = el.clientWidth * 0.75;
    el.scrollBy({
        left: direction === 'left' ? -scrollAmount : scrollAmount,
        behavior: 'smooth'
    });
};

/**
 * Returns the current scroll boundary state for a swimlane element.
 * @param {HTMLElement} el - The .swimlane-scroll container.
 * @returns {{ atStart: boolean, atEnd: boolean }}
 */
window.getSwimlaneScrollState = function (el) {
    if (!el) return { atStart: true, atEnd: true };
    if (el.classList && el.classList.contains('media-tile-shelf-scroll') && window.updateMediaTileShelfVisibleWidth) {
        window.updateMediaTileShelfVisibleWidth(el);
    }

    var tolerance = 4;
    return {
        atStart: el.scrollLeft <= tolerance,
        atEnd: el.scrollLeft + el.clientWidth >= el.scrollWidth - tolerance
    };
};

window.isElementContentOverflowing = function (element) {
    if (!element) return false;
    return element.scrollHeight > element.clientHeight + 1
        || element.scrollWidth > element.clientWidth + 1;
};

window.getSwimlaneItems = function (el) {
    if (!el) return [];

    if (el.classList && el.classList.contains('media-tile-shelf-scroll')) {
        return Array.prototype.filter.call(el.querySelectorAll('.media-tile, .recent-view-card'), function (item) {
            return item
                && item.offsetWidth > 0
                && item.closest('.media-tile-shelf-scroll') === el;
        });
    }

    return Array.prototype.filter.call(el.children || [], function (child) {
        return child && child.offsetWidth > 0;
    });
};

window.getSwimlaneSnapTargets = function (el) {
    if (!el) return [];

    var maxScroll = Math.max(0, el.scrollWidth - el.clientWidth);
    var containerRect = el.getBoundingClientRect();
    var currentLeft = el.scrollLeft;
    var targets = [0, maxScroll];
    var style = window.getComputedStyle ? window.getComputedStyle(el) : null;
    var paddingLeft = style ? parseFloat(style.paddingLeft || '0') : 0;
    paddingLeft = Number.isFinite(paddingLeft) ? paddingLeft : 0;

    window.getSwimlaneItems(el).forEach(function (child) {
        var childRect = child.getBoundingClientRect();
        var target = currentLeft + childRect.left - containerRect.left - paddingLeft;
        target = Math.min(Math.max(target, 0), maxScroll);
        targets.push(Math.round(target));
    });

    return targets
        .filter(function (target, index, list) {
            return list.indexOf(target) === index;
        })
        .sort(function (a, b) { return a - b; });
};

window.getSwimlaneSnapTarget = function (el, direction) {
    if (!el) return 0;

    var targets = window.getSwimlaneSnapTargets(el);
    var currentLeft = el.scrollLeft;
    var maxScroll = Math.max(0, el.scrollWidth - el.clientWidth);
    var amount = el.clientWidth * 0.75;
    var tolerance = 4;

    if (targets.length === 0) {
        return Math.min(Math.max(currentLeft + (direction === 'left' ? -amount : amount), 0), maxScroll);
    }

    if (direction === 'left') {
        var desiredLeft = currentLeft - amount;
        var leftCandidates = targets.filter(function (target) {
            return target < currentLeft - tolerance;
        });
        var firstLeftCandidateInPage = leftCandidates.find(function (target) {
            return target >= desiredLeft - tolerance;
        });

        return firstLeftCandidateInPage !== undefined
            ? firstLeftCandidateInPage
            : (leftCandidates.length > 0 ? leftCandidates[leftCandidates.length - 1] : 0);
    }

    var desiredRight = currentLeft + amount;
    var rightCandidates = targets.filter(function (target) {
        return target > currentLeft + tolerance;
    });
    var lastRightCandidateInPage = rightCandidates.filter(function (target) {
        return target <= desiredRight + tolerance;
    }).pop();

    return lastRightCandidateInPage !== undefined
        ? lastRightCandidateInPage
        : (rightCandidates.length > 0 ? rightCandidates[0] : maxScroll);
};

/**
 * Scrolls the swimlane and resolves with the boundary state after the
 * animation completes (~400 ms for smooth scroll).
 * @param {HTMLElement} el - The .swimlane-scroll container.
 * @param {string} direction - "left" or "right".
 * @returns {Promise<{ atStart: boolean, atEnd: boolean }>}
 */
window.scrollSwimlaneEx = function (el, direction) {
    if (!el) return Promise.resolve({ atStart: true, atEnd: true });
    if (el.classList && el.classList.contains('media-tile-shelf-scroll') && window.updateMediaTileShelfVisibleWidth) {
        window.updateMediaTileShelfVisibleWidth(el);
    }

    var target = window.getSwimlaneSnapTarget(el, direction);
    el.__swimlaneAllowScroll = true;
    el.scrollTo({ left: target, behavior: 'smooth' });
    return new Promise(function (resolve) {
        setTimeout(function () {
            el.scrollLeft = target;
            el.__swimlaneStableScrollLeft = el.scrollLeft;
            el.__swimlaneAllowScroll = false;
            resolve({
                atStart: el.scrollLeft <= 4,
                atEnd: el.scrollLeft + el.clientWidth >= el.scrollWidth - 4
            });
        }, 420); // wait for smooth-scroll animation to settle
    });
};

window.packContinueGroups = function (row) {
    if (!row || row.__packing) return;
    const groups = Array.from(row.querySelectorAll('.continue-group'));
    // Below this breakpoint CSS gives each group the full row width.
    if (window.innerWidth < 1280 || groups.length < 2) {
        groups.forEach(group => group.style.removeProperty('width'));
        return;
    }
    row.__packing = true;
    try {
        const data = groups.map(group => {
            const scroll = group.querySelector('.media-tile-shelf-scroll');
            const tiles = scroll?.querySelectorAll('.media-tile');
            const width = tiles?.[0]?.getBoundingClientRect().width || 0;
            const style = scroll ? getComputedStyle(scroll) : null;
            const gap = parseFloat(style?.gap) || 0;
            const gutters = (parseFloat(style?.paddingLeft) || 0) + (parseFloat(style?.paddingRight) || 0);
            const count = tiles?.length || 0;
            return { group, width, gap, gutters, count, visible: Math.min(2, count) };
        });
        if (data.some(item => item.width <= 0 || item.count === 0)) return;
        const groupWidth = item => Math.ceil(item.width * item.visible + item.gap * (item.visible - 1) + item.gutters + 96);
        const available = row.clientWidth - 32 * (groups.length - 1);
        let used = data.reduce((sum, item) => sum + groupWidth(item), 0);
        // Spend spare width on whole cards, but never reduce a group below two.
        // If the minimum groups do not fit, flex-wrap moves a group to the next line.
        for (const item of data) {
            while (item.visible < item.count) {
                const before = groupWidth(item);
                item.visible++;
                const extra = groupWidth(item) - before;
                if (used + extra > available) { item.visible--; break; }
                used += extra;
            }
        }
        for (const item of data) item.group.style.width = Math.min(row.clientWidth, groupWidth(item)) + 'px';
    } finally { row.__packing = false; }
};
window.updateMediaTileShelfVisibleWidth = function (el) {
    if (el?.closest) window.packContinueGroups(el.closest('.continue-groups'));
    if (!el) return;

    var track = el.closest ? el.closest('.media-tile-shelf-track') : el.parentElement;
    var viewport = el.parentElement && el.parentElement.classList && el.parentElement.classList.contains('media-tile-shelf-window')
        ? el.parentElement
        : track;
    var availableWidth = viewport ? viewport.clientWidth : el.clientWidth;
    var items = window.getSwimlaneItems(el);

    if (!availableWidth || items.length === 0) {
        return;
    }

    var firstRect = items[0].getBoundingClientRect();
    var itemWidth = firstRect.width || items[0].offsetWidth;
    if (!itemWidth) {
        return;
    }

    var style = window.getComputedStyle ? window.getComputedStyle(el) : null;
    var paddingLeft = style ? parseFloat(style.paddingLeft || '0') : 0;
    var paddingRight = style ? parseFloat(style.paddingRight || '0') : 0;
    paddingLeft = Number.isFinite(paddingLeft) ? paddingLeft : 0;
    paddingRight = Number.isFinite(paddingRight) ? paddingRight : 0;

    var visibleWidth = availableWidth;
    // Phones let the row run to the screen edge so the next card peeks; larger screens trim it to whole cards.
    if (el.closest && el.closest('.continue-group') && document.documentElement.dataset.formFactor !== 'phone') {
        const gap = style ? parseFloat(style.columnGap || style.gap || '0') || 0 : 0;
        const count = Math.max(1, Math.floor((availableWidth - paddingLeft - paddingRight + gap) / (itemWidth + gap)));
        visibleWidth = Math.min(availableWidth, count * itemWidth + Math.max(0, count - 1) * gap + paddingLeft + paddingRight);
    }

    visibleWidth = Math.max(Math.min(availableWidth, visibleWidth), Math.min(availableWidth, itemWidth + paddingLeft + paddingRight));

    var roundedVisibleWidth = Math.round(visibleWidth);
    var arrowOffset = Math.max(0, Math.round(availableWidth - roundedVisibleWidth));
    var visibleWidthValue = roundedVisibleWidth + 'px';
    var arrowOffsetValue = arrowOffset + 'px';

    el.style.setProperty('--media-tile-shelf-visible-width', visibleWidthValue);
    el.style.setProperty('--media-tile-shelf-arrow-offset', arrowOffsetValue);

    if (track) {
        track.style.setProperty('--media-tile-shelf-visible-width', visibleWidthValue);
        track.style.setProperty('--media-tile-shelf-arrow-offset', arrowOffsetValue);
    }

    var maxScroll = Math.max(0, el.scrollWidth - el.clientWidth);
    if (el.scrollLeft > maxScroll) {
        el.scrollLeft = maxScroll;
    }

    if (el.__swimlaneStableScrollLeft !== undefined) {
        el.__swimlaneStableScrollLeft = Math.min(el.__swimlaneStableScrollLeft, maxScroll);
    }
};

window.isVerticalMediaTileWheel = function (event) {
    if (!event) return false;
    return Math.abs(event.deltaY || 0) >= Math.abs(event.deltaX || 0);
};

window.updateMediaTileShelfStableHeight = function (el) {
    if (!el) return;
    // Freeze the shelf throughout expansion and contraction, including observer callbacks.
    if (el.classList && el.classList.contains('has-active-in-row-hover')) return;

    var style = window.getComputedStyle ? window.getComputedStyle(el) : null;
    var paddingTop = style ? parseFloat(style.paddingTop || '0') : 0;
    var paddingBottom = style ? parseFloat(style.paddingBottom || '0') : 0;
    paddingTop = Number.isFinite(paddingTop) ? paddingTop : 0;
    paddingBottom = Number.isFinite(paddingBottom) ? paddingBottom : 0;

    var restingHeight = 0;
    Array.prototype.forEach.call(el.querySelectorAll('.media-tile, .recent-view-card'), function (tile) {
        if (tile.closest('.media-tile-shelf-scroll') !== el) return;
        // Include captions beneath artwork as well as fixed-size group tiles.
        var rect = tile.getBoundingClientRect();
        restingHeight = Math.max(restingHeight, tile.offsetHeight || rect.height || 0);
    });

    if (restingHeight > 0) {
        el.style.height = Math.ceil(restingHeight + paddingTop + paddingBottom) + 'px';
        el.style.setProperty('--media-tile-row-height', Math.ceil(restingHeight) + 'px');
    }
};

window.registerMediaShelfBoundaries = function (el, dotnet) {
    if (!el || el.__shelfBoundaries) return;
    let previous = '', frame = 0;
    const update = () => {
        if (frame) cancelAnimationFrame(frame);
        frame = requestAnimationFrame(() => {
            frame = 0;
            const state = window.getSwimlaneScrollState(el);
            const key = state.atStart + ':' + state.atEnd;
            if (key === previous) return;
            previous = key;
            dotnet.invokeMethodAsync('OnScrollBoundaryChanged', state.atStart, state.atEnd)
                .catch(error => console.debug('Shelf boundary observer disconnected', error));
        });
    };
    const resize = new ResizeObserver(update);
    resize.observe(el);
    el.addEventListener('scroll', update, { passive: true });
    el.__shelfBoundaries = { dispose() { resize.disconnect(); el.removeEventListener('scroll', update); if (frame) cancelAnimationFrame(frame); } };
    update();
};
window.unregisterMediaShelfBoundaries = function (el) {
    el?.__shelfBoundaries?.dispose();
    if (el) el.__shelfBoundaries = null;
};

// Removes everything a media shelf registered in one call, so it runs while the element is still on the page.
window.unregisterMediaShelf = function (el) {
    window.unregisterMediaShelfBoundaries(el);
    window.unregisterMediaTileShelfScrollGuard(el);
};

window.registerMediaTileShelfScrollGuard = function (el) {
    if (!el || el.__mediaTileShelfScrollGuard) return;

    var isFinePointer = function () {
        return !window.matchMedia || window.matchMedia('(hover: hover) and (pointer: fine)').matches;
    };

    var restoreStablePosition = function () {
        if (!isFinePointer() || !el.classList.contains("has-active-in-row-hover")) return;

        if (!el.__swimlaneAllowScroll && !el.__mediaTileHoverScrollLock) {
            var stableLeft = el.__swimlaneStableScrollLeft || 0;
            if (Math.abs(el.scrollLeft - stableLeft) > 1) {
                el.scrollLeft = stableLeft;
            }
        }


    };

    var onWheel = function (event) {
        if (!isFinePointer() || !el.classList.contains("has-active-in-row-hover")) return;

        if (window.isVerticalMediaTileWheel(event)) {
            window.requestAnimationFrame(restoreStablePosition);
            return;
        }

        event.preventDefault();
        event.stopPropagation();
        window.requestAnimationFrame(restoreStablePosition);
    };

    var onScroll = function () {
        window.requestAnimationFrame(restoreStablePosition);
    };

    var onResize = function () {
        if (el.__mediaTileShelfResizeFrame) {
            window.cancelAnimationFrame(el.__mediaTileShelfResizeFrame);
        }

        el.__mediaTileShelfResizeFrame = window.requestAnimationFrame(function () {
            el.__mediaTileShelfResizeFrame = null;
            window.updateMediaTileShelfVisibleWidth(el);
            window.updateMediaTileShelfStableHeight(el);
            restoreStablePosition();
        });
    };

    window.updateMediaTileShelfVisibleWidth(el);
    window.updateMediaTileShelfStableHeight(el);
    window.requestAnimationFrame(function () {
        window.updateMediaTileShelfStableHeight(el);
    });
    el.__swimlaneStableScrollLeft = el.scrollLeft;
    el.__mediaTileShelfScrollGuard = {
        onWheel: onWheel,
        onScroll: onScroll,
        onResize: onResize
    };
    el.classList.add('is-row-scroll-guarded');
    el.addEventListener('wheel', onWheel, { passive: false });
    el.addEventListener('scroll', onScroll, { passive: true });
    window.addEventListener('resize', onResize, { passive: true });
};

window.unregisterMediaTileShelfScrollGuard = function (el) {
    if (!el || !el.__mediaTileShelfScrollGuard) return;

    el.removeEventListener('wheel', el.__mediaTileShelfScrollGuard.onWheel);
    el.removeEventListener('scroll', el.__mediaTileShelfScrollGuard.onScroll);
    window.removeEventListener('resize', el.__mediaTileShelfScrollGuard.onResize);

    if (el.__mediaTileShelfResizeFrame) {
        window.cancelAnimationFrame(el.__mediaTileShelfResizeFrame);
        el.__mediaTileShelfResizeFrame = null;
    }

    var track = el.closest ? el.closest('.media-tile-shelf-track') : el.parentElement;
    if (track) {
        track.style.removeProperty('--media-tile-shelf-visible-width');
        track.style.removeProperty('--media-tile-shelf-arrow-offset');
    }

    el.style.removeProperty('--media-tile-shelf-visible-width');
    el.style.removeProperty('--media-tile-shelf-arrow-offset');
    el.style.removeProperty('--media-tile-row-height');
    el.style.removeProperty('height');
    el.classList.remove('is-row-scroll-guarded');
    el.__mediaTileShelfScrollGuard = null;
    el.__swimlaneAllowScroll = false;
};

// -- Alphabetical Grid scroll-to-letter ---------------------------------

/**
 * Smoothly scrolls the page to an element by its ID.
 * Used by AlphabeticalGrid.razor for the alphabet strip quick-jump.
 *
 * @param {string} elementId - The DOM ID of the target element (e.g. "az-A").
 */
window.scrollToLetter = function (elementId) {
    var el = document.getElementById(elementId);
    if (el) {
        el.scrollIntoView({ behavior: 'smooth', block: 'start' });
    }
};

// -- LibraryItem Card helpers ---------------------------------------------------

window.libraryItemCardHelpers = {
    isNearBottomEdge: function (element, threshold) {
        if (!element) return false;
        var rect = element.getBoundingClientRect();
        return (window.innerHeight - rect.bottom) < threshold;
    }
};

// -- LibraryItem Settings (localStorage) ----------------------------------------

window.libraryItemSettings = {
    getCardSize: function () {
        return parseInt(localStorage.getItem('library-item-card-size') || '80', 10);
    },
    setCardSize: function (size) {
        localStorage.setItem('library-item-card-size', size.toString());
    }
};

window.listenUi = {
    getMode: function () {
        return localStorage.getItem('listen-ui-mode');
    },
    setMode: function (mode) {
        if (!mode) return;
        localStorage.setItem('listen-ui-mode', mode);
    },
    getMusicRoute: function () {
        return localStorage.getItem('listen-ui-music-route');
    },
    setMusicRoute: function (route) {
        if (!route) return;
        localStorage.setItem('listen-ui-music-route', route);
    },
    getSelectedArtist: function () {
        return localStorage.getItem('listen-ui-selected-artist');
    },
    setSelectedArtist: function (artistName) {
        if (!artistName) return;
        localStorage.setItem('listen-ui-selected-artist', artistName);
    },
    getTrackGridColumns: function (viewKey) {
        if (!viewKey) return null;
        return localStorage.getItem('listen-track-grid-columns-' + viewKey);
    },
    setTrackGridColumns: function (viewKey, json) {
        if (!viewKey || !json) return;
        localStorage.setItem('listen-track-grid-columns-' + viewKey, json);
    }
};

window.tuvimaDownloads = {
    fromStream: async function (fileName, streamReference, contentType) {
        const buffer = await streamReference.arrayBuffer();
        const blob = new Blob([buffer], { type: contentType || 'application/zip' });
        const url = URL.createObjectURL(blob);
        const anchor = document.createElement('a');
        anchor.href = url;
        anchor.download = fileName || 'tuvima-backup.zip';
        document.body.appendChild(anchor);
        anchor.click();
        anchor.remove();
        URL.revokeObjectURL(url);
    }
};

window.detailOrigin = (() => {
    const prefix = 'tuvima:detail-origin:';
    let initialized = false;
    let freshRoute = null;
    let navigationEpoch = 0;
    const routeKey = () => `${window.location.pathname}${window.location.search}`;
    // Pages scroll inside the layout shell's main pane (Home, details, most routes),
    // so it is tracked alongside the explicitly tagged lane/section scrollers.
    const scrollTargets = () => {
        const targets = Array.from(document.querySelectorAll('[data-detail-origin-scroll]'))
            .map((element, index) => ({ element, key: element.getAttribute('data-detail-origin-scroll') || String(index) }));
        const shellMain = document.querySelector('.layout-context-sidebar > .context-sidebar-shell__main');
        if (shellMain && !targets.some(target => target.element === shellMain)) {
            targets.push({ element: shellMain, key: 'shell-main' });
        }
        return targets;
    };

    const capture = () => {
        try {
            const active = document.activeElement;
            sessionStorage.setItem(prefix + routeKey(), JSON.stringify({
                scrollY: window.scrollY,
                scrollContainers: scrollTargets()
                    .map(target => ({
                        key: target.key,
                        top: target.element.scrollTop
                    })),
                activeHref: active instanceof HTMLAnchorElement ? active.getAttribute('href') : null,
                capturedAt: Date.now()
            }));
            sessionStorage.setItem(prefix + 'last', routeKey());
        } catch (error) {
            // Navigation remains functional when private browsing or policy
            // prevents this best-effort state from being stored.
            console.debug('Detail origin state could not be captured.', error);
        }
    };

    const restore = () => {
        if (freshRoute === routeKey()) { resetFresh(); freshRoute = null; return; }
        const epoch = navigationEpoch;
        try {
            const raw = sessionStorage.getItem(prefix + routeKey());
            if (!raw) return;
            const state = JSON.parse(raw);
            window.requestAnimationFrame(() => {
                window.setTimeout(() => {
                    if (epoch !== navigationEpoch) return;
                    if (freshRoute === routeKey()) { resetFresh(); return; }
                    window.scrollTo({ top: Number(state.scrollY) || 0, behavior: 'instant' });
                    const scrollContainers = scrollTargets();
                    (state.scrollContainers || []).forEach((position, index) => {
                        const element = (scrollContainers.find(candidate => candidate.key === position.key)
                            || scrollContainers[index])?.element;
                        // Catalogue timelines restore an indexed year, which may
                        // require a different page window. A stale pixel offset
                        // would override that jump and land in the wrong period.
                        if (element && !element.querySelector('.app-timeline')) {
                            element.scrollTop = Number(position.top) || 0;
                        }
                    });
                    if (state.activeHref) {
                        const links = Array.from(document.querySelectorAll('a[href]'));
                        links.find(link => link.getAttribute('href') === state.activeHref)
                            ?.focus({ preventScroll: true });
                    }
                }, 80);
            });
        } catch (error) {
            console.debug('Detail origin state could not be restored.', error);
        }
    };

    const initialize = () => {
        if (initialized) {
            restore();
            return;
        }

        initialized = true;
        document.addEventListener('click', event => {
            const link = event.target instanceof Element
                ? event.target.closest('a[href]')
                : null;
            if (!(link instanceof HTMLAnchorElement)) return;
            const destination = new URL(link.href, window.location.href);
            const destinationRoute = `${destination.pathname}${destination.search}`;
            if (destination.origin === window.location.origin
                && destination.pathname.startsWith('/details/')
                && destinationRoute !== routeKey()) {
                // Remember where we came from (for Back) and open the details page at its top.
                fresh(destinationRoute);
            }
        }, true);
        window.addEventListener('popstate', () => { freshRoute = null; window.setTimeout(restore, 0); });
        restore();
    };

    const back = fallback => {
        try {
            const last = sessionStorage.getItem(prefix + 'last');
            if (last && last !== routeKey()) {
                window.history.back();
                return;
            }
        } catch (error) {
            console.debug('Detail origin state could not be read.', error);
        }
        window.location.assign(fallback || '/');
    };

    // A title opens in the details overlay, which scrolls inside its own scroller. Only that scroller goes back to
    // the top: the page under the overlay keeps its place for when the overlay is dismissed.
    const freshScrollSelector = '[data-detail-origin-scroll="details-overlay"]';
    // Sections of a details page (progress, series, credits) arrive after it opens. Each arrival used to put the
    // scroller back at the top, which fought anyone who had already started scrolling. Once the person scrolls,
    // touches or presses a key, the page is theirs and later arrivals no longer move it.
    let personTookOver = false;
    const resetFresh = () => {
        if (personTookOver) return;
        document.querySelectorAll(freshScrollSelector).forEach(element => { element.scrollTop = 0; });
    };
    const takeoverEvents = ['wheel', 'touchmove', 'keydown', 'pointerdown'];
    const onTakeover = () => { personTookOver = true; };
    const fresh = route => {
        capture();
        navigationEpoch++;
        personTookOver = false;
        const target = new URL(route, window.location.href);
        freshRoute = `${target.pathname}${target.search}`;
        takeoverEvents.forEach(name => document.addEventListener(name, onTakeover, true));
        // Keep the intent through asynchronous page mounting and its origin-restore callback.
        const observer = new MutationObserver(() => { if (routeKey() === freshRoute) resetFresh(); });
        observer.observe(document.body, { childList: true, subtree: true });
        requestAnimationFrame(() => { if (routeKey() === freshRoute) resetFresh(); });
        const epoch = navigationEpoch;
        setTimeout(() => {
            if (epoch === navigationEpoch) { if (routeKey() === freshRoute) resetFresh(); freshRoute = null; }
            observer.disconnect();
            if (epoch === navigationEpoch) takeoverEvents.forEach(name => document.removeEventListener(name, onTakeover, true));
        }, 1000);
    };
    return { initialize, capture, restore, back, fresh };
})();

// Dismissing the details overlay steps the browser back over every title opened inside it (the entries are tracked in
// DetailsOverlayState, which records how many lie between the overlay and the page under it).
window.detailsOverlay = {
    goBack: function (steps) {
        const count = Math.floor(Number(steps));
        if (count > 0) window.history.go(-count);
    }
};

// Register before the first detail link is clicked so the originating lane
// position can be captured on the initial navigation.
window.detailOrigin.initialize();

// Scroll the existing episode rail without changing the rendered item set.
window.scrollSequenceItem = (id, index, align = 'nearest', behavior = 'smooth') => {
    const rail = document.getElementById(id);
    const item = rail?.querySelector(`[data-sequence-index="${index}"]`) ?? rail?.children[index];
    if (!rail || !item) return;

    const unclampedLeft = align === 'center'
        ? rail.scrollLeft + item.getBoundingClientRect().left - rail.getBoundingClientRect().left - ((rail.clientWidth - item.clientWidth) / 2)
        : index === 0 ? 0 : item.offsetLeft - rail.offsetLeft - 8;
    const maximumLeft = Math.max(0, rail.scrollWidth - rail.clientWidth);
    rail.scrollTo({ left: Math.max(0, Math.min(maximumLeft, unclampedLeft)), behavior });
};

window.scrollSequenceRail = (id, direction) => {
    const rail = document.getElementById(id);
    if (!rail) return;
    const distance = Math.max(240, rail.clientWidth * 0.82) * (direction < 0 ? -1 : 1);
    rail.scrollBy({ left: distance, behavior: 'smooth' });
};

// Rich selectors use the same keyboard contract even when AppPopover portals them.
window.tuvimaFocusMenu = function (id) {
    requestAnimationFrame(function () {
        const menu = document.getElementById(id);
        menu?.querySelector('input:not(:disabled), [role="menuitem"]:not(:disabled)')?.focus();
    });
};
document.addEventListener('keydown', function (event) {
    const menu = event.target.closest?.('.app-overflow-menu__content');
    if (!menu || !['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) return;
    if (event.target.matches('input, textarea') && ['Home', 'End'].includes(event.key)) return;
    const options = Array.from(menu.querySelectorAll('[role="menuitem"]:not(:disabled)'));
    if (!options.length) return;
    event.preventDefault();
    const current = options.indexOf(document.activeElement);
    const index = event.key === 'Home' ? 0 : event.key === 'End' ? options.length - 1
        : event.key === 'ArrowDown' ? (current + 1) % options.length
        : current <= 0 ? options.length - 1 : current - 1;
    options[index].focus();
});
// AppTable cards mode re-lays rows out with display:block/grid, which some engines treat as dropping table
// semantics. Explicit ARIA roles keep the table navigable by screen readers; the observer keeps them current
// when Blazor adds or replaces rows.
window.tuvimaTableCards = (function () {
    const observers = new WeakMap();
    const roles = { TABLE: 'table', THEAD: 'rowgroup', TBODY: 'rowgroup', TFOOT: 'rowgroup', TR: 'row', TH: 'columnheader', TD: 'cell' };
    function applyRoles(table) {
        if (!table.hasAttribute('role')) table.setAttribute('role', roles.TABLE);
        for (const node of table.querySelectorAll('thead, tbody, tfoot, tr, th, td')) {
            if (!node.hasAttribute('role')) node.setAttribute('role', roles[node.tagName]);
        }
    }
    return {
        apply: function (table) {
            if (!(table instanceof HTMLElement)) return;
            applyRoles(table);
            if (observers.has(table)) return;
            const observer = new MutationObserver(() => applyRoles(table));
            observer.observe(table, { childList: true, subtree: true });
            observers.set(table, observer);
        }
    };
})();

window.tuvimaGetEditorListScrollTop = function (element) {
    return element?.scrollTop ?? 0;
};

window.tuvimaSetEditorListScrollTop = function (element, scrollTop) {
    if (element) element.scrollTop = scrollTop || 0;
};

window.tuvimaArtwork = {
    openFullSize: function (source) {
        const url = new URL(source, window.location.origin);
        url.searchParams.delete('size');
        if (url.protocol !== 'https:' && url.protocol !== 'http:' && url.protocol !== 'blob:') return;
        window.open(url.href, '_blank', 'noopener');
    }
};

window.tuvimaMenu = { hasFocus: function (element) { return Promise.resolve().then(() => element.contains(document.activeElement)); }, move: function (element, key) {
    const root = element.closest('[role="menu"],.media-rate-control,.playback-panel__body') || element;
    const items = Array.from(root.querySelectorAll('[role="menuitem"],[role="menuitemcheckbox"]')).filter(item => !item.disabled);
    if (!items.length) return;
    const current = items.indexOf(document.activeElement);
    const index = key === 'Home' ? 0 : key === 'End' ? items.length - 1 : (current + (key === 'ArrowUp' || key === 'ArrowLeft' ? -1 : 1) + items.length) % items.length;
    items[index].focus();
} };

// The launch splash (first-run setup rework D4): the short intro animation is drawn by the server page and removed as soon as the app has rendered.
// A timer removes it anyway after 12 seconds, so a failed start never leaves it covering the page.
(function () {
    function remove() {
        var splash = document.getElementById('tl-launch-splash');
        if (!splash) return;
        splash.classList.add('tl-launch-splash--done');
        setTimeout(function () { if (splash.parentNode) splash.parentNode.removeChild(splash); }, 250);
    }
    window.tuvimaDismissLaunchSplash = remove;
    setTimeout(remove, 12000);
})();
