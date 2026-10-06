// app.js — Global JavaScript helpers for the Dashboard

/**
 * Ensures the dark theme class is applied to <body>.
 * Called from MainLayout on first render. Light mode has been removed.
 */
window.setThemeClass = function () {
    document.body.classList.add('app-dark');
};

window.tuvimaPrefersReducedMotion = function () {
    return !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
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

window.tuvimaFocusById = function (id) {
    const target = id && document.getElementById(id);
    if (target) target.focus({ preventScroll: true });
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
    var observerOwnerKey = null;

    function readBreakpoint(name, fallback) {
        var value = window.getComputedStyle(document.documentElement)
            .getPropertyValue('--tl-breakpoint-' + name)
            .trim();
        var parsed = Number(value);
        return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
    }

    function classifyViewport() {
        var breakpoint = readBreakpoint('navigation', 840);
        if (window.matchMedia) {
            return window.matchMedia('(max-width: ' + breakpoint + 'px)').matches ? 'mobile' : 'web';
        }
        return window.innerWidth <= breakpoint ? 'mobile' : 'web';
    }

    function register(dotNetRef, ownerKey) {
        unregister();
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
        var next = classifyViewport();
        if (next === pendingObservedClass
            || next === lastObservedClass && pendingObservedClass === null) return;

        var version = ++notificationVersion;
        pendingObservedClass = next;
        try {
            Promise.resolve(activeObserver.invokeMethodAsync('HandleViewportDeviceClassChanged', next))
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
        return Array.prototype.filter.call(el.querySelectorAll('.media-tile, .media-group-tile, .recent-view-card'), function (item) {
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
    if (el.closest && el.closest('.continue-group')) {
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
    Array.prototype.forEach.call(el.querySelectorAll('.media-tile, .media-group-tile, .recent-view-card'), function (tile) {
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

window.registerMediaTileShelfScrollGuard = function (el) {
    if (!el || el.__mediaTileShelfScrollGuard) return;

    var isFinePointer = function () {
        return !window.matchMedia || window.matchMedia('(hover: hover) and (pointer: fine)').matches;
    };

    var restoreStablePosition = function () {
        if (!isFinePointer()) return;

        if (!el.__swimlaneAllowScroll && !el.__mediaTileHoverScrollLock) {
            var stableLeft = el.__swimlaneStableScrollLeft || 0;
            if (Math.abs(el.scrollLeft - stableLeft) > 1) {
                el.scrollLeft = stableLeft;
            }
        }

        if (Math.abs((el.scrollTop || 0)) > 1) {
            el.scrollTop = 0;
        }
    };

    var onWheel = function (event) {
        if (!isFinePointer()) return;

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

// -- Media tile hover positioning ------------------------------------

window.getMediaTileHoverHost = function () {
    var host = document.getElementById('media-tile-hover-host');
    if (host) return host;

    host = document.createElement('div');
    host.id = 'media-tile-hover-host';
    host.className = 'media-tile-hover-host';
    document.body.appendChild(host);
    return host;
};

window.positionMediaTileHover = function (cardEl) {
    if (!cardEl) return;

    if (cardEl.__mediaTileHoverFrame) {
        cardEl.__mediaTileHoverNeedsReposition = true;
        return;
    }

    cardEl.__mediaTileHoverFrame = window.requestAnimationFrame(function () {
        cardEl.__mediaTileHoverFrame = null;
        var panel = cardEl.__mediaTileHoverPanel || cardEl.querySelector('.media-tile-hover-panel');
        if (!panel) return;

        var frame = cardEl.querySelector('.media-tile-frame') || cardEl;
        var cardRect = frame.getBoundingClientRect();
        var viewportWidth = document.documentElement.clientWidth || window.innerWidth;
        var viewportHeight = document.documentElement.clientHeight || window.innerHeight;
        var gutter = 12;
        if (panel.classList.contains('is-media-movie') || panel.classList.contains('is-media-tv')) {
            panel.style.setProperty('--media-tile-expanded-width', Math.min(cardRect.height * 16 / 9, viewportWidth - gutter * 2) + 'px');
        }
        panel.style.removeProperty('--media-tile-hover-left');
        panel.style.removeProperty('--media-tile-hover-top');
        panel.style.removeProperty('--media-tile-hover-max-height');
        panel.style.removeProperty('--media-tile-hover-art-max-height');
        panel.style.setProperty('--media-tile-hover-anchor-width', Math.round(cardRect.width) + 'px');
        panel.style.setProperty('--media-tile-hover-anchor-height', cardRect.height + 'px');
        panel.style.setProperty('--media-tile-hover-max-height', Math.max(0, viewportHeight - gutter * 2) + 'px');
        panel.style.left = '';
        panel.style.top = '';

        var body = panel.querySelector('.media-tile-hover-body');
        var bodyHeight = body ? body.getBoundingClientRect().height : 0;
        panel.style.setProperty('--media-tile-hover-art-max-height', Math.max(0, viewportHeight - gutter * 2 - bodyHeight) + 'px');

        var panelRect = panel.getBoundingClientRect();
        var panelStyle = window.getComputedStyle ? window.getComputedStyle(panel) : null;
        var panelWidth = panelRect.width || panel.offsetWidth;
        var panelHeight = panelRect.height || panel.offsetHeight;
        if (!panelWidth) {
            panelWidth = cardRect.width;
        }
        if (!panelHeight) {
            panelHeight = cardRect.height;
        }

        var isWatchPreview = panel.classList.contains('is-media-movie') || panel.classList.contains('is-media-tv');
        var estimatedPanelHeight = isWatchPreview ? cardRect.height : panelHeight;
        if (!isWatchPreview && panelWidth > 0 && panel.classList.contains('is-banner-popover')) {
            estimatedPanelHeight = Math.max(estimatedPanelHeight, (panelWidth * 9 / 16) + bodyHeight + 8);
        } else if (!isWatchPreview && panelWidth > 0 && panel.classList.contains('is-art-popover')) {
            var estimatedArtHeight = panelWidth;
            if (panel.classList.contains('is-portrait')) {
                estimatedArtHeight = panelWidth * 1.5;
            }

            estimatedPanelHeight = Math.max(estimatedPanelHeight, estimatedArtHeight + bodyHeight + 8);
        }

        var rawPanelMaxHeight = panelStyle ? parseFloat(panelStyle.maxHeight || '0') : 0;
        if (Number.isFinite(rawPanelMaxHeight) && rawPanelMaxHeight > 0) {
            estimatedPanelHeight = Math.min(estimatedPanelHeight, rawPanelMaxHeight);
        }

        panelHeight = isWatchPreview ? cardRect.height : Math.max(panelHeight, Math.min(estimatedPanelHeight, viewportHeight - (gutter * 2)));

        // Expand from the resting card's centre, like a streaming-service preview,
        // while keeping the shelf itself completely stationary.
        var cardCenterX = cardRect.left + (cardRect.width / 2);
        var panelLeft = cardCenterX - (panelWidth / 2);
        var minLeft = gutter;
        var maxLeft = viewportWidth - gutter - panelWidth;
        if (maxLeft < minLeft) {
            panelLeft = minLeft;
        } else {
            panelLeft = Math.min(Math.max(panelLeft, minLeft), maxLeft);
        }

        var panelTop = cardRect.top;
        var maxTop = viewportHeight - gutter - panelHeight;
        if (maxTop < gutter) {
            panelTop = gutter;
        } else {
            panelTop = Math.min(Math.max(panelTop, gutter), maxTop);
        }

        panel.style.setProperty('--media-tile-hover-left', Math.round(panelLeft) + 'px');
        panel.style.setProperty('--media-tile-hover-top', Math.round(panelTop) + 'px');
        panel.style.setProperty('--media-tile-hover-origin-x', Math.round(cardCenterX - panelLeft) + 'px');
        panel.classList.add('is-positioned');

        if (cardEl.__mediaTileHoverNeedsReposition) {
            cardEl.__mediaTileHoverNeedsReposition = false;
            window.positionMediaTileHover(cardEl);
        }
    });
};

window.correctMediaTileHoverViewport = function (cardEl) {
    if (!cardEl) return;

    var panel = cardEl.__mediaTileHoverPanel || cardEl.querySelector('.media-tile-hover-panel');
    if (!panel || !panel.classList.contains('is-visible')) return;

    window.positionMediaTileHover(cardEl);
};

window.scheduleMediaTileHoverViewportCorrection = function (cardEl) {
    window.requestAnimationFrame(function () {
        window.correctMediaTileHoverViewport(cardEl);
        window.requestAnimationFrame(function () {
            window.correctMediaTileHoverViewport(cardEl);
        });
    });
};

window.mountMediaTileHover = function (cardEl) {
    if (!cardEl) return null;

    var panel = cardEl.__mediaTileHoverPanel || cardEl.querySelector('.media-tile-hover-panel');
    if (!panel) return null;

    if (!panel.__mediaTileHoverOriginalParent) {
        panel.__mediaTileHoverOriginalParent = panel.parentElement;
    }

    if (!Object.prototype.hasOwnProperty.call(panel, '__mediaTileHoverOriginalNextSibling')) {
        panel.__mediaTileHoverOriginalNextSibling = panel.nextSibling;
    }

    if (window.getComputedStyle) {
        var cardStyle = window.getComputedStyle(cardEl);
        [
            '--media-tile-accent',
            '--media-tile-secondary-accent',
            '--media-tile-action-accent',
            '--media-tile-progress-accent',
            '--art-bg-base',
            '--art-bg-base-dark',
            '--art-bg-accent',
            '--art-bg-accent-muted',
            '--art-bg-glow',
            '--art-bg-glow-secondary',
            '--art-bg-border',
            '--art-bg-shadow',
            '--art-bg-overlay',
            '--media-tile-hover-image'
        ].forEach(function (propertyName) {
            var propertyValue = cardStyle.getPropertyValue(propertyName);
            if (propertyValue) {
                panel.style.setProperty(propertyName, propertyValue.trim());
            }
        });
    }

    panel.classList.remove('is-visible');
    panel.classList.remove('is-positioned');
    panel.classList.add('is-viewport-mounted');
    var mountParent = window.getMediaTileHoverHost();
    if (mountParent && panel.parentElement !== mountParent) mountParent.appendChild(panel);
    return panel;
};

window.restoreMediaTileHover = function (cardEl) {
    if (!cardEl) return;

    var panel = cardEl.querySelector('.media-tile-hover-panel') || cardEl.__mediaTileHoverPanel;
    if (!panel || !panel.__mediaTileHoverOriginalParent) return;

    var originalParent = panel.__mediaTileHoverOriginalParent;
    var originalNextSibling = panel.__mediaTileHoverOriginalNextSibling;

    if (originalNextSibling && originalNextSibling.parentNode === originalParent) {
        originalParent.insertBefore(panel, originalNextSibling);
    } else {
        originalParent.appendChild(panel);
    }

    panel.classList.remove('is-viewport-mounted');
    panel.classList.remove('is-positioned');
};

window.lockMediaTileHoverRowScroll = function (cardEl) {
    if (!cardEl) return;

    var scrollEl = cardEl.closest('.media-tile-shelf-scroll');
    if (!scrollEl) return;

    cardEl.__mediaTileHoverScrollElement = scrollEl;

    if (!scrollEl.__mediaTileHoverScrollLock) {
        scrollEl.__mediaTileHoverScrollLock = {
            owner: cardEl,
            left: scrollEl.scrollLeft,
            top: scrollEl.scrollTop || 0
        };

        var restore = function () {
            var lock = scrollEl.__mediaTileHoverScrollLock;
            if (!lock) return;
            if (scrollEl.__swimlaneAllowScroll) return;

            if (scrollEl.scrollLeft !== lock.left) {
                scrollEl.scrollLeft = lock.left;
            }

            if ((scrollEl.scrollTop || 0) !== lock.top) {
                scrollEl.scrollTop = lock.top;
            }
        };

        var blockWheel = function (event) {
            var lock = scrollEl.__mediaTileHoverScrollLock;
            if (!lock) return;

            if (window.isVerticalMediaTileWheel(event)) {
                window.requestAnimationFrame(restore);
                return;
            }

            event.preventDefault();
            event.stopPropagation();

            window.requestAnimationFrame(restore);
        };

        scrollEl.__mediaTileHoverScrollRestore = restore;
        scrollEl.__mediaTileHoverWheelBlock = blockWheel;
        scrollEl.addEventListener('scroll', restore, { passive: true });
        scrollEl.addEventListener('wheel', blockWheel, { passive: false });
    } else {
        scrollEl.__mediaTileHoverScrollLock.owner = cardEl;
        scrollEl.__mediaTileHoverScrollLock.left = scrollEl.scrollLeft;
        scrollEl.__mediaTileHoverScrollLock.top = scrollEl.scrollTop || 0;
    }

    scrollEl.classList.add('is-hover-scroll-locked');

    var panel = cardEl.__mediaTileHoverPanel || cardEl.querySelector('.media-tile-hover-panel');
    if (panel && !panel.__mediaTileHoverWheelBlock) {
        panel.__mediaTileHoverWheelBlock = function (event) {
            var lock = scrollEl.__mediaTileHoverScrollLock;
            if (lock) {
                window.requestAnimationFrame(function () {
                    if (scrollEl.scrollLeft !== lock.left && !scrollEl.__swimlaneAllowScroll) {
                        scrollEl.scrollLeft = lock.left;
                    }

                    if ((scrollEl.scrollTop || 0) !== lock.top) {
                        scrollEl.scrollTop = lock.top;
                    }
                });
            }

            if (window.isVerticalMediaTileWheel(event)) {
                return;
            }

            event.preventDefault();
            event.stopPropagation();
        };
        panel.addEventListener('wheel', panel.__mediaTileHoverWheelBlock, { passive: false });
    }
};

window.unlockMediaTileHoverRowScroll = function (cardEl) {
    if (!cardEl) return;

    var scrollEl = cardEl.__mediaTileHoverScrollElement;
    if (!scrollEl || !scrollEl.__mediaTileHoverScrollLock) return;

    if (scrollEl.__mediaTileHoverScrollLock.owner !== cardEl) return;

    if (scrollEl.__mediaTileHoverScrollRestore) {
        scrollEl.removeEventListener('scroll', scrollEl.__mediaTileHoverScrollRestore);
    }
    if (scrollEl.__mediaTileHoverWheelBlock) {
        scrollEl.removeEventListener('wheel', scrollEl.__mediaTileHoverWheelBlock);
    }

    var panel = cardEl.__mediaTileHoverPanel || cardEl.querySelector('.media-tile-hover-panel');
    if (panel && panel.__mediaTileHoverWheelBlock) {
        panel.removeEventListener('wheel', panel.__mediaTileHoverWheelBlock);
        panel.__mediaTileHoverWheelBlock = null;
    }

    scrollEl.classList.remove('is-hover-scroll-locked');
    scrollEl.__mediaTileHoverScrollLock = null;
    scrollEl.__mediaTileHoverScrollRestore = null;
    scrollEl.__mediaTileHoverWheelBlock = null;
    cardEl.__mediaTileHoverScrollElement = null;
};

window.keepMediaTileHoverInRowViewport = function (cardEl) {
    if (!cardEl) return;

    var row = cardEl.closest('.media-tile-shelf-scroll');
    if (!row) return;

    var cardRect = cardEl.getBoundingClientRect();
    var rowRect = row.getBoundingClientRect();
    var gutter = 8;
    var delta = 0;

    if (cardRect.right > rowRect.right - gutter) {
        delta = cardRect.right - rowRect.right + gutter;
    } else if (cardRect.left < rowRect.left + gutter) {
        delta = cardRect.left - rowRect.left - gutter;
    }

    if (Math.abs(delta) < 1) return;

    row.__swimlaneAllowScroll = true;
    row.scrollTo({ left: row.scrollLeft + delta, behavior: 'smooth' });

    if (row.__mediaTileHoverEdgeTimer) {
        window.clearTimeout(row.__mediaTileHoverEdgeTimer);
    }

    row.__mediaTileHoverEdgeTimer = window.setTimeout(function () {
        row.__swimlaneStableScrollLeft = row.scrollLeft;
        row.__swimlaneAllowScroll = false;
        row.__mediaTileHoverEdgeTimer = null;
    }, 360);
};

window.showMediaTileHover = function (cardEl) {
    if (!cardEl) return;
    var row = cardEl.closest('.media-tile-shelf-scroll');
    if (!row) return;
    var previous = row.querySelector('.media-tile.is-hover-active');
    if (previous && previous !== cardEl) window.clearMediaTileHover(previous);
    var panel = cardEl.querySelector('.media-tile-hover-panel');
    if (!panel) return;
    cardEl.__mediaTileHoverPanel = panel;
    if (cardEl.__mediaTileHideTimer) {
        window.clearTimeout(cardEl.__mediaTileHideTimer);
        cardEl.__mediaTileHideTimer = null;
    }
    var frame = cardEl.querySelector('.media-tile-frame') || cardEl;
    var rect = frame.getBoundingClientRect();
    if (!Number.isFinite(rect.height) || rect.height <= 16 || rect.width <= 16) return;
    var expandedWidth = Math.min(rect.height * 16 / 9, Math.max(0, window.innerWidth - 24));
    cardEl.style.setProperty('--media-tile-hover-anchor-width', rect.width + 'px');
    cardEl.style.setProperty('--media-tile-hover-anchor-height', rect.height + 'px');
    cardEl.style.setProperty('--media-tile-expanded-width', Math.round(expandedWidth) + 'px');
    row.classList.add('has-active-in-row-hover');
    cardEl.classList.add('is-hover-active');
    // Keep the preview inside the expanding card so its neighbours move in the row.
    panel.classList.add('is-inline-expanded');
    var image = panel.querySelector('.media-tile-hover-image');
    if (image) { image.loading = 'eager'; if ('fetchPriority' in image) image.fetchPriority = 'high'; }
    window.requestAnimationFrame(function () {
        window.requestAnimationFrame(function () {
            if (!cardEl.classList.contains('is-hover-active')) return;
            panel.classList.add('is-visible');
            window.setTimeout(function () {
                if (cardEl.classList.contains('is-hover-active')) window.keepMediaTileHoverInRowViewport(cardEl);
            }, 300);
        });
    });
};

window.clearMediaTileHover = function (cardEl) {
    if (!cardEl) return;

    var panel = cardEl.__mediaTileHoverPanel || cardEl.querySelector('.media-tile-hover-panel');
    cardEl.classList.remove('is-hover-active');

    if (cardEl.__mediaTileShowTimer) {
        window.clearTimeout(cardEl.__mediaTileShowTimer);
        cardEl.__mediaTileShowTimer = null;
    }

    if (!panel) return;

    panel.classList.remove('is-visible');
    panel.classList.remove('is-inline-expanded');
    var wasGridOverlay = panel.classList.contains('is-grid-overlay');

    window.setTimeout(function () {
        if (cardEl.classList.contains('is-hover-active') || panel.classList.contains('is-visible')) {
            return;
        }

        var restingFrame = cardEl.querySelector('.media-tile-frame') || cardEl;
        var restingFrameRect = restingFrame.getBoundingClientRect();
        if (restingFrameRect.width > 16 && restingFrameRect.height > 16) {
            cardEl.style.setProperty('--media-tile-hover-anchor-width', Math.round(restingFrameRect.width) + 'px');
            cardEl.style.setProperty('--media-tile-hover-anchor-height', Math.round(restingFrameRect.height) + 'px');
        }
        cardEl.style.removeProperty('--media-tile-expanded-width');
        if (wasGridOverlay) {
            panel.classList.remove('is-grid-overlay');
            panel.style.removeProperty('--media-tile-expanded-width');
            cardEl.classList.remove('is-grid-overlay-anchor');
            window.restoreMediaTileHover(cardEl);
        }
        var row = cardEl.closest('.media-tile-shelf-scroll, .media-tile-grid');
        if (row && !row.querySelector('.media-tile.is-hover-active')) {
            row.classList.remove('has-active-in-row-hover');
        }
    }, 380);
};

window.registerMediaTileHover = function (cardEl) {
    if (!cardEl || cardEl.__mediaTileHoverRegistered) return;

    cardEl.classList.add('is-hover-js-enabled');
    if (cardEl.closest('.media-tile-grid')) {
        cardEl.classList.add('is-grid-hover-tile');
    }

    // Cache the resting geometry so cinematic expansion can preserve the shelf layout.
    var restingFrame = cardEl.querySelector('.media-tile-frame') || cardEl;
    var restingFrameRect = restingFrame.getBoundingClientRect();
    if (restingFrameRect.width > 16 && restingFrameRect.height > 16) {
        cardEl.style.setProperty('--media-tile-hover-anchor-width', Math.round(restingFrameRect.width) + 'px');
        cardEl.style.setProperty('--media-tile-hover-anchor-height', Math.round(restingFrameRect.height) + 'px');
    }

    var show = function (event) {
        if (window.matchMedia && !window.matchMedia('(hover: hover) and (pointer: fine)').matches) return;
        if (event && event.type === 'focusin' && cardEl.__mediaTilePointerFocus) return;
        if (cardEl.__mediaTileHideTimer) {
            window.clearTimeout(cardEl.__mediaTileHideTimer);
            cardEl.__mediaTileHideTimer = null;
        }

        if (cardEl.__mediaTileShowTimer) {
            window.clearTimeout(cardEl.__mediaTileShowTimer);
        }

        var row = cardEl.closest('.media-tile-shelf-scroll, .media-tile-grid');
        var activeCard = row ? row.querySelector('.media-tile.is-hover-active') : null;
        var showDelay = 200;

        cardEl.__mediaTileShowTimer = window.setTimeout(function () {
            cardEl.__mediaTileShowTimer = null;
            window.showMediaTileHover(cardEl);
        }, showDelay);
    };

    var isWithinHoverSurface = function (target) {
        if (!target) return false;

        var activePanel = cardEl.__mediaTileHoverPanel || panel;
        return cardEl.contains(target) || (activePanel && activePanel.contains(target));
    };

    var isHoverSurfaceStillActive = function () {
        var activePanel = cardEl.__mediaTileHoverPanel || panel;
        var activeElement = document.activeElement;

        return (cardEl.matches && cardEl.matches(':hover'))
            || (activePanel && activePanel.matches && activePanel.matches(':hover'))
            || isWithinHoverSurface(activeElement);
    };

    var scheduleHide = function (event) {
        if (event && isWithinHoverSurface(event.relatedTarget)) return;

        if (cardEl.__mediaTileShowTimer) {
            window.clearTimeout(cardEl.__mediaTileShowTimer);
            cardEl.__mediaTileShowTimer = null;
        }

        if (cardEl.__mediaTileHideTimer) {
            window.clearTimeout(cardEl.__mediaTileHideTimer);
        }

        cardEl.__mediaTileHideTimer = window.setTimeout(function () {
            if (isHoverSurfaceStillActive()) {
                return;
            }

            window.clearMediaTileHover(cardEl);
        }, 110);
    };

    var keepOpen = function () {
        if (cardEl.__mediaTileShowTimer) {
            window.clearTimeout(cardEl.__mediaTileShowTimer);
            cardEl.__mediaTileShowTimer = null;
        }

        if (cardEl.__mediaTileHideTimer) {
            window.clearTimeout(cardEl.__mediaTileHideTimer);
            cardEl.__mediaTileHideTimer = null;
        }
    };

    const pointerFocus = () => { cardEl.__mediaTilePointerFocus = true; };
    const keyboardFocus = () => { cardEl.__mediaTilePointerFocus = false; };
    cardEl.addEventListener('pointerdown', pointerFocus);
    cardEl.addEventListener('keydown', keyboardFocus);
    cardEl.__mediaTilePointerFocusHandler = pointerFocus;
    cardEl.__mediaTileKeyboardFocusHandler = keyboardFocus;
    cardEl.addEventListener('mouseenter', show);
    cardEl.addEventListener('focusin', show);
    cardEl.addEventListener('mouseleave', scheduleHide);
    cardEl.addEventListener('focusout', scheduleHide);

    var panel = cardEl.querySelector('.media-tile-hover-panel');
    if (panel) {
        panel.addEventListener('mouseenter', keepOpen);
        panel.addEventListener('focusin', keepOpen);
        panel.addEventListener('mouseleave', scheduleHide);
        panel.addEventListener('focusout', scheduleHide);
    }

    cardEl.__mediaTileHoverShow = show;
    cardEl.__mediaTileHoverHide = scheduleHide;
    cardEl.__mediaTileHoverKeepOpen = keepOpen;
    cardEl.__mediaTileHoverRegistered = true;
};

window.unregisterMediaTileHover = function (cardEl) {
    if (!cardEl || !cardEl.__mediaTileHoverRegistered) return;
    cardEl.removeEventListener('pointerdown', cardEl.__mediaTilePointerFocusHandler);
    cardEl.removeEventListener('keydown', cardEl.__mediaTileKeyboardFocusHandler);

    var panel = cardEl.__mediaTileHoverPanel || cardEl.querySelector('.media-tile-hover-panel');

    if (cardEl.__mediaTileHoverShow) {
        cardEl.removeEventListener('mouseenter', cardEl.__mediaTileHoverShow);
        cardEl.removeEventListener('focusin', cardEl.__mediaTileHoverShow);
    }

    if (cardEl.__mediaTileHoverHide) {
        cardEl.removeEventListener('mouseleave', cardEl.__mediaTileHoverHide);
        cardEl.removeEventListener('focusout', cardEl.__mediaTileHoverHide);
    }

    if (panel && cardEl.__mediaTileHoverKeepOpen) {
        panel.removeEventListener('mouseenter', cardEl.__mediaTileHoverKeepOpen);
        panel.removeEventListener('focusin', cardEl.__mediaTileHoverKeepOpen);
        panel.removeEventListener('mouseleave', cardEl.__mediaTileHoverHide);
        panel.removeEventListener('focusout', cardEl.__mediaTileHoverHide);
    }

    if (cardEl.__mediaTileHideTimer) {
        window.clearTimeout(cardEl.__mediaTileHideTimer);
    }
    if (cardEl.__mediaTileShowTimer) {
        window.clearTimeout(cardEl.__mediaTileShowTimer);
    }
    cardEl.classList.remove('is-hover-js-enabled');
    cardEl.classList.remove('is-grid-hover-tile');
    window.clearMediaTileHover(cardEl);

    delete cardEl.__mediaTileHoverPanel;
    delete cardEl.__mediaTileHoverShow;
    delete cardEl.__mediaTileHoverHide;
    delete cardEl.__mediaTileHoverKeepOpen;
    delete cardEl.__mediaTileShowTimer;
    delete cardEl.__mediaTileHideTimer;
    delete cardEl.__mediaTileHoverRegistered;
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

window.tuvimaPopupStateSync = (function () {
    function getLatestState(stateKey) {
        var openerOwnsState = false;
        try {
            var opener = window.opener;
            if (opener && !opener.closed
                && opener.location.origin === window.location.origin
                && opener.listenPlayback
                && typeof opener.listenPlayback.getStoredState === 'function') {
                openerOwnsState = true;
                return opener.listenPlayback.getStoredState();
            }
        } catch (_) {
            // Cross-origin or isolated opener access falls back to same-origin storage.
        }

        if (openerOwnsState) return null;

        try {
            return window.localStorage.getItem(stateKey);
        } catch (_) {
            return null;
        }
    }

    function requestLatestState(sendCommand) {
        if (typeof sendCommand !== 'function') return;
        sendCommand(JSON.stringify({ action: 'request-state' }));
    }

    return { getLatestState: getLatestState, requestLatestState: requestLatestState };
})();

window.listenPlayback = (function () {
    var playbackConfig = {
        popupWidth: 420,
        popupHeight: 780,
        immediateActionDedupMilliseconds: 900,
        immediateActionConsumeMilliseconds: 1800,
        audioObserverIntervalMilliseconds: 1200,
        audioObserverMinimumIntervalMilliseconds: 500,
        seekToleranceSeconds: 0.75,
        volumeStep: 0.05,
        defaultVolume: 0.8
    };
    var stateKey = 'tuvima.playback.v2.state';
    var commandKey = 'tuvima.playback.v2.command';
    var popupName = 'tuvima-listen-mini-player';
    var channel = typeof BroadcastChannel !== 'undefined' ? new BroadcastChannel('tuvima-listen-playback') : null;
    var stateHandler = null;
    var commandHandler = null;
    var popupWindow = null;
    var popupWindowId = null;
    var popupUnloadHandler = null;
    var audioObservers = typeof WeakMap !== 'undefined' ? new WeakMap() : null;
    var playerShortcutHandlers = typeof WeakMap !== 'undefined' ? new WeakMap() : null;

    function readConfigValue(options, camelName, snakeName, fallback) {
        if (!options) return fallback;
        if (Object.prototype.hasOwnProperty.call(options, camelName)) return options[camelName];
        var pascalName = camelName.charAt(0).toUpperCase() + camelName.slice(1);
        if (Object.prototype.hasOwnProperty.call(options, pascalName)) return options[pascalName];
        if (snakeName && Object.prototype.hasOwnProperty.call(options, snakeName)) return options[snakeName];
        return fallback;
    }

    function asFiniteNumber(value, fallback, min, max) {
        var next = typeof value === 'number' ? value : Number.parseFloat(value);
        if (!Number.isFinite(next)) return fallback;
        if (typeof min === 'number') next = Math.max(min, next);
        if (typeof max === 'number') next = Math.min(max, next);
        return next;
    }

    function configure(options) {
        playbackConfig.popupWidth = asFiniteNumber(readConfigValue(options, 'popupWidth', 'popup_width', playbackConfig.popupWidth), playbackConfig.popupWidth, 280, 1200);
        playbackConfig.popupHeight = asFiniteNumber(readConfigValue(options, 'popupHeight', 'popup_height', playbackConfig.popupHeight), playbackConfig.popupHeight, 360, 1400);
        playbackConfig.immediateActionDedupMilliseconds = asFiniteNumber(readConfigValue(options, 'immediateActionDedupMilliseconds', 'immediate_action_dedup_milliseconds', playbackConfig.immediateActionDedupMilliseconds), playbackConfig.immediateActionDedupMilliseconds, 100, 5000);
        playbackConfig.immediateActionConsumeMilliseconds = asFiniteNumber(readConfigValue(options, 'immediateActionConsumeMilliseconds', 'immediate_action_consume_milliseconds', playbackConfig.immediateActionConsumeMilliseconds), playbackConfig.immediateActionConsumeMilliseconds, 100, 10000);
        playbackConfig.audioObserverIntervalMilliseconds = asFiniteNumber(readConfigValue(options, 'audioObserverIntervalMilliseconds', 'audio_observer_interval_milliseconds', playbackConfig.audioObserverIntervalMilliseconds), playbackConfig.audioObserverIntervalMilliseconds, 100, 10000);
        playbackConfig.audioObserverMinimumIntervalMilliseconds = asFiniteNumber(readConfigValue(options, 'audioObserverMinimumIntervalMilliseconds', 'audio_observer_minimum_interval_milliseconds', playbackConfig.audioObserverMinimumIntervalMilliseconds), playbackConfig.audioObserverMinimumIntervalMilliseconds, 100, 5000);
        playbackConfig.seekToleranceSeconds = asFiniteNumber(readConfigValue(options, 'seekToleranceSeconds', 'seek_tolerance_seconds', playbackConfig.seekToleranceSeconds), playbackConfig.seekToleranceSeconds, 0.05, 10);
        playbackConfig.volumeStep = asFiniteNumber(readConfigValue(options, 'volumeStep', 'volume_step', playbackConfig.volumeStep), playbackConfig.volumeStep, 0.01, 0.5);
        playbackConfig.defaultVolume = asFiniteNumber(readConfigValue(options, 'defaultVolume', 'default_volume', playbackConfig.defaultVolume), playbackConfig.defaultVolume, 0, 1);
    }

    function notifyState(json) {
        if (stateHandler) {
            var reference = json
                ? DotNet.createJSStreamReference(new Blob([json], { type: 'application/json' }))
                : null;
            stateHandler.invokeMethodAsync('HandlePlaybackState', reference);
        }
    }

    function notifyCommand(json) {
        if (commandHandler && json) {
            commandHandler.invokeMethodAsync('HandlePlaybackCommand', json);
        }
    }

    function readAudioElementState(element) {
        synchronizeCaptionSelection(element);
        if (!element) {
            return {
                currentTime: 0,
                duration: 0,
                volume: playbackConfig.defaultVolume,
                muted: false,
                paused: true,
                playbackRate: 1
            };
        }

        var pendingStart = Number.parseFloat(element.dataset.listenPendingStartPosition || '');
        var currentTime = element.currentTime || 0;
        if (Number.isFinite(pendingStart) && pendingStart > 0) {
            var pendingStartedAt = Number.parseFloat(element.dataset.listenPendingStartAt || '');
            var pendingAge = Number.isFinite(pendingStartedAt) ? Date.now() - pendingStartedAt : 0;
            if (currentTime >= pendingStart - playbackConfig.seekToleranceSeconds || pendingAge > 8000) {
                delete element.dataset.listenPendingStartPosition;
                delete element.dataset.listenPendingStartAt;
            } else {
                currentTime = pendingStart;
            }
        }

        return {
            currentTime: currentTime,
            subtitleCount: element._tuvimaHls?.subtitleTracks?.length || element.textTracks?.length || 0,
            captionTracks: readCaptionTrackChoices(element),
            assetId: element.tagName === 'VIDEO' ? element._tuvimaVideoBinding?.asset || '' : audioAssetId(element),
            requestVersion: element.tagName === 'VIDEO' ? element._tuvimaVideoBinding?.request ?? null : Number(element.dataset.playbackRequestVersion),
            audioTrackCount: element._tuvimaHls?.audioTracks?.length || element.audioTracks?.length || 0,
            duration: isFinite(element.duration) ? element.duration : 0,
            volume: typeof element.volume === 'number' ? element.volume : playbackConfig.defaultVolume,
            muted: !!element.muted,
            paused: !!element.paused,
            playbackRate: typeof element.playbackRate === 'number' ? element.playbackRate : 1
        };
    }

    function readCaptionTrackChoices(element) {
        if (!element) return [];
        var choices = [];
        var textTracks = Array.from(element.textTracks || []);
        textTracks.forEach(function (track, index) {
            if (track.kind !== 'captions' && track.kind !== 'subtitles') return;
            var node = Array.from(element.querySelectorAll('track')).find(function (candidate) { return candidate.track === track; });
            var key = node?.dataset?.playbackTrackKey || ('browser:' + index);
            choices.push({
                key: key,
                label: track.label || track.language || ('Caption ' + (index + 1)),
                language: track.language || '',
                selected: track.mode === 'showing'
            });
        });

        var hls = element._tuvimaHls;
        Array.from(hls?.subtitleTracks || []).forEach(function (track, index) {
            var label = track.name || track.label || track.lang || track.language || ('Subtitle ' + (index + 1));
            var language = track.lang || track.language || '';
            var existing = choices.find(function (choice) {
                return choice.label.toLocaleLowerCase() === String(label).toLocaleLowerCase()
                    && !choice.key.startsWith('managed:')
                    && !Number.isInteger(choice.hlsIndex)
                    && (!language || !choice.language || choice.language.toLocaleLowerCase() === String(language).toLocaleLowerCase());
            });
            var selected = hls.subtitleDisplay !== false && hls.subtitleTrack === index;
            if (existing) {
                existing.selected = existing.selected || selected;
                existing.hlsIndex = index;
            } else choices.push({ key: 'hls:' + index, label: String(label), language: String(language), selected: selected, hlsIndex: index });
        });
        return choices;
    }

    function currentCaptionBinding(element, asset, request, profile) {
        const binding = element?._tuvimaVideoBinding;
        return binding && element.dataset.playbackAssetId === binding.asset
            && Number(element.dataset.playbackRequestVersion) === binding.request
            && (!asset || binding.asset === asset) && (request == null || binding.request === Number(request))
            && (!profile || binding.profile === profile) ? binding : null;
    }

    function synchronizeCaptionSelection(element) {
        const binding = currentCaptionBinding(element);
        if (!binding || element._tuvimaSynchronizingCaptions) return;
        const previous = element._tuvimaCaptionSelection;
        const selection = previous?.asset === binding.asset && previous?.profile === binding.profile
            ? previous : { asset: binding.asset, profile: binding.profile, key: null, explicit: false };
        element._tuvimaCaptionSelection = selection;
        const choices = readCaptionTrackChoices(element);
        if (!selection.explicit) {
            const preferred = Array.from(element.querySelectorAll('track')).find(node => node.dataset.playbackPreferred === 'true');
            const nativeDefault = Array.from(element.querySelectorAll('track')).find(node => node.default);
            selection.key = preferred?.dataset.playbackTrackKey || choices.find(choice => choice.selected)?.key
                || nativeDefault?.dataset.playbackTrackKey || null;
        }
        const selected = choices.filter(choice => choice.selected);
        const showingNative = Array.from(element.textTracks || []).filter(track => (track.kind === 'captions' || track.kind === 'subtitles') && track.mode === 'showing');
        if (selected.length === (selection.key ? 1 : 0) && (!selection.key || selected[0].key === selection.key) && showingNative.length <= 1) return;
        element._tuvimaSynchronizingCaptions = true;
        try {
            // A temporarily missing explicit choice stays off until that track returns.
            selectCaptionTrack(element, choices.some(choice => choice.key === selection.key) ? selection.key : null, null, null, null, false);
        } finally { element._tuvimaSynchronizingCaptions = false; }
    }

    function selectCaptionTrack(element, key, expectedAsset, expectedRequest, expectedProfile, remember = true) {
        if (!element) return false;
        if (expectedAsset && !currentCaptionBinding(element, expectedAsset, expectedRequest, expectedProfile)) return false;
        var hls = element._tuvimaHls;
        var choices = readCaptionTrackChoices(element);
        var choice = key ? choices.find(track => track.key === key) : null;
        if (key && !choice) return false;
        if (remember && currentCaptionBinding(element)) {
            const binding = element._tuvimaVideoBinding;
            element._tuvimaCaptionSelection = { asset: binding.asset, profile: binding.profile, key: key || null, explicit: true };
        }
        for (const track of Array.from(element.textTracks || [])) if (track.mode !== 'disabled') track.mode = 'disabled';
        if (hls) {
            hls.subtitleTrack = -1;
            hls.subtitleDisplay = false;
        }
        if (!choice) return true;
        if (Number.isInteger(choice.hlsIndex) && hls) {
            hls.subtitleTrack = choice.hlsIndex;
            hls.subtitleDisplay = true;
            return true;
        }
        var node = Array.from(element.querySelectorAll('track'))
            .find(track => track.dataset.playbackTrackKey === key);
        var nativeTrack = node?.track;
        if (!nativeTrack && key.startsWith('browser:')) {
            nativeTrack = Array.from(element.textTracks || [])[Number.parseInt(key.slice(8), 10)];
        }
        if (!nativeTrack) return false;
        nativeTrack.mode = 'showing';
        return true;
    }

    function audioObserverFor(element) {
        return audioObservers ? audioObservers.get(element) : element && element.__listenAudioObserver;
    }

    function setAudioObserver(element, observer) {
        if (!element) return;
        if (audioObservers) {
            if (observer) {
                audioObservers.set(element, observer);
            } else {
                audioObservers.delete(element);
            }
            return;
        }

        if (observer) {
            element.__listenAudioObserver = observer;
        } else {
            delete element.__listenAudioObserver;
        }
    }

    function removeAudioObserver(element) {
        var observer = audioObserverFor(element);
        if (!element || !observer) return;

        element.removeEventListener('timeupdate', observer.onTimeUpdate);
        element.removeEventListener('durationchange', observer.onMetadataChanged);
        element.removeEventListener('loadedmetadata', observer.onMetadataChanged);
        element.removeEventListener('ratechange', observer.onMetadataChanged);
        element.removeEventListener('volumechange', observer.onMetadataChanged);
        element.removeEventListener('timeupdate', observer.onSleepTimerProgress);
        element.removeEventListener('durationchange', observer.onSleepTimerProgress);
        element.removeEventListener('loadedmetadata', observer.onSleepTimerProgress);
        element.removeEventListener('ratechange', observer.onSleepTimerProgress);
        element.removeEventListener('seeking', observer.onSleepTimerProgress);
        element.removeEventListener('seeked', observer.onSleepTimerProgress);
        element.removeEventListener('playing', observer.onSleepTimerProgress);
        element.removeEventListener('waiting', observer.onSleepTimerProgress);
        element.removeEventListener('ended', observer.onNativeAudioEnded, true);
        element.removeEventListener('play', observer.onNativePlaybackRestart);
        clearNativeSleepTimer(observer);
        element.textTracks?.removeEventListener('addtrack', observer.onCaptionTracksChanged);
        element.textTracks?.removeEventListener('removetrack', observer.onCaptionTracksChanged);
        element.textTracks?.removeEventListener('change', observer.onCaptionTracksChanged);
        observer.captionTracks.forEach(track => track.removeEventListener('cuechange', observer.onCaptionCueChanged));
        window.removeEventListener('resize', observer.onCaptionLayoutChanged);
        window.visualViewport?.removeEventListener('resize', observer.onCaptionLayoutChanged);
        element.removeEventListener('enterpictureinpicture', observer.onCaptionLayoutChanged);
        element.removeEventListener('leavepictureinpicture', observer.onCaptionLayoutChanged);
        observer.hostObserver?.disconnect();
        observer.captionNodesObserver?.disconnect();
        setAudioObserver(element, null);
    }

    function shortcutHandlerFor(element) {
        return playerShortcutHandlers ? playerShortcutHandlers.get(element) : element && element.__listenPlaybackShortcutHandler;
    }

    function setShortcutHandler(element, handler) {
        if (!element) return;
        if (playerShortcutHandlers) {
            if (handler) {
                playerShortcutHandlers.set(element, handler);
            } else {
                playerShortcutHandlers.delete(element);
            }
            return;
        }

        if (handler) {
            element.__listenPlaybackShortcutHandler = handler;
        } else {
            delete element.__listenPlaybackShortcutHandler;
        }
    }

    function isEditableShortcutTarget(target) {
        if (!target) return false;
        var tagName = (target.tagName || '').toLowerCase();
        if (tagName === 'input' || tagName === 'textarea' || tagName === 'select') return true;
        if (target.isContentEditable) return true;
        if (target.closest && target.closest('[role="combobox"], [role="listbox"], [role="option"]')) return true;
        return !!(target.closest && target.closest('[contenteditable="true"]'));
    }

    function clearNativeSleepTimer(observer) {
        if (!observer) return;
        if (observer.sleepTimerTimeout) window.clearTimeout(observer.sleepTimerTimeout);
        observer.sleepTimerTimeout = null;
        if (observer.sleepTimerBindingCancel) observer.sleepTimerBindingCancel();
        observer.sleepTimerBindingCancel = null;
        observer.pendingSleepTimer = null;
        observer.sleepTimer = null;
    }

    function cancelPendingSleepTimerBinding(observer) {
        if (!observer) return;
        if (observer.sleepTimerBindingCancel) observer.sleepTimerBindingCancel();
        observer.sleepTimerBindingCancel = null;
        observer.pendingSleepTimer = null;
    }

    function samePlaybackUrl(actual, expected) {
        if (!actual || !expected) return false;
        try { return new URL(actual, document.baseURI).href === new URL(expected, document.baseURI).href; }
        catch (_) { return actual === expected; }
    }

    function audioAssetId(element) {
        return element && (element.dataset.playbackAssetId || element.dataset.currentAssetId || '');
    }

    function audioRequestVersionMatches(element, timer) {
        if (!element || !timer) return false;
        var actual = Number(element.dataset.playbackRequestVersion);
        return Number.isSafeInteger(actual) && actual === Number(timer.playbackRequestVersion);
    }

    function audioSourceMatches(element, timer) {
        if (!element || !timer || !timer.sourceUrl || !audioRequestVersionMatches(element, timer)
            || !samePlaybackUrl(element.dataset.playbackSource, timer.sourceUrl)) return false;
        var hls = element._tuvimaHls;
        if (hls) {
            if (!samePlaybackUrl(hls.url, timer.sourceUrl) || hls.media !== element) return false;
            if (timer.hlsInstance && timer.hlsInstance !== hls) return false;
            if (!timer.hlsInstance) timer.hlsInstance = hls;
            return true;
        }
        if (timer.hlsInstance) return false;
        var current = element.currentSrc || element.src || element.getAttribute('src');
        return !!current && samePlaybackUrl(current, timer.sourceUrl);
    }

    function readAudioPositionForSleepTimer(element, expectedAssetId, expectedRequestVersion, expectedSourceUrl) {
        var observer = audioObserverFor(element);
        var active = observer && observer.sleepTimer;
        var probe = {
            boundAssetId: expectedAssetId,
            playbackRequestVersion: expectedRequestVersion,
            sourceUrl: expectedSourceUrl,
            hlsInstance: active
                && active.boundAssetId === expectedAssetId
                && active.playbackRequestVersion === expectedRequestVersion
                && samePlaybackUrl(active.sourceUrl, expectedSourceUrl)
                ? active.hlsInstance
                : null
        };
        if (audioAssetId(element) !== expectedAssetId || !audioSourceMatches(element, probe)) return null;
        var position = Number(element.currentTime);
        return Number.isFinite(position) && position >= 0 ? position : null;
    }

    function notifyNativeSleepTimerExpired(observer, timer) {
        if (!observer || !timer || timer.fired || observer.sleepTimer !== timer) return;
        var assetMatches = audioAssetId(observer.element) === timer.boundAssetId;
        var sourceMatches = audioSourceMatches(observer.element, timer);
        if (!assetMatches || !sourceMatches) {
            return;
        }
        timer.fired = true;
        if (observer.sleepTimerTimeout) window.clearTimeout(observer.sleepTimerTimeout);
        observer.sleepTimerTimeout = null;
        try { observer.element.pause(); } catch (_) { }
        try {
            var invocation = observer.dotNetRef.invokeMethodAsync('HandleNativeSleepTimerExpired',
                timer.timerGeneration, timer.boundAssetId, timer.playbackRequestVersion,
                Number(observer.element.currentTime) || 0, false);
            if (invocation && typeof invocation.catch === 'function') invocation.catch(function (error) {
                console.debug('Could not report audiobook sleep timer expiry.', error);
            });
        } catch (error) { console.debug('Could not report audiobook sleep timer expiry.', error); }
    }

    function scheduleNativeSleepTimer(observer) {
        if (!observer) return;
        if (observer.sleepTimerTimeout) window.clearTimeout(observer.sleepTimerTimeout);
        observer.sleepTimerTimeout = null;
        var timer = observer.sleepTimer;
        if (!timer || timer.fired) return;
        var element = observer.element;
        if (audioAssetId(element) !== timer.boundAssetId || !audioSourceMatches(element, timer)) return;

        if (timer.mode === 'timer') {
            if (!Number.isFinite(timer.monotonicDeadline)) return;
            var remaining = timer.monotonicDeadline - performance.now();
            observer.sleepTimerTimeout = window.setTimeout(function () {
                // UTC is converted once to a monotonic deadline for this live owner.
                if (performance.now() < timer.monotonicDeadline) scheduleNativeSleepTimer(observer);
                else notifyNativeSleepTimerExpired(observer, timer);
            }, Math.max(0, remaining));
            return;
        }

        if (timer.mode !== 'end-current' && timer.mode !== 'end-next') return;
        if (audioAssetId(element) !== timer.targetAssetId) return;
        var currentTime = Number(element.currentTime);
        var targetEnd = Number(timer.targetEndSeconds);
        if (!Number.isFinite(currentTime) || !Number.isFinite(targetEnd)) return;
        if (currentTime >= targetEnd) {
            notifyNativeSleepTimerExpired(observer, timer);
            return;
        }
        if (element.paused || element.waiting || !(Number(element.playbackRate) > 0)) return;
        var delay = Math.max(0, (targetEnd - currentTime) / Number(element.playbackRate) * 1000);
        observer.sleepTimerTimeout = window.setTimeout(function () {
            // Timers can wake early, and seeks/rate changes can move the boundary.
            if (Number(element.currentTime) < targetEnd) scheduleNativeSleepTimer(observer);
            else notifyNativeSleepTimerExpired(observer, timer);
        }, delay);
    }

    function waitForNativeSleepTimerSource(observer, timer) {
        return new Promise(function (resolve) {
            var done = false;
            var timeout;
            var mutationObserver;
            function finish(success) {
                if (done) return;
                done = true;
                window.clearTimeout(timeout);
                observer.element.removeEventListener('loadedmetadata', check);
                observer.element.removeEventListener('canplay', check);
                observer.element.removeEventListener('error', failed);
                mutationObserver?.disconnect();
                if (observer.sleepTimerBindingCancel === cancel) observer.sleepTimerBindingCancel = null;
                if (!success && observer.pendingSleepTimer === timer) observer.pendingSleepTimer = null;
                resolve(success);
            }
            function check() {
                if (observer.pendingSleepTimer !== timer) return finish(false);
                if (audioAssetId(observer.element) === timer.boundAssetId && audioSourceMatches(observer.element, timer)) {
                    observer.sleepTimer = timer;
                    observer.pendingSleepTimer = null;
                    observer.sleepTimerGeneration = timer.timerGeneration;
                    scheduleNativeSleepTimer(observer);
                    finish(true);
                }
            }
            function failed() { finish(false); }
            function cancel() { finish(false); }
            observer.pendingSleepTimer = timer;
            observer.sleepTimerBindingCancel = cancel;
            observer.element.addEventListener('loadedmetadata', check);
            observer.element.addEventListener('canplay', check);
            observer.element.addEventListener('error', failed);
            if (typeof MutationObserver !== 'undefined') {
                mutationObserver = new MutationObserver(check);
                mutationObserver.observe(observer.element, { attributes: true, attributeFilter: ['data-current-asset-id', 'data-playback-asset-id', 'data-playback-request-version', 'src'] });
            }
            timeout = window.setTimeout(function () { finish(false); }, 12000);
            check();
        });
    }

    function unregisterPlayerShortcuts(element) {
        var handler = shortcutHandlerFor(element);
        if (!element || !handler) return;
        element.removeEventListener('keydown', handler);
        setShortcutHandler(element, null);
    }

    function registerPlayerShortcuts(element, dotNetRef) {
        if (!element || !dotNetRef) return;
        unregisterPlayerShortcuts(element);

        var handler = function (event) {
            if (!event || event.defaultPrevented || event.altKey || event.ctrlKey || event.metaKey || isEditableShortcutTarget(event.target)) return;

            var action = null;
            if (event.code === 'Space' || event.key === ' ') action = 'toggle-play';
            if (event.key === 'ArrowLeft') action = 'skip-back';
            if (event.key === 'ArrowRight') action = 'skip-forward';
            if (event.key === 'ArrowUp') action = 'volume-up';
            if (event.key === 'ArrowDown') action = 'volume-down';
            if (event.key?.toLowerCase() === 'm') action = 'toggle-mute';
            if (!action) return;

            event.preventDefault();
            dotNetRef.invokeMethodAsync('HandlePlayerShortcut', action)
                .catch(function (error) {
                    console.debug('Could not dispatch playback shortcut.', error);
                });
        };

        element.addEventListener('keydown', handler);
        setShortcutHandler(element, handler);
    }

    function audioEngineElement() {
        var element = document.getElementById('listen-audio-engine');
        if (element) return element;

        try {
            if (window.opener && !window.opener.closed && window.opener.document) {
                return window.opener.document.getElementById('listen-audio-engine');
            }
        } catch (error) {
            console.debug("Could not access opener audio engine.", error);
        }

        return null;
    }

    async function startAudioElement(element, options) {
        if (!element) return false;

        var payload = options || {};
        var streamUrl = payload.streamUrl || payload.StreamUrl || '';
        var positionSeconds = payload.positionSeconds ?? payload.PositionSeconds ?? 0;
        var playbackRate = payload.playbackRate ?? payload.PlaybackRate ?? 1;
        var volume = payload.volume ?? payload.Volume;
        var muted = payload.muted ?? payload.Muted;

        try {
            if (streamUrl && element.dataset.playbackSource !== streamUrl) {
                ensureAudioSource(element, streamUrl);
            }

            if (typeof volume === 'number') {
                element.volume = Math.max(0, Math.min(1, volume));
            }

            if (typeof muted === 'boolean') {
                element.muted = muted;
            }

            if (typeof playbackRate === 'number' && isFinite(playbackRate) && playbackRate >= 0.5 && playbackRate <= 3) {
                element.playbackRate = playbackRate;
            }

            var target = Math.max(0, positionSeconds || 0);
            if (target > 0) {
                element.dataset.listenPendingStartPosition = String(target);
                element.dataset.listenPendingStartAt = String(Date.now());
            } else {
                delete element.dataset.listenPendingStartPosition;
                delete element.dataset.listenPendingStartAt;
            }

            var applyTargetSeek = function () {
                if (target <= 0) return;
                try {
                    element.currentTime = target;
                } catch (seekError) {
                    console.debug("Audio start seek was rejected.", seekError);
                }
            };

            if (target > 0 && element.readyState >= 1) {
                applyTargetSeek();
            } else if (target > 0) {
                element.addEventListener('loadedmetadata', applyTargetSeek, { once: true });
            }

            delete element.dataset.playbackStartFailure;
            await element.play();
            scheduleNativeSleepTimer(audioObserverFor(element));
            if (target > 0 && element.readyState >= 1 && Math.abs((element.currentTime || 0) - target) > playbackConfig.seekToleranceSeconds) {
                applyTargetSeek();
            }
            if (target <= 0 || (element.currentTime || 0) >= target - playbackConfig.seekToleranceSeconds) {
                delete element.dataset.listenPendingStartPosition;
                delete element.dataset.listenPendingStartAt;
            }
            return true;
        } catch (error) {
            element.dataset.playbackStartFailure = error.name || "PlaybackError";
            console.debug("Audio start request was rejected.", error);
            return false;
        }
    }

    function clearPreparedCaptions(element) {
        element._tuvimaCaptionAbort?.abort();
        element._tuvimaCaptionAbort = null;
        clearTimeout(element._tuvimaCaptionTimer);
        element.querySelectorAll('track[data-prepared-caption]').forEach(track => track.remove());
    }

    function discoverPreparedCaptions(element, streamUrl) {
        const master = new URL(streamUrl, window.location.href);
        if (master.origin !== window.location.origin) return;
        const controller = new AbortController();
        element._tuvimaCaptionAbort = controller;
        let attempts = 0;
        const poll = async () => {
            if (controller.signal.aborted || element.dataset.playbackSource !== streamUrl) return;
            try {
                const response = await fetch(new URL('captions.json', master), { credentials: 'same-origin', signal: controller.signal });
                if (response.ok) {
                    const captions = await response.json();
                    if (controller.signal.aborted || element.dataset.playbackSource !== streamUrl) return;
                    if (!element._tuvimaHls?.subtitleTracks?.length && Array.isArray(captions)) {
                        for (const caption of captions) {
                            if (!/^s\d+\/caption\.vtt$/.test(caption.path)) continue;
                            const track = document.createElement('track');
                            track.kind = 'subtitles';
                            track.label = caption.language || 'und';
                            track.srclang = caption.language || 'und';
                            track.src = new URL(caption.path, master).href;
                            track.dataset.preparedCaption = 'true';
                            element.appendChild(track);
                        }
                    }
                    return;
                }
                if (response.status === 401 || response.status === 403) return;
            } catch (error) {
                if (controller.signal.aborted) return;
            }
            if (++attempts < 150) element._tuvimaCaptionTimer = setTimeout(poll, 2000);
        };
        element._tuvimaCaptionTimer = setTimeout(poll, 2000);
    }

    function ensureAudioSource(element, streamUrl) {
        if (!element || !streamUrl) return;

        try {
            if (element.dataset.playbackSource === streamUrl) return;
            clearPreparedCaptions(element);
            if (element._tuvimaHls) {
                element._tuvimaHls.destroy();
                element._tuvimaHls = null;
            }
            element.dataset.playbackSource = streamUrl;
            if (/\.m3u8(?:[?#]|$)/i.test(streamUrl) && window.Hls?.isSupported()) {
                const hls = new Hls({ startPosition: 0, enableWorker: true });
                element._tuvimaHls = hls;
                hls.on(Hls.Events.ERROR, (_, data) => {
                    if (data.fatal) {
                        element.dataset.playbackError = data.details || 'Video stream failed';
                        element.dispatchEvent(new Event('error'));
                    }
                });
                hls.loadSource(streamUrl);
                hls.attachMedia(element);
                discoverPreparedCaptions(element, streamUrl);
                return;
            }
            if (element.getAttribute('src') !== streamUrl) {
                element.setAttribute('src', streamUrl);
                element.load();
                if (/\.m3u8(?:[?#]|$)/i.test(streamUrl)) discoverPreparedCaptions(element, streamUrl);
            }
        } catch (error) {
            console.debug("Audio source sync was rejected.", error);
        }
    }

    function numberFromDataset(value, fallback) {
        var parsed = Number.parseFloat(value);
        return Number.isFinite(parsed) ? parsed : fallback;
    }

    function boolFromDataset(value, fallback) {
        if (value === 'true') return true;
        if (value === 'false') return false;
        return fallback;
    }

    function startPositionFromDataset(action, fallback) {
        var position = numberFromDataset(action.dataset.listenStartPosition, fallback || 0);
        if (position > 0) return position;

        if ((action.dataset.listenStartKind || '').toLowerCase() !== 'resume') {
            return Math.max(0, position || 0);
        }

        var progress = numberFromDataset(action.dataset.listenStartProgress, 0);
        var duration = numberFromDataset(action.dataset.listenStartDuration, 0);
        var rewind = numberFromDataset(action.dataset.listenStartRewind, 0);
        if (progress > 0 && progress < 99.5 && duration > 0) {
            return Math.max(0, duration * Math.min(100, progress) / 100 - Math.max(0, rewind));
        }

        return Math.max(0, position || 0);
    }

    function openPopupWindow(url) {
        if (!url) return false;
        if (popupWindow && !popupWindow.closed) {
            if (typeof popupWindow.focus === 'function') {
                popupWindow.focus();
            }
            return true;
        }

        var availableWidth = Math.max(280, Number(window.screen && window.screen.availWidth) || window.innerWidth || playbackConfig.popupWidth);
        var availableHeight = Math.max(360, Number(window.screen && window.screen.availHeight) || window.innerHeight || playbackConfig.popupHeight);
        var width = Math.min(Math.round(playbackConfig.popupWidth), availableWidth);
        var height = Math.min(Math.round(playbackConfig.popupHeight), availableHeight);
        var leftBase = Number(window.screen && window.screen.availLeft) || 0;
        var topBase = Number(window.screen && window.screen.availTop) || 0;
        var left = Math.round(leftBase + (availableWidth - width) / 2);
        var top = Math.round(topBase + (availableHeight - height) / 2);
        var ownerId = window.tuvimaBookmarkCommands && window.tuvimaBookmarkCommands.getOwnerId();
        popupWindowId = window.crypto && window.crypto.randomUUID ? window.crypto.randomUUID() :
            'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function (c) { var r = Math.random() * 16 | 0; return (c === 'x' ? r : (r & 3 | 8)).toString(16); });
        if (ownerId) {
            try {
                var popupUrl = new URL(url, window.location.href);
                popupUrl.searchParams.set('owner', ownerId);
                popupUrl.searchParams.set('window', popupWindowId);
                url = popupUrl.pathname + popupUrl.search + popupUrl.hash;
            } catch (_) { }
        }

        popupWindow = window.open(
            url,
            popupName,
            'popup=yes,width=' + width + ',height=' + height + ',left=' + left + ',top=' + top + ',resizable=yes,scrollbars=no'
        );

        if (popupWindow && typeof popupWindow.focus === 'function') {
            popupWindow.focus();
        }

        return !!popupWindow;
    }

    function focusPopup() {
        window.requestAnimationFrame(function () {
            var target = document.querySelector('.listen-popup button:not([disabled]), .listen-popup input:not([disabled]), main button:not([disabled]), main input:not([disabled])');
            if (target && typeof target.focus === 'function') {
                target.focus({ preventScroll: true });
            }
        });
    }

    function captureAudiobookBookmarkPosition(expectedAssetId, expectedRequestVersion, expectedSourceUrl) {
        var audio = document.getElementById('listen-audio-engine');
        if (!audio || !expectedAssetId || !expectedSourceUrl) return null;
        var currentAssetId = audio.dataset && audio.dataset.currentAssetId;
        if (!currentAssetId || currentAssetId.toLowerCase() !== String(expectedAssetId).toLowerCase()) return null;
        var probe = {
            boundAssetId: expectedAssetId,
            playbackRequestVersion: expectedRequestVersion,
            sourceUrl: expectedSourceUrl,
            hlsInstance: audio._tuvimaHls || null
        };
        if (!audioRequestVersionMatches(audio, probe) || !audioSourceMatches(audio, probe)) return null;
        var position = Number(audio.currentTime);
        if (!Number.isFinite(position) || position < 0) return null;
        var duration = Number(audio.duration);
        return {
            assetId: currentAssetId,
            playbackRequestVersion: Number(expectedRequestVersion),
            sourceVerified: true,
            positionSeconds: position,
            durationSeconds: Number.isFinite(duration) && duration > 0 ? duration : null
        };
    }

    function closeAudioSourceMatches(audio, sourceUrl, requestVersion) {
        return !!sourceUrl && audioSourceMatches(audio, {
            sourceUrl: sourceUrl, playbackRequestVersion: requestVersion, hlsInstance: audio._tuvimaHls || null
        });
    }

    function readCurrentAudiobookPosition(expectedAssetId, expectedRequestVersion, expectedSourceUrl) {
        var audio = document.getElementById('listen-audio-engine');
        return readAudioPositionForSleepTimer(audio, expectedAssetId, expectedRequestVersion, expectedSourceUrl);
    }

    function returnToVideo() {
        try {
            if (!window.opener || window.opener.closed) return false;
            window.opener.focus();
            var videoHost = window.opener.document.querySelector('.video-playback-host.is-expanded');
            if (!videoHost) return false;
            videoHost.focus({ preventScroll: true });
            return true;
        } catch (error) {
            console.debug('Could not return focus to the main video player.', error);
            return false;
        }
    }

    var lastImmediateStartAction = null;
    var lastImmediateStartAt = 0;
    var lastImmediatePopupAction = null;
    var lastImmediatePopupAt = 0;

    function startAudioFromImmediateAction(target, allowDuplicate) {
        var action = target && target.closest ? target.closest('[data-listen-immediate-start]') : null;
        if (!action || action.disabled || action.getAttribute('aria-disabled') === 'true') return;
        if (action.dataset.listenImmediateStart === 'music') {
            const interactive = target.closest('a,button,input,[role="menuitem"]');
            if (interactive && !interactive.hasAttribute('data-listen-row-play')) return;
            if (!action.dataset.listenStartUrl) return;
        }

        var audio = audioEngineElement();
        if (!audio) return;

        var now = Date.now();
        if (!allowDuplicate && action === lastImmediateStartAction && now - lastImmediateStartAt < playbackConfig.immediateActionDedupMilliseconds) {
            return;
        }

        lastImmediateStartAction = action;
        lastImmediateStartAt = now;

        startAudioElement(audio, {
            streamUrl: action.dataset.listenStartUrl || '',
            positionSeconds: startPositionFromDataset(action, 0),
            playbackRate: numberFromDataset(action.dataset.listenStartRate, 1),
            volume: numberFromDataset(action.dataset.listenStartVolume, undefined),
            muted: boolFromDataset(action.dataset.listenStartMuted, undefined)
        });
    }

    var lastImmediateToggleAction = null;
    var lastImmediateToggleAt = 0;
    var immediateToggleConsumedAt = 0;

    function toggleAudioFromImmediateAction(target, allowDuplicate) {
        var action = target && target.closest ? target.closest('[data-listen-audio-toggle="true"]') : null;
        if (!action || action.disabled || action.getAttribute('aria-disabled') === 'true') return;

        var audio = audioEngineElement();
        if (!audio) return;

        var now = Date.now();
        if (!allowDuplicate && action === lastImmediateToggleAction && now - lastImmediateToggleAt < playbackConfig.immediateActionDedupMilliseconds) {
            return;
        }

        lastImmediateToggleAction = action;
        lastImmediateToggleAt = now;

        if (!audio.paused) {
            audio.pause();
            immediateToggleConsumedAt = now;
            return;
        }

        var start = startAudioElement(audio, {
            streamUrl: action.dataset.listenStartUrl || audio.getAttribute('src') || '',
            positionSeconds: startPositionFromDataset(action, audio.currentTime || 0),
            playbackRate: numberFromDataset(action.dataset.listenStartRate, audio.playbackRate || 1),
            volume: numberFromDataset(action.dataset.listenStartVolume, undefined),
            muted: boolFromDataset(action.dataset.listenStartMuted, undefined)
        });
        if (start && typeof start.then === 'function') {
            start.then(function (started) {
                if (started) {
                    immediateToggleConsumedAt = Date.now();
                }
            });
        }
    }

    var lastImmediateSeekAction = null;
    var lastImmediateSeekAt = 0;
    var immediateSeekConsumedAt = 0;

    function seekAudioFromImmediateAction(target, allowDuplicate) {
        var action = target && target.closest ? target.closest('[data-playback-seek-delta]') : null;
        if (!action || action.disabled || action.getAttribute('aria-disabled') === 'true') return;

        var audio = audioEngineElement();
        if (!audio) return;

        var now = Date.now();
        if (!allowDuplicate && action === lastImmediateSeekAction && now - lastImmediateSeekAt < playbackConfig.immediateActionDedupMilliseconds) {
            return;
        }

        var delta = numberFromDataset(action.dataset.playbackSeekDelta, 0);
        if (!delta) return;

        lastImmediateSeekAction = action;
        lastImmediateSeekAt = now;

        var duration = Number.isFinite(audio.duration) && audio.duration > 0 ? audio.duration : Number.MAX_SAFE_INTEGER;
        var current = Number.isFinite(audio.currentTime) ? audio.currentTime : 0;
        var next = Math.max(0, Math.min(duration, current + delta));
        try {
            audio.currentTime = next;
            immediateSeekConsumedAt = now;
        } catch (error) {
            console.debug("Immediate audio seek was rejected.", error);
        }
    }

    function openPopupFromImmediateAction(target, allowDuplicate) {
        var action = target && target.closest ? target.closest('[data-listen-popup-route]') : null;
        if (!action || action.disabled || action.getAttribute('aria-disabled') === 'true') return;

        var route = action.dataset.listenPopupRoute || '';
        if (!route) return;

        var now = Date.now();
        if (!allowDuplicate && action === lastImmediatePopupAction && now - lastImmediatePopupAt < playbackConfig.immediateActionDedupMilliseconds) {
            return;
        }

        lastImmediatePopupAction = action;
        lastImmediatePopupAt = now;
        openPopupWindow(route);
    }

    document.addEventListener('click', function (event) {
        startAudioFromImmediateAction(event.target, true);
        toggleAudioFromImmediateAction(event.target, false);
        seekAudioFromImmediateAction(event.target, false);
        openPopupFromImmediateAction(event.target, false);
    }, true);

    if (channel) {
        channel.onmessage = function (event) {
            if (!event || !event.data) return;

            if (event.data.type === 'state') {
                notifyState(event.data.json);
            }

            if (event.data.type === 'command') {
                notifyCommand(event.data.json);
            }
        };
    }

    window.addEventListener('storage', function (event) {
        if (!event) return;

        if (event.key === stateKey) {
            notifyState(event.newValue || '');
        }

        if (event.key === commandKey && event.newValue) {
            notifyCommand(event.newValue);
        }
    });

    return {
        configure: configure,
        hasState: function () {
            return Boolean(window.tuvimaPopupStateSync.getLatestState(stateKey));
        },
        getStateStream: function () {
            var json = window.tuvimaPopupStateSync.getLatestState(stateKey);
            if (!json) {
                throw new Error('Playback state is no longer available.');
            }
            return new Blob([json], { type: 'application/json' });
        },
        getStoredState: function () {
            return localStorage.getItem(stateKey);
        },
        setState: function (json) {
            localStorage.setItem(stateKey, json);
            if (channel) {
                channel.postMessage({ type: 'state', json: json });
            }
        },
        clearState: function () {
            localStorage.removeItem(stateKey);
            if (channel) {
                channel.postMessage({ type: 'state', json: '' });
            }
        },
        sendCommand: function (json) {
            if (!json) return;
            localStorage.setItem(commandKey, json);
            if (channel) {
                channel.postMessage({ type: 'command', json: json });
            }
        },
        registerStateHandler: function (dotNetRef) {
            stateHandler = dotNetRef;
            window.tuvimaPopupStateSync.requestLatestState(function (json) {
                window.listenPlayback.sendCommand(json);
            });
        },
        unregisterStateHandler: function (dotNetRef) {
            if (!dotNetRef || stateHandler === dotNetRef) {
                stateHandler = null;
            }
        },
        registerCommandHandler: function (dotNetRef) {
            commandHandler = dotNetRef;
        },
        unregisterCommandHandler: function (dotNetRef) {
            if (!dotNetRef || commandHandler === dotNetRef) {
                commandHandler = null;
            }
        },
        openPopup: openPopupWindow,
        pauseAudioForClose: function (assetId, requestVersion, sourceUrl, predecessorSourceUrl, pendingSource) {
            var audio = document.getElementById('listen-audio-engine');
            if (!audio || String(audio.dataset.currentAssetId || '').toLowerCase() !== String(assetId || '').toLowerCase()
                || Number(audio.dataset.playbackRequestVersion) !== Number(requestVersion)) return null;
            var sourceMatches = closeAudioSourceMatches(audio, sourceUrl, requestVersion);
            var predecessorMatches = pendingSource && closeAudioSourceMatches(audio, predecessorSourceUrl, requestVersion);
            if (!sourceMatches && !predecessorMatches) return null;
            audio.pause();
            // The predecessor's position must never become the unresolved current work's resume point.
            return sourceMatches ? captureAudiobookBookmarkPosition(assetId, requestVersion, sourceUrl) : null;
        },
        finalizeAudioClose: function (assetId, requestVersion, sourceUrl, predecessorSourceUrl, pendingSource, profileId) {
            var audio = document.getElementById('listen-audio-engine');
            if (!audio || String(audio.dataset.currentAssetId || '').toLowerCase() !== String(assetId || '').toLowerCase()
                || Number(audio.dataset.playbackRequestVersion) !== Number(requestVersion)) return false;
            var stored;
            try { stored = JSON.parse(localStorage.getItem(stateKey) || 'null'); } catch (_) { stored = null; }
            if (stored) {
                var current = stored.queue && stored.queue[stored.current_index];
                var currentStored = current && String(current.asset_id || '').toLowerCase() === String(assetId || '').toLowerCase()
                    && Number(stored.playback_request_version) === Number(requestVersion);
                var stalePredecessor = pendingSource && Number.isSafeInteger(stored.playback_request_version)
                    && stored.playback_request_version < Number(requestVersion);
                if ((!currentStored && !stalePredecessor)
                    || String(stored.profile_id || '').toLowerCase() !== String(profileId || '').toLowerCase()) return false;
            }
            var sourceMatches = closeAudioSourceMatches(audio, sourceUrl, requestVersion);
            var predecessorMatches = pendingSource && closeAudioSourceMatches(audio, predecessorSourceUrl, requestVersion);
            if (audio.currentSrc || audio.getAttribute('src') || audio._tuvimaHls) {
                if (!sourceMatches && !predecessorMatches) {
                    // Cancel the logical pending request without releasing an unverified physical source.
                    if (!pendingSource) return false;
                } else {
                    audio.pause();
                    clearPreparedCaptions(audio);
                    if (audio._tuvimaHls) { audio._tuvimaHls.destroy(); audio._tuvimaHls = null; }
                    delete audio.dataset.playbackSource;
                    audio.removeAttribute('src');
                    audio.load();
                }
            }
            window.listenPlayback.clearState();
            window.listenPlayback.closePopup();
            return true;
        },
        captureAudiobookBookmarkPosition: captureAudiobookBookmarkPosition,
        readCurrentAudiobookPosition: readCurrentAudiobookPosition,
        closePopup: function () {
            if (popupWindow && !popupWindow.closed) {
                popupWindow.close();
            }

            popupWindow = null;
            popupWindowId = null;
        },
        isCurrentPopupWindow: function (id) {
            return !!id && !!popupWindow && !popupWindow.closed && String(id).toLowerCase() === String(popupWindowId).toLowerCase();
        },
        registerPopupWindow: function (ownerId, senderId, windowId) {
            if (popupUnloadHandler) {
                window.removeEventListener('beforeunload', popupUnloadHandler);
            }

            if (!ownerId || !senderId || !windowId || typeof BroadcastChannel === 'undefined') return;
            var registrationGeneration = Date.now();
            function notifyOwner(action) {
                try {
                    var addressed = new BroadcastChannel('tuvima-bookmark-commands:' + String(ownerId).replace(/-/g, '').toLowerCase());
                    var id = window.crypto && window.crypto.randomUUID ? window.crypto.randomUUID() :
                        'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function (c) { var r = Math.random() * 16 | 0; return (c === 'x' ? r : (r & 3 | 8)).toString(16); });
                    addressed.postMessage({ type: 'command', command: { action: action, commandId: id,
                        recipientId: ownerId, senderId: senderId, popupWindowId: windowId, ownerGeneration: registrationGeneration } });
                    addressed.close();
                } catch (error) {
                    console.debug('Could not notify the current popup owner.', error);
                }
            }
            notifyOwner('register-popup');
            popupUnloadHandler = function () { notifyOwner('popup-closed'); };

            window.addEventListener('beforeunload', popupUnloadHandler);
        },
        focusPopup: focusPopup,
        focusMainWindow: function () { try { if (window.opener && !window.opener.closed) window.opener.focus(); } catch (_) { } },
        returnToVideo: returnToVideo,
        unregisterPopupWindow: function () {
            if (!popupUnloadHandler) return;
            window.removeEventListener('beforeunload', popupUnloadHandler);
            popupUnloadHandler = null;
        },
        readAudioState: function (element) {
            return readAudioElementState(element);
        },
        readAudioPositionForSleepTimer: function (element, expectedAssetId, expectedRequestVersion, expectedSourceUrl) {
            return readAudioPositionForSleepTimer(element, expectedAssetId, expectedRequestVersion, expectedSourceUrl);
        },
        registerAudioStateObserver: function (element, dotNetRef, intervalMs) {
            if (!element || !dotNetRef) return;
            removeAudioObserver(element);

            var interval = Math.max(
                playbackConfig.audioObserverMinimumIntervalMilliseconds,
                intervalMs || playbackConfig.audioObserverIntervalMilliseconds);
            var lastNotifiedAt = 0;
            var notify = function (force) {
                var now = Date.now();
                if (!force && now - lastNotifiedAt < interval) {
                    return;
                }

                lastNotifiedAt = now;
                try {
                    var invocation = dotNetRef.invokeMethodAsync('HandleObservedAudioState', readAudioElementState(element));
                    if (invocation && typeof invocation.catch === 'function') {
                        invocation.catch(function (error) {
                            console.debug('Could not report listen audio state.', error);
                        });
                    }
                } catch (error) {
                    console.debug('Could not report listen audio state.', error);
                }
            };
            var observer = {
                element: element,
                dotNetRef: dotNetRef,
                sleepTimer: null,
                sleepTimerTimeout: null,
                sleepTimerGeneration: 0,
                pendingSleepTimer: null,
                lastNativeEndedBinding: null,
                onTimeUpdate: function () { notify(false); },
                onMetadataChanged: function () { notify(true); },
                onSleepTimerProgress: function () { scheduleNativeSleepTimer(observer); },
                onNativePlaybackRestart: function () {
                    observer.lastNativeEndedBinding = null;
                    scheduleNativeSleepTimer(observer);
                },
                onNativeAudioEnded: function (event) {
                    if (element.tagName === 'VIDEO') return; // Video binds its loaded source in video-presentation.js.
                    var currentTime = Number(element.currentTime);
                    var duration = Number(element.duration);
                    var assetId = audioAssetId(element);
                    var requestVersion = Number(element.dataset.playbackRequestVersion);
                    var timer = observer.sleepTimer;
                    var sourceUrl = element.dataset.playbackSource;
                    var probe = timer || {
                        sourceUrl: sourceUrl,
                        playbackRequestVersion: requestVersion,
                        hlsInstance: element._tuvimaHls || null
                    };
                    var currentSource = element.currentSrc || element.src || element.getAttribute('src');
                    if (element.ended !== true || !assetId || !Number.isSafeInteger(requestVersion)
                        || !Number.isFinite(currentTime) || currentTime < 0
                        || !Number.isFinite(duration) || duration <= 0 || !currentSource
                        || !audioRequestVersionMatches(element, probe)
                        || !audioSourceMatches(element, probe)) return;

                    var timerGeneration = timer ? Number(timer.timerGeneration) : observer.sleepTimerGeneration;
                    var bindingKey = `${assetId}|${requestVersion}|${timerGeneration}|${currentSource}`;
                    if (observer.lastNativeEndedBinding === bindingKey) return;
                    observer.lastNativeEndedBinding = bindingKey;

                    var capturedTargetEnded = !!timer
                        && (timer.mode === 'end-current' || timer.mode === 'end-next')
                        && assetId === timer.targetAssetId
                        && assetId === timer.boundAssetId
                        && requestVersion === Number(timer.playbackRequestVersion)
                        && audioSourceMatches(element, timer);
                    if (capturedTargetEnded) {
                        // The actual native ended signal is authoritative even when the
                        // container's authored endpoint is rounded beyond media duration.
                        event.preventDefault();
                        event.stopImmediatePropagation();
                        if (!element.paused) element.pause();
                        try {
                            var expiry = observer.dotNetRef.invokeMethodAsync('HandleNativeSleepTimerExpired',
                                timer.timerGeneration, assetId, requestVersion, currentTime, true);
                            if (expiry && typeof expiry.catch === 'function') expiry.catch(function () { });
                        } catch (_) { }
                        return;
                    }

                    try {
                        var completion = observer.dotNetRef.invokeMethodAsync('HandleNativeAudioEnded',
                            assetId, requestVersion, timerGeneration, currentTime);
                        if (completion && typeof completion.catch === 'function') completion.catch(function () { });
                    } catch (_) { }
                },
                captionTracks: new Set(),
                onCaptionLayoutChanged: function () { observer.onCaptionCueChanged(); },
                onCaptionCueChanged: function () { notify(true); },
                onCaptionTracksChanged: function () {
                    synchronizeCaptionSelection(element);
                    for (const track of Array.from(element.textTracks || [])) {
                        if (track.kind !== 'captions' && track.kind !== 'subtitles') continue;
                        if (!observer.captionTracks.has(track)) {
                            observer.captionTracks.add(track);
                            track.addEventListener('cuechange', observer.onCaptionCueChanged);
                        }
                    }
                    observer.onCaptionCueChanged();
                    notify(true);
                }
            };

            element.addEventListener('timeupdate', observer.onTimeUpdate);
            element.addEventListener('durationchange', observer.onMetadataChanged);
            element.addEventListener('loadedmetadata', observer.onMetadataChanged);
            element.addEventListener('ratechange', observer.onMetadataChanged);
            element.addEventListener('volumechange', observer.onMetadataChanged);
            element.addEventListener('timeupdate', observer.onSleepTimerProgress);
            element.addEventListener('durationchange', observer.onSleepTimerProgress);
            element.addEventListener('loadedmetadata', observer.onSleepTimerProgress);
            element.addEventListener('ratechange', observer.onSleepTimerProgress);
            element.addEventListener('seeking', observer.onSleepTimerProgress);
            element.addEventListener('seeked', observer.onSleepTimerProgress);
            element.addEventListener('playing', observer.onSleepTimerProgress);
            element.addEventListener('waiting', observer.onSleepTimerProgress);
            element.addEventListener('ended', observer.onNativeAudioEnded, true);
            element.addEventListener('play', observer.onNativePlaybackRestart);
            element.textTracks?.addEventListener('addtrack', observer.onCaptionTracksChanged);
            element.textTracks?.addEventListener('removetrack', observer.onCaptionTracksChanged);
            element.textTracks?.addEventListener('change', observer.onCaptionTracksChanged);
            window.addEventListener('resize', observer.onCaptionLayoutChanged);
            window.visualViewport?.addEventListener('resize', observer.onCaptionLayoutChanged);
            element.addEventListener('enterpictureinpicture', observer.onCaptionLayoutChanged);
            element.addEventListener('leavepictureinpicture', observer.onCaptionLayoutChanged);
            var host = element.closest('.video-playback-host');
            if (host && typeof MutationObserver !== 'undefined') {
                observer.hostObserver = new MutationObserver(observer.onCaptionLayoutChanged);
                observer.hostObserver.observe(host, { attributes: true, attributeFilter: ['class'] });
                observer.captionNodesObserver = new MutationObserver(observer.onCaptionTracksChanged);
                observer.captionNodesObserver.observe(element, { childList: true, subtree: true, attributes: true,
                    attributeFilter: ['data-playback-preferred', 'default'] });
            }
            setAudioObserver(element, observer);
            observer.onCaptionTracksChanged();
        },
        unregisterAudioStateObserver: function (element) {
            removeAudioObserver(element);
        },
        setAudiobookSleepTimer: async function (element, state, sourceUrl) {
            var observer = audioObserverFor(element);
            if (!observer) return false;
            var generation = Number(state && state.timerGeneration);
            if (!Number.isFinite(generation) || generation < observer.sleepTimerGeneration) return false;
            if (!state || state.mode === 'off') {
                var activeTimer = observer.sleepTimer;
                var offAssetId = typeof state.boundAssetId === 'string' ? state.boundAssetId.replace(/-/g, '').toLowerCase() : '';
                var hasOffAssetScope = offAssetId.length === 32 && offAssetId !== '00000000000000000000000000000000';
                var offRequestVersion = Number(state.playbackRequestVersion);
                var hasOffRequestScope = Number.isFinite(offRequestVersion) && offRequestVersion > 0;
                if (activeTimer
                    && ((hasOffAssetScope && activeTimer.boundAssetId !== state.boundAssetId)
                        || (hasOffRequestScope && activeTimer.playbackRequestVersion !== offRequestVersion))) return false;
                if (observer.sleepTimerTimeout) window.clearTimeout(observer.sleepTimerTimeout);
                observer.sleepTimerTimeout = null;
                cancelPendingSleepTimerBinding(observer);
                observer.sleepTimer = null;
                observer.sleepTimerGeneration = generation;
                return true;
            }
            if (!sourceUrl) return false;
            var oldTimer = observer.sleepTimer;
            if (oldTimer && generation === oldTimer.timerGeneration) {
                // A generation identifies one immutable user choice. Only its live source
                // binding may move as the same audiobook advances between authorized assets.
                // A same-generation retry cannot silently replace the captured target/deadline.
                var sameChoice = oldTimer.mode === state.mode
                    && oldTimer.timerSessionId === state.timerSessionId
                    && oldTimer.profileId === state.profileId
                    && oldTimer.workId === state.workId
                    && oldTimer.originAssetId === state.originAssetId
                    && oldTimer.originChapterIndex === state.originChapterIndex
                    && oldTimer.targetAssetId === state.targetAssetId
                    && oldTimer.targetChapterIndex === state.targetChapterIndex
                    && oldTimer.targetChapterTitle === state.targetChapterTitle
                    && oldTimer.targetEndSeconds === state.targetEndSeconds
                    && oldTimer.chosenMinutes === state.chosenMinutes
                    && oldTimer.deadlineUtc === state.deadlineUtc;
                if (!sameChoice) return false;
            }
            cancelPendingSleepTimerBinding(observer);
            var deadlineUtc = Date.parse(state.deadlineUtc || '');
            var monotonicDeadline = null;
            if (state.mode === 'timer') {
                if (!Number.isFinite(deadlineUtc)) return false;
                monotonicDeadline = oldTimer
                    && oldTimer.timerSessionId === state.timerSessionId
                    && oldTimer.deadlineUtc === state.deadlineUtc
                    && Number.isFinite(oldTimer.monotonicDeadline)
                    ? oldTimer.monotonicDeadline
                    : performance.now() + Math.max(0, deadlineUtc - Date.now());
            }
            var candidate = {
                mode: state.mode,
                timerGeneration: state.timerGeneration,
                timerSessionId: state.timerSessionId,
                profileId: state.profileId,
                workId: state.workId,
                originAssetId: state.originAssetId,
                originChapterIndex: state.originChapterIndex,
                boundAssetId: state.boundAssetId,
                targetAssetId: state.targetAssetId,
                targetChapterIndex: state.targetChapterIndex,
                targetChapterTitle: state.targetChapterTitle,
                targetEndSeconds: state.targetEndSeconds,
                chosenMinutes: state.chosenMinutes,
                deadlineUtc: state.deadlineUtc,
                playbackRequestVersion: state.playbackRequestVersion,
                sourceUrl: sourceUrl,
                hlsInstance: null,
                monotonicDeadline: monotonicDeadline,
                fired: false
            };
            var registered = await waitForNativeSleepTimerSource(observer, candidate);
            return registered;
        },
        clearAudiobookSleepTimer: function (element) {
            var observer = audioObserverFor(element);
            if (observer) clearNativeSleepTimer(observer);
        },
        pauseAudioForSleepTimer: function (element, timerGeneration, assetId, playbackRequestVersion) {
            var observer = audioObserverFor(element);
            var timer = observer && observer.sleepTimer;
            if (!timer
                || timer.timerGeneration !== timerGeneration
                || timer.boundAssetId !== assetId
                || timer.playbackRequestVersion !== playbackRequestVersion
                || audioAssetId(element) !== assetId
                || !audioSourceMatches(element, timer)) return false;
            // Native expiry already paused this exact source before notifying .NET. A
            // concurrent scoped pause request may acknowledge that pause, but must not
            // pause again or mistake a replaced source for the original one.
            if (timer.fired) return element.paused === true;
            try { element.pause(); return true; }
            catch (_) { return false; }
        },
        registerPlayerShortcuts: registerPlayerShortcuts,
        unregisterPlayerShortcuts: unregisterPlayerShortcuts,
        loadAudio: function (element) {
            if (!element) return;
            try {
                element.load();
            } catch (error) {
                console.debug("Audio load was rejected.", error);
            }
        },
        ensureAudioSource: ensureAudioSource,
        consumeImmediateToggleHandled: function () {
            var now = Date.now();
            if (immediateToggleConsumedAt && now - immediateToggleConsumedAt < playbackConfig.immediateActionConsumeMilliseconds) {
                immediateToggleConsumedAt = 0;
                return true;
            }

            return false;
        },
        consumeImmediateSeekHandled: function () {
            var now = Date.now();
            if (immediateSeekConsumedAt && now - immediateSeekConsumedAt < playbackConfig.immediateActionConsumeMilliseconds) {
                immediateSeekConsumedAt = 0;
                return true;
            }

            return false;
        },
        playAudio: async function (element) {
            if (!element) return false;

            try {
                delete element.dataset.playbackStartFailure;
            await element.play();
                return true;
            } catch (error) {
                element.dataset.playbackStartFailure = error.name || "PlaybackError";
                console.debug("Audio play request was rejected.", error);
                return false;
            }
        },
        startAudio: startAudioElement,
        startFailure: function (element) { return element?.dataset.playbackStartFailure || "PlaybackError"; },
        pauseAudio: function (element) {
            if (!element) return;
            element.pause();
        },
        releaseMedia: function (element, expectedAssetId, expectedRequestVersion) {
            if (!element) return false;
            if (expectedAssetId && (audioAssetId(element)?.toLowerCase() !== String(expectedAssetId).toLowerCase()
                || Number(element.dataset.playbackRequestVersion) !== Number(expectedRequestVersion))) return false;
            element.pause();
            clearPreparedCaptions(element);
            element._tuvimaHls?.destroy();
            element._tuvimaHls = null;
            delete element.dataset.playbackSource;
            element.removeAttribute('src');
            element.load();
            delete element._tuvimaVideoBinding;
            return true;
        },
        seekAudio: function (element, seconds) {
            if (!element) return;
            var target = Math.max(0, seconds || 0);
            try {
                element.currentTime = target;
            } catch (error) {
                var applyWhenReady = function () {
                    try {
                        element.currentTime = target;
                    } catch (innerError) {
                        console.debug("Audio seek was rejected.", innerError);
                    }
                };
                element.addEventListener('loadedmetadata', applyWhenReady, { once: true });
            }
        },
        setVolume: function (element, volume) {
            if (!element) return;
            var next = Math.max(0, Math.min(1, volume || 0));
            element.volume = next;
        },
        setMuted: function (element, muted) {
            if (!element) return;
            element.muted = !!muted;
        },
        setPlaybackRate: function (element, rate) {
            if (!element) return;
            if (typeof rate !== 'number' || !isFinite(rate) || rate < 0.5 || rate > 3) return;
            try {
                element.playbackRate = rate;
            } catch (error) {
                console.debug("Audio playback rate was rejected.", error);
            }
        },
        toggleCaptions: function (element) {
            if (element?._tuvimaHls?.subtitleTracks?.length) {
                const hls = element._tuvimaHls;
                hls.subtitleTrack = hls.subtitleTrack >= 0 ? -1 : 0;
                hls.subtitleDisplay = hls.subtitleTrack >= 0;
                return hls.subtitleDisplay;
            }
            if (!element || !element.textTracks || element.textTracks.length === 0) return false;
            var shouldShow = true;
            for (var index = 0; index < element.textTracks.length; index++) {
                if (element.textTracks[index].mode === 'showing') {
                    shouldShow = false;
                    break;
                }
            }
            for (var trackIndex = 0; trackIndex < element.textTracks.length; trackIndex++) {
                element.textTracks[trackIndex].mode = shouldShow && trackIndex === 0 ? 'showing' : 'disabled';
            }
            return shouldShow;
        },
        selectCaptionTrack: selectCaptionTrack,
        synchronizeCaptionSelection: synchronizeCaptionSelection,
        selectAudioTrack: function (element, selectedIndex) {
            if (element?._tuvimaHls) {
                const hls = element._tuvimaHls;
                if (selectedIndex < 0 || selectedIndex >= hls.audioTracks.length) return false;
                hls.audioTrack = selectedIndex;
                return true;
            }
            if (!element || !element.audioTracks || element.audioTracks.length === 0) return false;
            var index = Math.max(0, Math.min(element.audioTracks.length - 1, selectedIndex || 0));
            for (var trackIndex = 0; trackIndex < element.audioTracks.length; trackIndex++) {
                element.audioTracks[trackIndex].enabled = trackIndex === index;
            }
            return true;
        },
        toggleFullscreen: async function (element, fallbackVideo) {
            if (!element) return false;
            try {
                if (document.fullscreenElement) {
                    await document.exitFullscreen();
                    return false;
                }
                if (document.webkitFullscreenElement && typeof document.webkitExitFullscreen === 'function') {
                    document.webkitExitFullscreen();
                    return false;
                }
                if (typeof element.requestFullscreen === 'function') {
                    await element.requestFullscreen();
                    return true;
                }
                if (typeof element.webkitRequestFullscreen === 'function') {
                    element.webkitRequestFullscreen();
                    return true;
                }
                const video = fallbackVideo || (element.matches?.('video') ? element : element.querySelector?.('video'));
                if (video?.webkitDisplayingFullscreen && typeof video.webkitExitFullscreen === 'function') {
                    video.webkitExitFullscreen();
                    return false;
                }
                if (typeof video?.webkitEnterFullscreen === 'function') {
                    video.webkitEnterFullscreen();
                    return true;
                }
            } catch (error) {
                console.debug("Video fullscreen request was rejected.", error);
            }
            return false;
        },
        togglePictureInPicture: async function (element) {
            if (!element || !document.pictureInPictureEnabled) return false;
            try {
                if (document.pictureInPictureElement) {
                    await document.exitPictureInPicture();
                    return false;
                }
                if (typeof element.requestPictureInPicture === 'function') {
                    await element.requestPictureInPicture();
                    return true;
                }
            } catch (error) {
                console.debug("Video picture-in-picture request was rejected.", error);
            }
            return false;
        },
        canPictureInPicture: function (element) {
            return !!element && !!document.pictureInPictureEnabled && typeof element.requestPictureInPicture === 'function';
        },
        exitPictureInPicture: async function () {
            if (document.pictureInPictureElement) await document.exitPictureInPicture();
        }
    };
})();

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
    fromStream: async function (fileName, streamReference) {
        const buffer = await streamReference.arrayBuffer();
        const blob = new Blob([buffer], { type: 'application/zip' });
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

    const capture = () => {
        try {
            const active = document.activeElement;
            sessionStorage.setItem(prefix + routeKey(), JSON.stringify({
                scrollY: window.scrollY,
                scrollContainers: Array.from(document.querySelectorAll('[data-detail-origin-scroll]'))
                    .map((element, index) => ({
                        key: element.getAttribute('data-detail-origin-scroll') || String(index),
                        top: element.scrollTop
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
                    const scrollContainers = Array.from(document.querySelectorAll('[data-detail-origin-scroll]'));
                    (state.scrollContainers || []).forEach((position, index) => {
                        const element = scrollContainers.find(candidate =>
                            candidate.getAttribute('data-detail-origin-scroll') === position.key)
                            || scrollContainers[index];
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
            if (destination.origin === window.location.origin
                && destination.pathname.startsWith('/details/')
                && !window.location.pathname.startsWith('/details/')) {
                capture();
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

    // Detail pages scroll inside the shell's main pane, not the frame content.
    const freshScrollSelector = '.playback-app-frame__content, [data-detail-origin-scroll], .context-sidebar-shell__main';
    const resetFresh = () => {
        window.scrollTo({ top: 0, behavior: 'instant' });
        document.querySelectorAll(freshScrollSelector).forEach(element => { element.scrollTop = 0; });
    };
    const fresh = route => {
        capture();
        navigationEpoch++;
        const target = new URL(route, window.location.href);
        freshRoute = `${target.pathname}${target.search}`;
        // Keep the intent through asynchronous page mounting and its origin-restore callback.
        const observer = new MutationObserver(() => { if (routeKey() === freshRoute) resetFresh(); });
        observer.observe(document.body, { childList: true, subtree: true });
        requestAnimationFrame(() => { if (routeKey() === freshRoute) resetFresh(); });
        const epoch = navigationEpoch;
        setTimeout(() => {
            if (epoch === navigationEpoch) { if (routeKey() === freshRoute) resetFresh(); freshRoute = null; }
            observer.disconnect();
        }, 1000);
    };
    return { initialize, capture, restore, back, fresh };
})();

// Register before the first detail link is clicked so the originating lane
// position can be captured on the initial navigation.
window.detailOrigin.initialize();

window.playbackTools = window.playbackTools || {
    dockObserver: null,
    dockObserverOwnerKey: null,
    resetContentScroll: function () {
        const content = document.querySelector('.playback-app-frame__content');
        if (content) content.scrollTop = 0;
    },
    isEditableFocus: function () {
        const active = document.activeElement;
        return active instanceof HTMLInputElement || active instanceof HTMLTextAreaElement ||
            active instanceof HTMLSelectElement || !!active?.isContentEditable;
    },
    playerReturnFocus: null,
    capturePlayerFocus: function (element) {
        this.playerReturnFocus = document.activeElement;
        if (element instanceof HTMLElement) element.focus();
    },
    restorePlayerFocus: function () {
        const target = this.playerReturnFocus;
        this.playerReturnFocus = null;
        if (target instanceof HTMLElement && target.isConnected) target.focus();
    },
    observeDock: function (element, ownerKey) {
        this.disconnectDock();
        this.dockObserverOwnerKey = ownerKey || {};
        if (!(element instanceof HTMLElement)) return;
        const update = () => {
            const height = Math.max(0, element.getBoundingClientRect().height);
            document.documentElement.style.setProperty('--tl-audio-dock-height', `${height}px`);
        };
        this.dockObserver = new ResizeObserver(update);
        this.dockObserver.observe(element);
        update();
    },
    disconnectDock: function (ownerKey) {
        if (ownerKey !== undefined && ownerKey !== this.dockObserverOwnerKey) return false;
        this.dockObserver?.disconnect();
        this.dockObserver = null;
        this.dockObserverOwnerKey = null;
        document.documentElement.style.setProperty('--tl-audio-dock-height', '0px');
        return true;
    },
    scrollActiveChapter: function () {
        window.requestAnimationFrame(() => {
            const active = document.querySelector('[data-playback-active-chapter="true"]');
            if (active instanceof HTMLElement) {
                active.scrollIntoView({ block: 'center', behavior: 'smooth' });
            }
        });
    }
};

// Scroll the existing episode rail without changing the rendered item set.
window.scrollSequenceItem = (id, index, align = 'nearest', behavior = 'smooth') => {
    const rail = document.getElementById(id);
    const item = rail?.querySelector(`[data-sequence-index="${index}"]`) ?? rail?.children[index];
    if (!rail || !item) return;

    const unclampedLeft = align === 'center'
        ? item.offsetLeft - ((rail.clientWidth - item.clientWidth) / 2)
        : item.offsetLeft - rail.offsetLeft;
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
    const root = element.closest('[role="menu"],.media-rate-control,.playback-popover-content') || element;
    const items = Array.from(root.querySelectorAll('[role="menuitem"],[role="menuitemcheckbox"]')).filter(item => !item.disabled);
    if (!items.length) return;
    const current = items.indexOf(document.activeElement);
    const index = key === 'Home' ? 0 : key === 'End' ? items.length - 1 : (current + (key === 'ArrowUp' || key === 'ArrowLeft' ? -1 : 1) + items.length) % items.length;
    items[index].focus();
} };
