// Page reader (PDF and comic reader spec D1-D9). The Blazor component PageReader owns the top line, slider and panels;
// this module owns everything that touches the pages: fetching them, laying them out (one page, two pages, scrolling),
// fit and zoom, turning (keys, taps, swipes), and the bars that fade away after a few seconds.
//
// A page source gives the module pages to draw. The comic source asks the Engine for the page list and page pictures
// through the Dashboard (/engine-book/{id}/pages); the PDF source (added in the same file) draws with PDF.js.

import { buildViews, chooseWidth as pickWidth, effectiveLayout as layoutFor, isRightToLeft, keyAction, tapAction, viewIndexOf } from "./page-reader-layout.js";

const STORAGE_KEY = "tuvima.pageReader.v1";
const CHROME_HIDE_MS = 3000;
const MIN_ZOOM = 1;
const MAX_ZOOM = 4;

let state = null;

// ---- Settings kept on this device ------------------------------------------------------------------------------

export function loadSettings() {
    try {
        const raw = localStorage.getItem(STORAGE_KEY);
        return raw ? JSON.parse(raw) : {};
    } catch {
        return {};
    }
}

export function saveSettings(partial) {
    try {
        localStorage.setItem(STORAGE_KEY, JSON.stringify({ ...loadSettings(), ...partial }));
    } catch {
        // Private windows and blocked storage simply do not remember; the reader works without it.
    }
}

// ---- Opening ---------------------------------------------------------------------------------------------------

/**
 * Opens the pages of one title in `host`.
 * options: { kind: "comic"|"pdf", startPage, layout, fit, direction, background }
 * Returns { ok, code, message, pageCount, rightToLeft }.
 */
export async function open(host, baseUrl, options, dotnet) {
    dispose();
    const shell = host.closest(".pr-shell") || host;
    state = {
        host,
        shell,
        baseUrl,
        dotnet,
        layout: options.layout || "auto",
        fit: options.fit || "page",
        direction: options.direction || "auto",
        background: options.background || "black",
        pages: [],
        widths: [],
        rtl: false,
        views: [],
        viewIndex: 0,
        current: 1,
        zoom: { z: 1, x: 0, y: 0 },
        paused: false,
        chromeTimer: 0,
        resizeTimer: 0,
        pointers: new Map(),
        gesture: null,
        disposed: false,
        source: null,
        scrollObserver: null,
        cleanup: [],
    };
    applyBackground();

    try {
        state.source = options.kind === "pdf" ? await createPdfSource(baseUrl, dotnet) : await createComicSource(baseUrl);
    } catch (error) {
        return failure(error);
    }

    const source = state.source;
    state.pages = source.pages;
    state.widths = source.widths || [];
    state.rtl = !!source.rightToLeft;
    if (state.pages.length === 0) {
        return { ok: false, code: "empty", message: "There are no pages in this file.", pageCount: 0, rightToLeft: false };
    }

    state.stage = document.createElement("div");
    state.stage.className = "pr-stage";
    state.stage.tabIndex = -1;
    host.replaceChildren(state.stage);
    wireInput();
    state.current = clamp(options.startPage || 1, 1, state.pages.length);
    render();
    showChrome();
    return { ok: true, code: "ok", message: "", pageCount: state.pages.length, rightToLeft: state.rtl };
}

function failure(error) {
    const code = error && error.code ? error.code : "unavailable";
    const message = error && error.message ? error.message : "This file can't be opened. It may be damaged or protected.";
    return { ok: false, code, message, pageCount: 0, rightToLeft: false };
}

export function dispose() {
    if (!state) {
        return;
    }

    state.disposed = true;
    clearTimeout(state.chromeTimer);
    clearTimeout(state.resizeTimer);
    state.scrollObserver?.disconnect();
    for (const undo of state.cleanup) {
        undo();
    }

    try {
        state.source?.dispose?.();
    } catch {
        // Closing a source that is already gone is not a failure.
    }

    state = null;
}

