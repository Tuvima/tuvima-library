// Page reader (PDF and comic reader spec D1-D9). The Blazor component PageReader owns the top line, slider and panels;
// this module owns everything that touches the pages: fetching them, laying them out (one page, two pages, scrolling),
// fit and zoom, turning (keys, taps, swipes), and the bars that fade away after a few seconds.
//
// A page source gives the module pages to draw. The comic source asks the Engine for the page list and page pictures
// through the Dashboard (/engine-book/{id}/pages); the PDF source (added in the same file) draws with PDF.js.

import { barKeyAction, buildViews, chooseWidth as pickWidth, effectiveLayout as layoutFor, findHits, isRightToLeft, keyAction, normalizeForSearch, tapAction, viewIndexOf } from "./page-reader-layout.js";

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
    const shell = host.closest(".rf-shell") || host;
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

/** PDF only: the document outline as { title, level, page } items (empty when there is none). */
export async function getOutline() {
    return state?.source?.outline ? await state.source.outline() : [];
}

/** PDF only: starts a search; hits arrive through the component's OnSearchResults. Resolves { token, hasText }. */
export async function searchText(query) {
    if (!state?.source?.search) {
        return { token: 0, hasText: false };
    }

    return await state.source.search(query);
}

export function cancelSearch() {
    state?.source?.cancelSearch?.();
}

