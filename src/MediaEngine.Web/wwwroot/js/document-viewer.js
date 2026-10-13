// Document viewer for the View Documents page. PDF.js ships locally under /vendor/pdfjs (Apache-2.0), never from a CDN.
// One PDF is open at a time. The .NET side owns the toolbar; this module draws pages and reports page/find changes back.

let libs = null;
let current = null;

function vendorUrl(name) {
    return new URL(`../vendor/pdfjs/${name}`, import.meta.url).href;
}

async function load() {
    if (libs) {
        return libs;
    }

    if (!document.querySelector('link[data-pdfjs-viewer]')) {
        const link = document.createElement('link');
        link.rel = 'stylesheet';
        link.href = vendorUrl('pdf_viewer.css');
        link.dataset.pdfjsViewer = 'true';
        document.head.appendChild(link);
    }

    const lib = await import(vendorUrl('pdf.min.mjs'));
    lib.GlobalWorkerOptions.workerSrc = vendorUrl('pdf.worker.min.mjs');
    globalThis.pdfjsLib = lib;
    const viewer = await import(vendorUrl('pdf_viewer.mjs'));
    libs = { lib, viewer };
    return libs;
}

export async function open(container, viewerElement, url, term, dotnet) {
    await close();
    const { lib, viewer } = await load();
    const eventBus = new viewer.EventBus();
    const linkService = new viewer.PDFLinkService({ eventBus });
    const findController = new viewer.PDFFindController({ eventBus, linkService });
    const pdfViewer = new viewer.PDFViewer({
        container,
        viewer: viewerElement,
        eventBus,
        linkService,
        findController,
        removePageBorders: true,
    });
    linkService.setViewer(pdfViewer);
    const state = { pdfViewer, eventBus, task: null, pdf: null, dotnet, term: term || '' };
    current = state;

    eventBus.on('pagesinit', () => {
        if (current !== state) {
            return;
        }

        pdfViewer.currentScaleValue = 'page-width';
        dotnet.invokeMethodAsync('OnPdfReady', pdfViewer.pagesCount);
        dotnet.invokeMethodAsync('OnPdfZoomChanged', Math.round(pdfViewer.currentScale * 100));
        if (state.term) {
            find(state.term);
        }
    });
    eventBus.on('pagechanging', event => {
        if (current === state) {
            dotnet.invokeMethodAsync('OnPdfPageChanged', event.pageNumber);
        }
    });
    eventBus.on('scalechanging', event => {
        if (event.scale > 0) {
            dotnet.invokeMethodAsync('OnPdfZoomChanged', Math.round(event.scale * 100));
        }
    });
    eventBus.on('updatefindmatchescount', event => reportFind(state, event.matchesCount));
    eventBus.on('updatefindcontrolstate', event => reportFind(state, event.matchesCount, event.state));

    try {
        state.task = lib.getDocument({ url, isEvalSupported: false, useSystemFonts: true });
        state.pdf = await state.task.promise;
        if (current !== state) {
            return;
        }

        pdfViewer.setDocument(state.pdf);
        linkService.setDocument(state.pdf, null);
    } catch (error) {
        if (current !== state) {
            // A newer document replaced this one while it was loading.
            return;
        }

        const passwordProtected = error && error.name === 'PasswordException';
        await dotnet.invokeMethodAsync('OnPdfFailed', passwordProtected);
    }
}

function reportFind(state, counts, findState) {
    if (current !== state) {
        return;
    }

    const found = counts ? counts.current : 0;
    const total = counts ? counts.total : 0;
    // 1 means the word was not found anywhere in the document.
    state.dotnet.invokeMethodAsync('OnPdfFind', found, total, findState === 1);
}

export function find(term) {
    if (!current) {
        return;
    }

    const query = (term || '').trim();
    current.term = query;
    current.eventBus.dispatch('find', {
        source: current.pdfViewer,
        type: '',
        query,
        caseSensitive: false,
        entireWord: false,
        highlightAll: true,
        findPrevious: false,
        matchDiacritics: false,
    });
}

export function findAgain(backwards) {
    if (!current || !current.term) {
        return;
    }

    current.eventBus.dispatch('find', {
        source: current.pdfViewer,
        type: 'again',
        query: current.term,
        caseSensitive: false,
        entireWord: false,
        highlightAll: true,
        findPrevious: !!backwards,
        matchDiacritics: false,
    });
}

export function goToPage(pageNumber) {
    if (current && pageNumber >= 1 && pageNumber <= current.pdfViewer.pagesCount) {
        current.pdfViewer.currentPageNumber = pageNumber;
    }
}

export function zoomIn() {
    current?.pdfViewer.increaseScale({ steps: 1 });
}

export function zoomOut() {
    current?.pdfViewer.decreaseScale({ steps: 1 });
}

export function fitWidth() {
    if (current) {
        current.pdfViewer.currentScaleValue = 'page-width';
    }
}

export async function close() {
    const state = current;
    current = null;
    if (!state) {
        return;
    }

    try {
        state.pdfViewer.setDocument(null);
        await state.task?.destroy();
    } catch {
        // The viewer is closing; a document that cannot be released cleanly has nothing left to show.
    }
}