// ---- Page sources ----------------------------------------------------------------------------------------------

async function createComicSource(baseUrl) {
    let response;
    try {
        response = await fetch(`${baseUrl}/pages`, { credentials: "same-origin", headers: { Accept: "application/json" } });
    } catch {
        throw { code: "offline", message: "Library can't reach the Engine. Your place is saved." };
    }

    if (response.status === 404) {
        throw { code: "notfound", message: "This title isn't available." };
    }

    if (response.status === 401 || response.status === 429 || response.status === 502) {
        throw { code: "offline", message: "Library can't reach the Engine. Your place is saved." };
    }

    if (!response.ok) {
        throw { code: "damaged", message: "This file can't be opened. It may be damaged or protected." };
    }

    const list = await response.json();
    const pages = (list.pages || []).map(page => ({ number: page.number, width: page.width || 0, height: page.height || 0 }));
    return {
        kind: "comic",
        pages,
        widths: (list.widths || []).filter(width => width > 0).sort((a, b) => a - b),
        rightToLeft: !!list.right_to_left,
        // A picture address for page `number` at `width` pixels wide (0 asks for the original).
        pictureUrl(number, width) {
            return `${baseUrl}/pages/${number}${width > 0 ? `?width=${width}` : ""}`;
        },
        // Fills `holder` (an element) with the page picture; resolves when it is there or has failed.
        draw(holder, number, width) {
            return new Promise(resolve => {
                const image = new Image();
                image.className = "pr-img";
                image.alt = `Page ${number}`;
                image.draggable = false;
                image.decoding = "async";
                image.onload = () => {
                    holder.classList.remove("pr-page--loading");
                    resolve(true);
                };
                image.onerror = () => {
                    holder.classList.remove("pr-page--loading");
                    holder.classList.add("pr-page--broken");
                    holder.replaceChildren(brokenNote());
                    resolve(false);
                };
                image.src = this.pictureUrl(number, width);
                holder.replaceChildren(image);
            });
        },
        preload(number, width) {
            const image = new Image();
            image.src = this.pictureUrl(number, width);
        },
    };
}

// PDF pages are drawn by the PDF.js build the Dashboard already ships. Added with the PDF packet.
async function createPdfSource() {
    throw { code: "unsupported", message: "PDFs open in the next update." };
}

function brokenNote() {
    const note = document.createElement("div");
    note.className = "pr-broken";
    note.textContent = "This page can't be shown";
    return note;
}

// ---- Layout -----------------------------------------------------------------------------------------------------

function effectiveLayout() {
    return layoutFor(state.layout, state.host.clientWidth, state.host.clientHeight);
}

function effectiveRtl() {
    return isRightToLeft(state.direction, state.rtl);
}

function viewOf(pageNumber) {
    return viewIndexOf(state.views, pageNumber);
}

function render() {
    if (!state) {
        return;
    }

    state.scrollObserver?.disconnect();
    state.scrollObserver = null;
    const mode = effectiveLayout();
    state.mode = mode;
    state.views = buildViews(mode, state.pages);
    state.viewIndex = viewOf(state.current);
    state.zoom = { z: 1, x: 0, y: 0 };
    state.stage.className = `pr-stage pr-stage--${mode === "scroll" ? "scroll" : "paged"} pr-fit--${state.fit}`;
    state.stage.dataset.rtl = effectiveRtl() ? "true" : "false";

    if (mode === "scroll") {
        renderScroll();
    } else {
        renderView();
    }

    announce();
}

function neededWidth(page, perRow, zoom) {
    const stageW = state.stage.clientWidth || 1;
    const stageH = state.stage.clientHeight || 1;
    const ratio = page.height > 0 ? page.width / page.height : 0.66;
    let css = state.fit === "width" ? stageW / perRow : Math.min(stageW / perRow, stageH * ratio);
    if (state.mode === "scroll") {
        css = Math.min(stageW, 1000);
    }

    return Math.ceil(css * (window.devicePixelRatio || 1) * zoom);
}