export function dispose() {
    if (!state) {
        return;
    }

    state.disposed = true;
    clearTimeout(state.peekTimer);
    state.grid?.observer.disconnect();
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

// PDF pages are drawn by the PDF.js build the Dashboard already ships (vendor/pdfjs, Apache-2.0, never from a CDN). PDF.js reads
// the file through the Dashboard's file route with range requests, so a big PDF opens without downloading all of it. Pages are
// drawn onto canvases at the width the layout needs (rounded up to a multiple of 64, never past MAX_PDF_WIDTH). There is no
// annotation layer, so links inside a PDF are not clickable; Contents and Search are the way to move around.
const MAX_PDF_WIDTH = 4096;
const MAX_PDF_PIXELS = 16 * 1024 * 1024;
const SEARCH_HIT_LIMIT = 200;
const OUTLINE_LIMIT = 500;

async function createPdfSource(baseUrl, dotnet) {
    const pdfjs = await import(new URL("../vendor/pdfjs/pdf.min.mjs", import.meta.url).href);
    pdfjs.GlobalWorkerOptions.workerSrc = new URL("../vendor/pdfjs/pdf.worker.min.mjs", import.meta.url).href;
    const task = pdfjs.getDocument({ url: `${baseUrl}/file`, withCredentials: true, isEvalSupported: false, enableScripting: false });
    let pdf;
    try {
        pdf = await task.promise;
    } catch (error) {
        throw pdfFailure(error);
    }

    const first = await pdf.getPage(1);
    const base = first.getViewport({ scale: 1 });
    const pages = Array.from({ length: pdf.numPages }, (_, index) => ({ number: index + 1, width: base.width, height: base.height }));
    const renders = new WeakMap();
    let searchToken = 0;
    let closed = false;

    async function paint(canvas, number, width) {
        const page = await pdf.getPage(number);
        const natural = page.getViewport({ scale: 1 });
        pages[number - 1].width = natural.width;
        pages[number - 1].height = natural.height;
        let scale = Math.max(160, Math.min(MAX_PDF_WIDTH, width)) / natural.width;
        const pixels = natural.width * scale * natural.height * scale;
        if (pixels > MAX_PDF_PIXELS) {
            scale *= Math.sqrt(MAX_PDF_PIXELS / pixels);
        }

        const viewport = page.getViewport({ scale });
        canvas.width = Math.floor(viewport.width);
        canvas.height = Math.floor(viewport.height);
        const render = page.render({ canvasContext: canvas.getContext("2d"), viewport });
        renders.set(canvas, render);
        try {
            await render.promise;
            return true;
        } catch (error) {
            if (error && error.name === "RenderingCancelledException") {
                return false;
            }
            throw error;
        } finally {
            page.cleanup();
        }
    }

    function newCanvas(number) {
        const canvas = document.createElement("canvas");
        canvas.className = "pr-img";
        canvas.setAttribute("role", "img");
        canvas.setAttribute("aria-label", `Page ${number}`);
        return canvas;
    }

    return {
        kind: "pdf",
        pages,
        widths: [],
        dynamicWidth: true,
        rightToLeft: false,
        pictureUrl(number, width) {
            return `pdf:${number}:${width}`;
        },
        async draw(holder, number, width) {
            const canvas = newCanvas(number);
            try {
                const done = await paint(canvas, number, width);
                if (done && !closed) {
                    holder.classList.remove("pr-page--loading");
                    holder.replaceChildren(canvas);
                }
                return done;
            } catch {
                holder.classList.remove("pr-page--loading");
                holder.classList.add("pr-page--broken");
                holder.replaceChildren(brokenNote());
                return false;
            }
        },
        // A sharper picture of a page that is already showing (after zooming in).
        async sharpen(holder, number, width) {
            const canvas = newCanvas(number);
            try {
                if (await paint(canvas, number, width) && !closed && holder.isConnected) {
                    holder.replaceChildren(canvas);
                }
            } catch {
                // The picture already showing stays.
            }
        },
        preload(number, width) {
            // Warm PDF.js' own page cache; nothing is drawn.
            pdf.getPage(number).then(page => page.cleanup?.(), () => {});
        },
        // Contents: the PDF's own outline as a flat list { title, level, page }, or [] when it has none.
        async outline() {
            let raw;
            try {
                raw = await pdf.getOutline();
            } catch {
                return [];
            }

            const items = [];
            const walk = async (nodes, level) => {
                for (const node of nodes || []) {
                    if (items.length >= OUTLINE_LIMIT || closed) {
                        return;
                    }

                    const page = await resolveDestination(node.dest);
                    if (page > 0 && node.title) {
                        items.push({ title: String(node.title).trim(), level, page });
                    }

                    await walk(node.items, level + 1);
                }
            };

            const resolveDestination = async dest => {
                try {
                    const explicit = typeof dest === "string" ? await pdf.getDestination(dest) : dest;
                    if (!Array.isArray(explicit) || explicit.length === 0) {
                        return 0;
                    }

                    const target = explicit[0];
                    return typeof target === "object" ? (await pdf.getPageIndex(target)) + 1 : Number(target) + 1;
                } catch {
                    return 0;
                }
            };

            await walk(raw, 0);
            return items;
        },
        // Search: reports hits page by page to the component so the list fills as it goes. A newer search stops an older one.
        async search(query) {
            const token = ++searchToken;
            const needle = normalizeForSearch(query);
            if (!needle) {
                return { token, hasText: true };
            }

            let hasText = false;
            let total = 0;
            for (let number = 1; number <= pdf.numPages && token === searchToken && !closed && total < SEARCH_HIT_LIMIT; number++) {
                const page = await pdf.getPage(number);
                const content = await page.getTextContent();
                page.cleanup();
                const text = content.items.map(item => item.str).join(" ").replace(/\s+/g, " ");
                if (text.trim().length > 0) {
                    hasText = true;
                }

                if (token !== searchToken || closed) {
                    break;
                }

                const hits = findHits(text, needle, number).slice(0, SEARCH_HIT_LIMIT - total);
                total += hits.length;
                if (hits.length > 0) {
                    await dotnet.invokeMethodAsync("OnSearchResults", token, hits, false);
                }
            }

            if (token === searchToken) {
                await dotnet.invokeMethodAsync("OnSearchResults", token, [], true);
            }

            return { token, hasText: hasText || pdf.numPages === 0 };
        },
        cancelSearch() {
            searchToken++;
        },
        dispose() {
            closed = true;
            searchToken++;
            task.destroy?.();
            pdf.destroy?.();
        },
    };
}

function pdfFailure(error) {
    const name = error && error.name;
    if (name === "PasswordException" || name === "InvalidPDFException" || name === "FormatError") {
        return { code: "damaged", message: "This file can't be opened. It may be damaged or protected." };
    }

    if (name === "MissingPDFException" || (error && error.status === 404)) {
        return { code: "notfound", message: "This title isn't available." };
    }

    if (name === "UnexpectedResponseException" && [401, 429, 502].includes(error.status)) {
        return { code: "offline", message: "Library can't reach the Engine. Your place is saved." };
    }

    if (name === "ResponseException" || name === "UnexpectedResponseException" || error instanceof TypeError) {
        return { code: "offline", message: "Library can't reach the Engine. Your place is saved." };
    }

    return { code: "damaged", message: "This file can't be opened. It may be damaged or protected." };
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
    if (state.source.dynamicWidth) {
        // A PDF is drawn at the width needed, in steps of 64 so near sizes share a picture.
        return clamp(Math.ceil(needed / 64) * 64, 160, MAX_PDF_WIDTH);
    }

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
        const stage = state.stage;
        if (stage.scrollTop + stage.clientHeight >= stage.scrollHeight - 2) {
            state.dotnet.invokeMethodAsync("OnEndReached");
            return;
        }

        stage.scrollBy({ top: stage.clientHeight * 0.85, behavior: "auto" });
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
            if (state && !state.paused && !state.shell.querySelector(".rf-bar:hover")) {
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
    on(state.shell, "pointermove", onSliderPointer);
    on(state.shell, "pointerdown", onSliderPointer);
    on(state.shell, "pointerleave", hidePeek);
    // While focus is in the top line or slider they stay; when focus leaves, the usual fade resumes.
    on(state.shell, "focusin", event => {
        if (event.target?.closest?.(".rf-bar")) {
            showChrome(true);
        }
    });
    on(state.shell, "focusout", event => {
        if (event.target?.closest?.(".rf-bar") && !event.relatedTarget?.closest?.(".rf-bar")) {
            showChrome();
        }
    });
    on(document, "pointerup", hidePeek);
    on(document, "pointercancel", hidePeek);
    on(window, "resize", onResize);
    on(window, "orientationchange", onResize);
}

// ---- Page pictures for the slider preview and the Pages panel -----------------------------------------------------

const THUMB_WIDTH = 160;

function thumbWidth() {
    return state.widths.length > 0 ? state.widths[0] : THUMB_WIDTH;
}

// A small picture of page `number` in `holder`.
function drawThumb(holder, number) {
    return state.source.draw(holder, number, thumbWidth());
}

// The slider preview: while the pointer is over the slider (or dragging it), a small picture of the page under it shows above it.
function onSliderPointer(event) {
    if (!state) {
        return;
    }

    const slider = event.target.closest ? event.target.closest(".pr-slider") : null;
    if (!slider || (event.pointerType !== "mouse" && event.type === "pointermove" && event.buttons === 0)) {
        if (!slider) {
            hidePeek();
        }
        return;
    }

    const rect = slider.getBoundingClientRect();
    if (rect.width <= 0 || state.pages.length === 0) {
        return;
    }

    let fraction = clamp((event.clientX - rect.left) / rect.width, 0, 1);
    if (effectiveRtl()) {
        fraction = 1 - fraction;
    }

    const number = clamp(Math.round(1 + fraction * (state.pages.length - 1)), 1, state.pages.length);
    let peek = state.peek;
    if (!peek) {
        peek = document.createElement("div");
        peek.className = "pr-peek";
        peek.setAttribute("aria-hidden", "true");
        peek.innerHTML = '<div class="pr-peek-pic pr-page--loading"></div><span class="pr-peek-label"></span>';
        slider.appendChild(peek);
        state.peek = peek;
    }

    if (peek.parentElement !== slider) {
        slider.appendChild(peek);
    }

    const half = 45;
    peek.style.left = `${clamp(event.clientX - rect.left, half, Math.max(half, rect.width - half))}px`;
    peek.hidden = false;
    peek.querySelector(".pr-peek-label").textContent = `Page ${number}`;
    if (peek.dataset.page !== String(number)) {
        peek.dataset.page = String(number);
        const pic = peek.querySelector(".pr-peek-pic");
        pic.classList.add("pr-page--loading");
        pic.replaceChildren();
        const wanted = number;
        clearTimeout(state.peekTimer);
        // Wait a beat so a fast drag does not ask for every page it passes.
        state.peekTimer = setTimeout(() => {
            if (state && peek.dataset.page === String(wanted)) {
                drawThumb(pic, wanted);
            }
        }, 60);
    }
}

function hidePeek() {
    if (state?.peek) {
        state.peek.hidden = true;
        clearTimeout(state.peekTimer);
    }
}

/**
 * The Pages panel's grid. The component gives an empty element; the grid is built here so a long comic opens at once,
 * and a picture is only asked for when its cell scrolls into view. `marked` is the pages that carry a bookmark mark.
 */
export function mountPageGrid(host, marked) {
    if (!state) {
        return;
    }

    unmountPageGrid();
    const markedSet = new Set(marked || []);
    const grid = document.createElement("div");
    grid.className = "pr-grid";
    const observer = new IntersectionObserver(entries => {
        for (const entry of entries) {
            if (entry.isIntersecting) {
                observer.unobserve(entry.target);
                drawThumb(entry.target.querySelector(".pr-th-pic"), Number(entry.target.dataset.page));
            }
        }
    }, { root: host.closest(".rf-panel-body") || null, rootMargin: "200px" });

    const first = state.current;
    for (const page of state.pages) {
        const cell = document.createElement("button");
        cell.type = "button";
        cell.className = "pr-th";
        cell.dataset.page = String(page.number);
        cell.setAttribute("aria-label", markedSet.has(page.number) ? `Page ${page.number}, bookmarked` : `Page ${page.number}`);
        if (page.number === first) {
            cell.classList.add("pr-th--cur");
            cell.setAttribute("aria-current", "true");
        }

        const pic = document.createElement("div");
        pic.className = "pr-th-pic pr-page--loading";
        const ratio = page.width > 0 && page.height > 0 ? page.width / page.height : 0.66;
        pic.style.aspectRatio = String(ratio);
        cell.appendChild(pic);
        const label = document.createElement("span");
        label.className = "pr-th-num";
        label.textContent = String(page.number);
        cell.appendChild(label);
        if (markedSet.has(page.number)) {
            const mark = document.createElement("i");
            mark.className = "pr-th-bm";
            cell.appendChild(mark);
        }

        cell.addEventListener("click", () => state?.dotnet.invokeMethodAsync("OnGridPick", page.number));
        grid.appendChild(cell);
        observer.observe(cell);
    }

    host.replaceChildren(grid);
    state.grid = { host, observer };
    const current = grid.querySelector(".pr-th--cur");
    current?.scrollIntoView({ block: "center" });
}

export function unmountPageGrid() {
    if (state?.grid) {
        state.grid.observer.disconnect();
        state.grid.host.replaceChildren();
        state.grid = null;
    }
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
    if (!state || event.defaultPrevented) {
        return;
    }

    // Esc closes the innermost thing first (a panel or the end card), even while a panel has the page keys paused.
    // (Tizen and webOS remotes send Back as a key code with no useful name.)
    if (keyAction(event.key, false, false) === "close" || event.keyCode === 461 || event.keyCode === 10009) {
        state.dotnet.invokeMethodAsync("OnEscape");
        event.preventDefault();
        return;
    }

    if (state.paused) {
        return;
    }

    const target = event.target;
    // Inside the top line or slider the arrow keys move between the tools instead of turning the page (TV remote).
    const bar = target?.closest ? target.closest(".rf-bar") : null;
    if (bar) {
        const where = bar.classList.contains("rf-bar--bottom") ? "slider" : "top";
        if (moveInBar(barKeyAction(event.key, where), bar)) {
            event.preventDefault();
        }
        return;
    }

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
        case "menu":
            // OK on a button is that button's own press; anywhere else it brings up the top line and moves into it.
            if (target?.closest && target.closest("button, a, [role=button]")) {
                return;
            }

            showChrome(true);
            focusFirstTool();
            break;
        default:
            showChrome();
            return;
    }

    event.preventDefault();
}

function barTools(bar) {
    return [...bar.querySelectorAll("button, input[type=range], a[href]")].filter(element => !element.disabled && element.offsetParent !== null);
}

function focusFirstTool() {
    const tools = barTools(state.shell.querySelector(".rf-bar--top"));
    // The title is not a control, so the first one on the line is the reader's first tool (Search, Contents, Pages or Bookmark).
    tools[0]?.focus();
}

function moveInBar(action, bar) {
    if (!action) {
        return false;
    }

    if (action === "to-slider") {
        state.shell.querySelector(".rf-bar--bottom input[type=range]")?.focus();
        return true;
    }

    if (action === "to-tools") {
        focusFirstTool();
        return true;
    }

    const tools = barTools(bar);
    const at = tools.indexOf(document.activeElement);
    const to = action === "next-tool" ? Math.min(tools.length - 1, at + 1) : Math.max(0, at - 1);
    tools[to]?.focus();
    return true;
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
        const img = holder.querySelector("img, canvas");
        const number = Number(holder.dataset.page);
        const page = state.pages[number - 1];
        if (!img || !page) {
            continue;
        }

        const wanted = state.zoom.z > 1.5 ? chooseWidth(neededWidth(page, pagesInView.length, state.zoom.z)) : chooseWidth(neededWidth(page, pagesInView.length, 1));
        const wantedUrl = state.source.pictureUrl(number, wanted);
        if (state.source.sharpen) {
            if (img.dataset.zoomUrl !== wantedUrl && state.zoom.z > 1) {
                img.dataset.zoomUrl = wantedUrl;
                state.source.sharpen(holder, number, wanted).then(() => {
                    const replaced = holder.querySelector("canvas");
                    if (replaced) {
                        replaced.dataset.zoomUrl = wantedUrl;
                    }
                });
            }
            continue;
        }

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

/** The small page pictures in the Bookmarks panel; a row already drawn for its page is left alone. */
export function drawBookmarkPictures() {
    if (!state) {
        return;
    }

    for (const pic of document.querySelectorAll(".pr-bm-pic[data-page]")) {
        const number = Number(pic.dataset.page);
        if (pic.dataset.drawn === String(number)) {
            continue;
        }

        pic.dataset.drawn = String(number);
        pic.classList.add("pr-page--loading");
        pic.replaceChildren();
        drawThumb(pic, number);
    }
}

/** The bookmark marks on the slider: the component says where each goes (a percentage); the script sets the left edge. */
export function placeMarks() {
    for (const mark of document.querySelectorAll(".pr-mark[data-left]")) {
        mark.style.left = mark.dataset.left;
    }
}
