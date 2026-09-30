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

    // MudDialog also observes Escape at the document level. Stop the native
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

// All custom menus use the same click-away contract. MudMenu already handles
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
window.registerCtrlK = function (dotNetRef) {
    window.unregisterCtrlK();

    window._appCtrlKHandler = function (e) {
        if ((e.ctrlKey || e.metaKey) && e.key === 'k') {
            e.preventDefault();
            dotNetRef.invokeMethodAsync('OpenPalette');
        }
    };

    document.addEventListener('keydown', window._appCtrlKHandler);
};

window.unregisterCtrlK = function () {
    if (!window._appCtrlKHandler) {
        return;
    }

    document.removeEventListener('keydown', window._appCtrlKHandler);
    window._appCtrlKHandler = null;
};

// -- Device Context ---------------------------------------------------------

window.tuvimaResponsive = (function () {
    var observer = null;
    var observerTimer = null;
    var lastObservedClass = null;

    function readBreakpoint(name, fallback) {
        var value = window.getComputedStyle(document.documentElement)
            .getPropertyValue('--tl-breakpoint-' + name)
            .trim();
        var parsed = Number(value);
        return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
    }

    function classifyViewport() {
        return window.innerWidth <= readBreakpoint('navigation', 840) ? 'mobile' : 'web';
    }

    function register(dotNetRef) {
        unregister();
        observer = dotNetRef;
        lastObservedClass = classifyViewport();
        window.addEventListener('resize', onResize, { passive: true });
    }

    function onResize() {
        window.clearTimeout(observerTimer);
        observerTimer = window.setTimeout(function () {
            if (!observer) return;
            var next = classifyViewport();
            if (next === lastObservedClass) return;
            lastObservedClass = next;
            observer.invokeMethodAsync('HandleViewportDeviceClassChanged', next);
        }, 160);
    }

    function unregister() {
        window.removeEventListener('resize', onResize);
        window.clearTimeout(observerTimer);
        observerTimer = null;
        observer = null;
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
        return Array.prototype.filter.call(el.querySelectorAll('.media-tile, .media-group-tile'), function (item) {
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

window.updateMediaTileShelfVisibleWidth = function (el) {
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

    var style = window.getComputedStyle ? window.getComputedStyle(el) : null;
    var paddingTop = style ? parseFloat(style.paddingTop || '0') : 0;
    var paddingBottom = style ? parseFloat(style.paddingBottom || '0') : 0;
    paddingTop = Number.isFinite(paddingTop) ? paddingTop : 0;
    paddingBottom = Number.isFinite(paddingBottom) ? paddingBottom : 0;

    var restingHeight = 0;
    Array.prototype.forEach.call(el.querySelectorAll('.media-tile, .media-group-tile'), function (tile) {
        if (tile.closest('.media-tile-shelf-scroll') !== el) return;
        // Include captions beneath artwork as well as fixed-size group tiles.
        var rect = tile.getBoundingClientRect();
        restingHeight = Math.max(restingHeight, rect.height || tile.offsetHeight || 0);
    });

    if (restingHeight > 0) {
        el.style.height = Math.ceil(restingHeight + paddingTop + paddingBottom) + 'px';
        el.style.setProperty('--media-tile-row-height', Math.ceil(restingHeight) + 'px');
    }
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
        panel.style.removeProperty('--media-tile-hover-left');
        panel.style.removeProperty('--media-tile-hover-top');
        panel.style.removeProperty('--media-tile-hover-max-height');
        panel.style.removeProperty('--media-tile-hover-art-max-height');
        panel.style.setProperty('--media-tile-hover-anchor-width', Math.round(cardRect.width) + 'px');
        panel.style.setProperty('--media-tile-hover-anchor-height', Math.round(cardRect.height) + 'px');
        panel.style.setProperty('--media-tile-hover-max-height', Math.round(cardRect.height) + 'px');
        panel.style.left = '';
        panel.style.top = '';

        var body = panel.querySelector('.media-tile-hover-body');
        var bodyHeight = body ? body.getBoundingClientRect().height : 0;
        panel.style.setProperty('--media-tile-hover-art-max-height', Math.round(cardRect.height) + 'px');

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

        var estimatedPanelHeight = panelHeight;
        if (panelWidth > 0 && panel.classList.contains('is-banner-popover')) {
            estimatedPanelHeight = Math.max(estimatedPanelHeight, (panelWidth * 9 / 16) + bodyHeight + 8);
        } else if (panelWidth > 0 && panel.classList.contains('is-art-popover')) {
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

        panelHeight = Math.max(panelHeight, Math.min(estimatedPanelHeight, viewportHeight - (gutter * 2)));

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

    var mountParent = window.getMediaTileHoverHost();
    if (mountParent && panel.parentElement !== mountParent) {
        mountParent.appendChild(panel);
    }

    panel.classList.remove('is-visible');
    panel.classList.remove('is-positioned');
    panel.classList.add('is-viewport-mounted');
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

    var rowContainer = cardEl.closest('.media-tile-shelf-scroll, .media-tile-grid');
    var previousCard = rowContainer
        ? rowContainer.querySelector('.media-tile.is-hover-active')
        : null;
    if (previousCard && previousCard !== cardEl) {
        window.clearMediaTileHover(previousCard);
    }

    var panel = cardEl.querySelector('.media-tile-hover-panel');
    if (!panel) return;

    cardEl.__mediaTileHoverPanel = panel;

    if (cardEl.__mediaTileHideTimer) {
        window.clearTimeout(cardEl.__mediaTileHideTimer);
        cardEl.__mediaTileHideTimer = null;
    }

    var frame = cardEl.querySelector('.media-tile-frame') || cardEl;
    var frameRect = frame.getBoundingClientRect();
    var storedAnchorWidth = parseFloat(cardEl.style.getPropertyValue('--media-tile-hover-anchor-width'));
    var storedAnchorHeight = parseFloat(cardEl.style.getPropertyValue('--media-tile-hover-anchor-height'));
    var anchorWidth = frameRect.width > 16 ? frameRect.width : storedAnchorWidth;
    var anchorHeight = frameRect.height > 16 ? frameRect.height : storedAnchorHeight;
    if (!Number.isFinite(anchorWidth) || anchorWidth <= 16 || !Number.isFinite(anchorHeight) || anchorHeight <= 16) {
        return;
    }
    var rowRect = rowContainer ? rowContainer.getBoundingClientRect() : document.documentElement.getBoundingClientRect();
    var expandedWidth = Math.min(
        Math.max(anchorWidth * 2.15, 520),
        Math.max(anchorWidth, rowRect.width - 16),
        720);
    cardEl.style.setProperty('--media-tile-hover-anchor-width', Math.round(anchorWidth) + 'px');
    cardEl.style.setProperty('--media-tile-hover-anchor-height', Math.round(anchorHeight) + 'px');
    cardEl.style.setProperty('--media-tile-expanded-width', Math.round(expandedWidth) + 'px');
    if (rowContainer) {
        rowContainer.classList.add('has-active-in-row-hover');
    }
    cardEl.classList.add('is-hover-active');
    panel.classList.add('is-inline-expanded');
    var useGridOverlay = !!cardEl.closest('.media-tile-grid')
        && window.innerWidth > window.tuvimaResponsive.navigationBreakpoint()
        && (!window.matchMedia || window.matchMedia('(hover: hover) and (pointer: fine)').matches);

    if (useGridOverlay) {
        cardEl.classList.add('is-grid-overlay-anchor');
        panel.classList.add('is-grid-overlay');
        window.mountMediaTileHover(cardEl);
        panel.style.setProperty('--media-tile-expanded-width', Math.round(expandedWidth) + 'px');
    }

    var hoverImage = panel.querySelector('.media-tile-hover-image');
    if (hoverImage) {
        hoverImage.loading = 'eager';
        if ('fetchPriority' in hoverImage) {
            hoverImage.fetchPriority = 'high';
        }
    }

    window.requestAnimationFrame(function () {
        window.requestAnimationFrame(function () {
            if (cardEl.classList.contains('is-hover-active')) {
                if (useGridOverlay) {
                    window.positionMediaTileHover(cardEl);
                }
                panel.classList.add('is-visible');
                window.setTimeout(function () {
                    if (cardEl.classList.contains('is-hover-active')) {
                        if (useGridOverlay) {
                            window.scheduleMediaTileHoverViewportCorrection(cardEl);
                        } else {
                            window.keepMediaTileHoverInRowViewport(cardEl);
                        }
                    }
                }, 370);
            }
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

    var show = function () {
        if (cardEl.__mediaTileHideTimer) {
            window.clearTimeout(cardEl.__mediaTileHideTimer);
            cardEl.__mediaTileHideTimer = null;
        }

        if (cardEl.__mediaTileShowTimer) {
            window.clearTimeout(cardEl.__mediaTileShowTimer);
        }

        var row = cardEl.closest('.media-tile-shelf-scroll, .media-tile-grid');
        var activeCard = row ? row.querySelector('.media-tile.is-hover-active') : null;
        var showDelay = activeCard && activeCard !== cardEl ? 45 : 240;

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

window.listenPlayback = (function () {
    var playbackConfig = {
        popupWidth: 460,
        popupHeight: 820,
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
        if (stateHandler && json) {
            stateHandler.invokeMethodAsync('HandlePlaybackState', json);
        }
    }

    function notifyCommand(json) {
        if (commandHandler && json) {
            commandHandler.invokeMethodAsync('HandlePlaybackCommand', json);
        }
    }

    function readAudioElementState(element) {
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
            audioTrackCount: element._tuvimaHls?.audioTracks?.length || element.audioTracks?.length || 0,
            duration: isFinite(element.duration) ? element.duration : 0,
            volume: typeof element.volume === 'number' ? element.volume : playbackConfig.defaultVolume,
            muted: !!element.muted,
            paused: !!element.paused,
            playbackRate: typeof element.playbackRate === 'number' ? element.playbackRate : 1
        };
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
        element.textTracks?.removeEventListener('addtrack', observer.onCaptionTracksChanged);
        element.textTracks?.removeEventListener('removetrack', observer.onCaptionTracksChanged);
        observer.captionTracks.forEach(track => track.removeEventListener('cuechange', observer.onCaptionCueChanged));
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
        return !!(target.closest && target.closest('[contenteditable="true"]'));
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
            if (!event || isEditableShortcutTarget(event.target)) return;

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

            if (typeof playbackRate === 'number' && isFinite(playbackRate)) {
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

        popupWindow = window.open(
            url,
            popupName,
            'popup=yes,width=' + Math.round(playbackConfig.popupWidth) + ',height=' + Math.round(playbackConfig.popupHeight) + ',resizable=yes,scrollbars=no'
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
        getState: function () {
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
        closePopup: function () {
            if (popupWindow && !popupWindow.closed) {
                popupWindow.close();
            }

            popupWindow = null;
        },
        registerPopupWindow: function () {
            if (popupUnloadHandler) {
                window.removeEventListener('beforeunload', popupUnloadHandler);
            }

            popupUnloadHandler = function () {
                try {
                    var json = JSON.stringify({ action: 'popup-closed' });
                    localStorage.setItem(commandKey, json);
                    if (channel) {
                        channel.postMessage({ type: 'command', json: json });
                    }
                } catch (error) {
                    console.debug('Could not broadcast listen popup close.', error);
                }
            };

            window.addEventListener('beforeunload', popupUnloadHandler);
        },
        focusPopup: focusPopup,
        unregisterPopupWindow: function () {
            if (!popupUnloadHandler) return;
            window.removeEventListener('beforeunload', popupUnloadHandler);
            popupUnloadHandler = null;
        },
        closeOwnWindow: function () {
            window.close();
        },
        readAudioState: function (element) {
            return readAudioElementState(element);
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
                onTimeUpdate: function () { notify(false); },
                onMetadataChanged: function () { notify(true); },
                captionTracks: new Set(),
                onCaptionCueChanged: function () {
                    if (element.tagName !== 'VIDEO') return;
                    for (const track of Array.from(element.textTracks || [])) {
                        for (const cue of Array.from(track.activeCues || [])) {
                            // Preserve authored placement; lift automatic captions above transport.
                            if (cue.line === 'auto') {
                                cue.snapToLines = false;
                                cue.line = 72;
                            }
                        }
                    }
                },
                onCaptionTracksChanged: function () {
                    for (const track of Array.from(element.textTracks || [])) {
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
            element.textTracks?.addEventListener('addtrack', observer.onCaptionTracksChanged);
            element.textTracks?.addEventListener('removetrack', observer.onCaptionTracksChanged);
            setAudioObserver(element, observer);
            observer.onCaptionTracksChanged();
        },
        unregisterAudioStateObserver: function (element) {
            removeAudioObserver(element);
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
        releaseMedia: function (element) {
            if (!element) return;
            element.pause();
            clearPreparedCaptions(element);
            element._tuvimaHls?.destroy();
            element._tuvimaHls = null;
            delete element.dataset.playbackSource;
            element.removeAttribute('src');
            element.load();
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
            var next = Math.max(0.5, Math.min(32, rate || 1));
            try {
                element.playbackRate = next;
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
        selectCaptionTrack: function (element, key) {
            if (!element) return false;
            if (element._tuvimaHls) {
                element._tuvimaHls.subtitleTrack = -1;
                element._tuvimaHls.subtitleDisplay = false;
            }
            for (const track of Array.from(element.textTracks || [])) track.mode = 'disabled';
            if (!key) return true;
            const node = Array.from(element.querySelectorAll('track'))
                .find(track => track.dataset.playbackTrackKey === key);
            if (!node?.track) return false;
            node.track.mode = 'showing';
            return true;
        },
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
        toggleFullscreen: async function (element) {
            if (!element) return false;
            try {
                if (document.fullscreenElement) {
                    await document.exitFullscreen();
                    return false;
                }
                if (typeof element.requestFullscreen === 'function') {
                    await element.requestFullscreen();
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
        try {
            const raw = sessionStorage.getItem(prefix + routeKey());
            if (!raw) return;
            const state = JSON.parse(raw);
            window.requestAnimationFrame(() => {
                window.setTimeout(() => {
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
        window.addEventListener('popstate', () => window.setTimeout(restore, 0));
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

    return { initialize, capture, restore, back };
})();

// Register before the first detail link is clicked so the originating lane
// position can be captured on the initial navigation.
window.detailOrigin.initialize();

window.playbackTools = window.playbackTools || {
    dockObserver: null,
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
    observeDock: function (element) {
        this.disconnectDock();
        if (!(element instanceof HTMLElement)) return;
        const update = () => {
            const height = Math.max(0, element.getBoundingClientRect().height);
            document.documentElement.style.setProperty('--tl-audio-dock-height', `${height}px`);
        };
        this.dockObserver = new ResizeObserver(update);
        this.dockObserver.observe(element);
        update();
    },
    disconnectDock: function () {
        this.dockObserver?.disconnect();
        this.dockObserver = null;
        document.documentElement.style.setProperty('--tl-audio-dock-height', '0px');
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

// Rich selectors use the same keyboard contract even when MudPopover portals them.
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