function chooseWidth(needed) {
    return pickWidth(state.widths, needed);
}

function renderView() {
    const pagesInView = state.views[state.viewIndex];
    const view = document.createElement("div");
    view.className = "pr-view";
    view.style.setProperty("--n", String(pagesInView.length));
    const zoomBox = document.createElement("div");
    zoomBox.className = "pr-zoom";
    zoomBox.appendChild(view);
    state.zoomBox = zoomBox;
    state.view = view;

    const ordered = effectiveRtl() ? [...pagesInView].reverse() : pagesInView;
    for (const number of ordered) {
        const holder = document.createElement("div");
        holder.className = "pr-page pr-page--loading";
        holder.dataset.page = String(number);
        const page = state.pages[number - 1];
        if (page.width > 0 && page.height > 0) {
            holder.style.aspectRatio = `${page.width} / ${page.height}`;
        }

        view.appendChild(holder);
        state.source.draw(holder, number, chooseWidth(neededWidth(page, pagesInView.length, 1)));
    }

    state.stage.replaceChildren(zoomBox);
    state.stage.scrollTop = 0;
    state.current = pagesInView[0];
    report();
    preloadAround();
}

function renderScroll() {
    const list = document.createElement("div");
    list.className = "pr-scrolllist";
    state.view = list;
    state.zoomBox = list;
    const holders = [];
    for (const page of state.pages) {
        const holder = document.createElement("div");
        holder.className = "pr-page pr-page--loading pr-page--stacked";
        holder.dataset.page = String(page.number);
        if (page.width > 0 && page.height > 0) {
            holder.style.aspectRatio = `${page.width} / ${page.height}`;
        }

        list.appendChild(holder);
        holders.push(holder);
    }

    state.stage.replaceChildren(list);
    const drawn = new Set();
    state.scrollObserver = new IntersectionObserver(entries => {
        for (const entry of entries) {
            if (!entry.isIntersecting) {
                continue;
            }

            const number = Number(entry.target.dataset.page);
            if (!drawn.has(number)) {
                drawn.add(number);
                const page = state.pages[number - 1];
                state.source.draw(entry.target, number, chooseWidth(neededWidth(page, 1, 1)));
            }
        }
    }, { root: state.stage, rootMargin: "1500px 0px" });
    holders.forEach(holder => state.scrollObserver.observe(holder));

    const onScroll = () => {
        if (state.mode !== "scroll") {
            return;
        }

        const middle = state.stage.scrollTop + state.stage.clientHeight / 2;
        let best = 1;
        for (const holder of holders) {
            if (holder.offsetTop <= middle) {
                best = Number(holder.dataset.page);
            } else {
                break;
            }
        }

        if (best !== state.current) {
            state.current = best;
            report();
        }
    };
    state.stage.addEventListener("scroll", onScroll, { passive: true });
    state.cleanup.push(() => state.stage?.removeEventListener("scroll", onScroll));
    requestAnimationFrame(() => holders[state.current - 1]?.scrollIntoView({ block: "start" }));
    report();
}

function preloadAround() {
    for (const offset of [1, -1, 2]) {
        const view = state.views[state.viewIndex + offset];
        if (!view) {
            continue;
        }

        for (const number of view) {
            const page = state.pages[number - 1];
            state.source.preload?.(number, chooseWidth(neededWidth(page, view.length, 1)));
        }
    }
}

// ---- Turning -----------------------------------------------------------------------------------------------------

export function goTo(pageNumber) {
    if (!state) {
        return;
    }

    state.current = clamp(Math.round(pageNumber), 1, state.pages.length);
    if (state.mode === "scroll") {
        state.stage.querySelector(`[data-page="${state.current}"]`)?.scrollIntoView({ block: "start" });
        report();
        return;
    }

    const index = viewOf(state.current);
    if (index !== state.viewIndex || !state.view) {
        state.viewIndex = index;
        state.zoom = { z: 1, x: 0, y: 0 };
        renderView();
        announce();
    }
}

export function next() {
    if (!state) {
        return;
    }

    if (state.mode === "scroll") {
        state.stage.scrollBy({ top: state.stage.clientHeight * 0.85, behavior: "auto" });
        return;
    }

    if (state.viewIndex + 1 >= state.views.length) {
        state.dotnet.invokeMethodAsync("OnEndReached");
        return;
    }

    goTo(state.views[state.viewIndex + 1][0]);
}

export function prev() {
    if (!state) {
        return;
    }

    if (state.mode === "scroll") {
        state.stage.scrollBy({ top: -state.stage.clientHeight * 0.85, behavior: "auto" });
        return;
    }

    if (state.viewIndex > 0) {
        goTo(state.views[state.viewIndex - 1][0]);
    }
}

function report() {
    if (!state) {
        return;
    }

    const pagesInView = state.mode === "scroll" ? [state.current] : state.views[state.viewIndex];
    state.dotnet.invokeMethodAsync("OnPageChanged", pagesInView[0], state.pages.length, pagesInView.length > 1 ? pagesInView[pagesInView.length - 1] : pagesInView[0]);
}

function announce() {
    // The page is announced to screen readers by the component's live region (OnPageChanged).
}

// ---- Settings applied live ---------------------------------------------------------------------------------------

export function setLayout(layout) {
    if (!state) {
        return;
    }

    state.layout = layout;
    render();
}

export function setFit(fit) {
    if (!state) {
        return;
    }

    state.fit = fit;
    render();
}

export function setDirection(direction) {
    if (!state) {
        return;
    }

    state.direction = direction;
    render();
}

export function setBackground(background) {
    if (!state) {
        return;
    }

    state.background = background;
    applyBackground();
}

function applyBackground() {
    state.shell.dataset.background = state.background;
}

// While a panel is open the pages ignore the keyboard and taps.
export function setInputPaused(paused) {
    if (state) {
        state.paused = !!paused;
        if (paused) {
            showChrome(true);
        } else {
            showChrome();
        }
    }
}

// ---- Bars -------------------------------------------------------------------------------------------------------

export function showChrome(hold) {
    if (!state) {
        return;
    }

    state.shell.dataset.chrome = "shown";
    clearTimeout(state.chromeTimer);
    if (!hold && !state.paused) {
        state.chromeTimer = setTimeout(() => {
            if (state && !state.paused && !state.shell.querySelector(".pr-bar:hover")) {
                state.shell.dataset.chrome = "hidden";
            }
        }, CHROME_HIDE_MS);
    }
}

function toggleChrome() {
    if (state.shell.dataset.chrome === "shown") {
        clearTimeout(state.chromeTimer);
        state.shell.dataset.chrome = "hidden";
    } else {
        showChrome();
    }
}

// ---- Input -------------------------------------------------------------------------------------------------------

function wireInput() {
    const stage = state.stage;
    const on = (target, type, handler, options) => {
        target.addEventListener(type, handler, options);
        state.cleanup.push(() => target.removeEventListener(type, handler, options));
    };

    on(stage, "pointerdown", onPointerDown);
    on(stage, "pointermove", onPointerMove);
    on(stage, "pointerup", onPointerUp);
    on(stage, "pointercancel", onPointerCancel);
    on(stage, "wheel", onWheel, { passive: false });
    on(document, "keydown", onKeyDown);
    on(state.shell, "pointermove", onShellPointerMove);
    on(window, "resize", onResize);
    on(window, "orientationchange", onResize);
}

function onShellPointerMove(event) {
    const rect = state.shell.getBoundingClientRect();
    const nearEdge = event.clientY - rect.top < 90 || rect.bottom - event.clientY < 90;
    if (event.pointerType === "mouse" && nearEdge) {
        showChrome();
    }
}

function onResize() {
    clearTimeout(state.resizeTimer);
    state.resizeTimer = setTimeout(() => {
        if (state) {
            render();
        }
    }, 150);
}

function onKeyDown(event) {
    if (!state || state.paused || event.defaultPrevented) {
        return;
    }

    const target = event.target;
    if (target && (target.tagName === "INPUT" || target.tagName === "TEXTAREA" || target.isContentEditable)) {
        return;
    }

    const action = keyAction(event.key, event.shiftKey, effectiveRtl());
    switch (action) {
        case "next": next(); break;
        case "previous": prev(); break;
        case "first": goTo(1); break;
        case "last": goTo(state.pages.length); break;
        case "zoom-in": zoomBy(1.25); break;
        case "zoom-out": zoomBy(0.8); break;
        case "zoom-reset": resetZoom(); break;
        case "close": state.dotnet.invokeMethodAsync("OnEscape"); break;
        default:
            showChrome();
            return;
    }

    event.preventDefault();
}

function onWheel(event) {
    if (!state || state.paused) {
        return;
    }

    if (event.ctrlKey || event.metaKey) {
        event.preventDefault();
        zoomAt(event.deltaY < 0 ? 1.15 : 1 / 1.15, event.clientX, event.clientY);
    }
    // A plain wheel scrolls Scrolling layouts and Width fit natively; it never turns the page (D8).
}

function onPointerDown(event) {
    if (!state || state.paused) {
        return;
    }

    state.stage.setPointerCapture?.(event.pointerId);
    state.pointers.set(event.pointerId, { x: event.clientX, y: event.clientY, startX: event.clientX, startY: event.clientY, t: performance.now(), type: event.pointerType });
    if (state.pointers.size === 2) {
        const [a, b] = [...state.pointers.values()];
        state.gesture = { kind: "pinch", dist: Math.hypot(a.x - b.x, a.y - b.y), zoom: state.zoom.z };
    }
}

function onPointerMove(event) {
    const pointer = state?.pointers.get(event.pointerId);
    if (!pointer) {
        return;
    }

    const dx = event.clientX - pointer.x;
    const dy = event.clientY - pointer.y;
    pointer.x = event.clientX;
    pointer.y = event.clientY;

    if (state.pointers.size === 2 && state.gesture?.kind === "pinch") {
        const [a, b] = [...state.pointers.values()];
        const dist = Math.hypot(a.x - b.x, a.y - b.y);
        const target = clamp(state.gesture.zoom * (dist / state.gesture.dist), MIN_ZOOM, MAX_ZOOM);
        setZoom(target, (a.x + b.x) / 2, (a.y + b.y) / 2);
        return;
    }

    if (state.zoom.z > 1) {
        state.zoom.x += dx;
        state.zoom.y += dy;
        applyZoom();
    }
}

function onPointerUp(event) {
    if (!state) {
        return;
    }

    const pointer = state.pointers.get(event.pointerId);
    state.pointers.delete(event.pointerId);
    if (state.gesture && state.pointers.size < 2) {
        state.gesture = null;
        refineZoomPicture();
        return;
    }

    if (!pointer || state.paused) {
        return;
    }

    const movedX = event.clientX - pointer.startX;
    const movedY = event.clientY - pointer.startY;
    const distance = Math.hypot(movedX, movedY);
    const elapsed = performance.now() - pointer.t;
    const rtl = effectiveRtl();

    // A quick sideways swipe turns the page (when not zoomed).
    if (state.zoom.z === 1 && state.mode !== "scroll" && Math.abs(movedX) > 50 && Math.abs(movedX) > Math.abs(movedY) * 1.5 && elapsed < 600) {
        const toNext = rtl ? movedX > 0 : movedX < 0;
        toNext ? next() : prev();
        return;
    }

    if (distance > 10) {
        if (state.zoom.z > 1) {
            refineZoomPicture();
        }

        return;
    }

    // A tap or click.
    const now = performance.now();
    if (state.lastTap && now - state.lastTap.t < 320 && Math.hypot(event.clientX - state.lastTap.x, event.clientY - state.lastTap.y) < 30) {
        state.lastTap = null;
        state.zoom.z > 1 ? resetZoom() : zoomAt(2.5, event.clientX, event.clientY);
        return;
    }

    state.lastTap = { t: now, x: event.clientX, y: event.clientY };
    const tapAt = { x: event.clientX, y: event.clientY };
    clearTimeout(state.tapTimer);
    state.tapTimer = setTimeout(() => {
        if (state && state.lastTap && state.lastTap.t === now) {
            state.lastTap = null;
            handleTap(tapAt.x, tapAt.y);
        }
    }, 240);
}

function onPointerCancel(event) {
    state?.pointers.delete(event.pointerId);
    state && (state.gesture = null);
}

function handleTap(x) {
    const rect = state.stage.getBoundingClientRect();
    const action = tapAction((x - rect.left) / rect.width, effectiveRtl(), state.zoom.z > 1 || state.mode === "scroll");
    if (action === "next") {
        next();
    } else if (action === "previous") {
        prev();
    } else {
        toggleChrome();
    }
}

// ---- Zoom --------------------------------------------------------------------------------------------------------

function zoomBy(factor) {
    const rect = state.stage.getBoundingClientRect();
    zoomAt(factor, rect.left + rect.width / 2, rect.top + rect.height / 2);
}

function zoomAt(factor, clientX, clientY) {
    setZoom(clamp(state.zoom.z * factor, MIN_ZOOM, MAX_ZOOM), clientX, clientY);
    refineZoomPicture();
}

function setZoom(target, clientX, clientY) {
    if (state.mode === "scroll") {
        return;
    }

    const rect = state.stage.getBoundingClientRect();
    const px = clientX - rect.left;
    const py = clientY - rect.top;
    const ratio = target / state.zoom.z;
    state.zoom.x = px - (px - state.zoom.x) * ratio;
    state.zoom.y = py - (py - state.zoom.y) * ratio;
    state.zoom.z = target;
    if (target === 1) {
        state.zoom.x = 0;
        state.zoom.y = 0;
    }

    applyZoom();
}

function resetZoom() {
    state.zoom = { z: 1, x: 0, y: 0 };
    applyZoom();
}

function applyZoom() {
    if (!state.zoomBox || state.mode === "scroll") {
        return;
    }

    const { z, x, y } = state.zoom;
    state.zoomBox.style.transform = z === 1 ? "" : `translate(${x}px, ${y}px) scale(${z})`;
    state.stage.classList.toggle("pr-stage--zoomed", z > 1);
}

// After zooming in, swap the page pictures for sharper ones (originals when zoomed a lot, D18).
function refineZoomPicture() {
    if (!state || state.mode === "scroll" || !state.view) {
        return;
    }

    const pagesInView = state.views[state.viewIndex];
    for (const holder of state.view.querySelectorAll(".pr-page")) {
        const img = holder.querySelector("img");
        const number = Number(holder.dataset.page);
        const page = state.pages[number - 1];
        if (!img || !page) {
            continue;
        }

        const wanted = state.zoom.z > 1.5 ? chooseWidth(neededWidth(page, pagesInView.length, state.zoom.z)) : chooseWidth(neededWidth(page, pagesInView.length, 1));
        const wantedUrl = state.source.pictureUrl(number, wanted);
        if (img.dataset.zoomUrl !== wantedUrl && state.zoom.z > 1) {
            img.dataset.zoomUrl = wantedUrl;
            const sharper = new Image();
            sharper.onload = () => {
                if (state && img.isConnected) {
                    img.src = wantedUrl;
                }
            };
            sharper.src = wantedUrl;
        }
    }
}

function clamp(value, min, max) {
    return Math.min(max, Math.max(min, value));
}
