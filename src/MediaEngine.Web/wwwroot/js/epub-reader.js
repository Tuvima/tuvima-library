// ============================================================================
// EPUB Reader — Tuvima Library
// Client-side pagination engine using CSS multi-column layout.
// Communicates with Blazor via DotNetObjectReference.
// ============================================================================

window.epubReader = (function () {
    'use strict';

    let _container = null;
    let _dotNetRef = null;
    let _currentPage = 0;
    let _totalPages = 1;
    let _pageUnit = 0;   // clientWidth + columnGap — the correct translateX step per page
    let _touchStartX = 0;
    let _touchStartY = 0;
    let _chromeTimeout = null;
    let _keyHandler = null;
    let _resizeObserver = null;
    let _textOffset = 0;
    let _touchElement = null;

    function textNodes() {
        const nodes = [];
        if (!_container) return nodes;
        const walker = document.createTreeWalker(_container, NodeFilter.SHOW_TEXT, {
            acceptNode: node => node.parentElement?.closest('script,style') ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT
        });
        while (walker.nextNode()) nodes.push(walker.currentNode);
        return nodes;
    }

    function offsetColumn(nodes, offset) {
        for (const node of nodes) {
            if (offset >= node.length) { offset -= node.length; continue; }
            const range = document.createRange();
            // Collapsed whitespace has a zero rectangle at the viewport origin.
            // It cannot anchor a page: advance to the next rendered character.
            for (let character = offset; character < node.length; character++) {
                range.setStart(node, character);
                range.setEnd(node, character + 1);
                const rect = range.getBoundingClientRect();
                if (rect.width > 0 && rect.height > 0)
                    return Math.max(0, Math.floor((rect.left - _container.getBoundingClientRect().left + 1) / _pageUnit));
            }
            offset = 0;
        }
        return _totalPages - 1;
    }

    function calculateTextOffset() {
        if (!_container || !_pageUnit) return 0;
        const nodes = textNodes();
        let low = 0, high = nodes.reduce((total, node) => total + node.length, 0);
        while (low < high) {
            const mid = Math.floor((low + high) / 2);
            if (offsetColumn(nodes, mid) < _currentPage) low = mid + 1;
            else high = mid;
        }
        return low;
    }

    function getTextOffset() {
        return _textOffset;
    }

    function restoreTextOffset(offset) {
        if (!_container) return 0;
        recalculate();
        goToPage(offsetColumn(textNodes(), Math.max(0, offset)));
        // Reflow may put the anchor midway down a page. Keep that exact anchor
        // until the user turns a page, so repeated size changes do not drift back.
        _textOffset = Math.max(0, offset);
        return _currentPage;
    }

    // ── Pagination ──────────────────────────────────────────────────────

    async function initPagination(containerId) {
        _container = document.getElementById(containerId);
        if (!_container) return 0;
        await document.fonts.ready;
        await new Promise(resolve => requestAnimationFrame(resolve));
        recalculate();

        // Observe container resize to recalculate pages
        if (_resizeObserver) _resizeObserver.disconnect();
        _resizeObserver = new ResizeObserver(() => {
            restoreTextOffset(_textOffset);
            notifyBlazor();
        });
        _resizeObserver.observe(_container);

        return _totalPages;
    }

    function recalculate() {
        if (!_container) return;
        const colWidth = _container.clientWidth;
        if (colWidth <= 0) {
            _totalPages = 1;
            _pageUnit = colWidth;
            return;
        }

        // CSS multi-column lays out columns separated by column-gap.
        // The correct step per page is (colWidth + gap), not colWidth alone.
        // Without accounting for the gap, every goToPage() lands 'gap' pixels
        // off-column, and the phantom page count includes ghost empty columns.
        //
        // Layout formula (CSS multi-column with N columns):
        //   scrollWidth = N * (colWidth + gap) - gap
        //   => N = (scrollWidth + gap) / (colWidth + gap)   [always an exact integer]
        const gapStr = getComputedStyle(_container).columnGap;
        const gap = parseFloat(gapStr);               // NaN for 'normal' (no explicit gap)
        const effectiveGap = isNaN(gap) ? 0 : gap;
        _pageUnit = colWidth + effectiveGap;

        const pages = Math.round((_container.scrollWidth + effectiveGap) / _pageUnit);
        _totalPages = Math.max(1, pages);
    }

    function getTotalPages() {
        if (!_container) return 1;
        recalculate();
        return _totalPages;
    }

    function goToPage(pageIndex) {
        if (!_container) return;
        recalculate();
        pageIndex = Math.max(0, Math.min(pageIndex, _totalPages - 1));
        _currentPage = pageIndex;
        // Use _pageUnit (colWidth + columnGap) so each step lands on a column boundary.
        _container.style.transform = 'translateX(-' + (pageIndex * _pageUnit) + 'px)';
        window.getSelection()?.removeAllRanges();
        _textOffset = calculateTextOffset();
    }

    function getCurrentPage() {
        return _currentPage;
    }

    function nextPage() {
        if (_currentPage < _totalPages - 1) {
            goToPage(_currentPage + 1);
            notifyBlazor();
            return true;
        }
        return false;
    }

    function previousPage() {
        if (_currentPage > 0) {
            goToPage(_currentPage - 1);
            notifyBlazor();
            return true;
        }
        return false;
    }

    // ── Touch gestures ──────────────────────────────────────────────────

    function registerTouchGestures(containerId, dotNetRef) {
        _dotNetRef = dotNetRef;
        const el = document.getElementById(containerId) || document.querySelector('.reader-content-viewport');
        if (!el) return;
        if (_touchElement) {
            _touchElement.removeEventListener('touchstart', onTouchStart);
            _touchElement.removeEventListener('touchend', onTouchEnd);
        }
        _touchElement = el;
        el.addEventListener('touchstart', onTouchStart, { passive: true });
        el.addEventListener('touchend', onTouchEnd, { passive: true });
    }

    function onTouchStart(e) {
        if (e.touches.length !== 1) return;
        _touchStartX = e.touches[0].clientX;
        _touchStartY = e.touches[0].clientY;
    }

    function onTouchEnd(e) {
        if (e.changedTouches.length !== 1) return;
        const dx = e.changedTouches[0].clientX - _touchStartX;
        const dy = e.changedTouches[0].clientY - _touchStartY;

        // Only horizontal swipes (ignore vertical scroll)
        if (Math.abs(dx) < 50 || Math.abs(dy) > Math.abs(dx)) return;

        if (dx < -50) {
            // Swipe left → next page
            if (!nextPage() && _dotNetRef) {
                _dotNetRef.invokeMethodAsync('OnNextChapter');
            }
        } else if (dx > 50) {
            // Swipe right → previous page
            if (!previousPage() && _dotNetRef) {
                _dotNetRef.invokeMethodAsync('OnPreviousChapter');
            }
        }
    }

    // ── Tap zones ───────────────────────────────────────────────────────

    function handleTapZone(zone) {
        switch (zone) {
            case 'left':
                if (!previousPage() && _dotNetRef) {
                    _dotNetRef.invokeMethodAsync('OnPreviousChapter');
                }
                break;
            case 'right':
                if (!nextPage() && _dotNetRef) {
                    _dotNetRef.invokeMethodAsync('OnNextChapter');
                }
                break;
            case 'center':
                if (_dotNetRef) {
                    _dotNetRef.invokeMethodAsync('OnToggleChrome');
                }
                break;
        }
    }

    // ── Keyboard navigation ─────────────────────────────────────────────

    function registerKeyboard(dotNetRef) {
        _dotNetRef = dotNetRef;

        if (_keyHandler) {
            document.removeEventListener('keydown', _keyHandler);
        }

        _keyHandler = function (e) {
            // Don't capture when typing in search input
            if (e.target.tagName === 'INPUT' || e.target.tagName === 'TEXTAREA') return;

            switch (e.key) {
                case 'ArrowRight':
                    e.preventDefault();
                    if (!nextPage() && _dotNetRef) {
                        _dotNetRef.invokeMethodAsync('OnNextChapter');
                    }
                    break;
                case 'ArrowLeft':
                    e.preventDefault();
                    if (!previousPage() && _dotNetRef) {
                        _dotNetRef.invokeMethodAsync('OnPreviousChapter');
                    }
                    break;
                case 'Escape':
                    e.preventDefault();
                    if (_dotNetRef) _dotNetRef.invokeMethodAsync('OnEscape');
                    break;
                case 'f':
                case 'F':
                    if (!e.ctrlKey && !e.metaKey) {
                        e.preventDefault();
                        toggleFullscreen();
                    } else {
                        // Ctrl+F → open search
                        e.preventDefault();
                        if (_dotNetRef) _dotNetRef.invokeMethodAsync('OnOpenSearch');
                    }
                    break;
                case 't':
                case 'T':
                    if (!e.ctrlKey && !e.metaKey) {
                        e.preventDefault();
                        if (_dotNetRef) _dotNetRef.invokeMethodAsync('OnToggleToc');
                    }
                    break;
                case 's':
                case 'S':
                    if (!e.ctrlKey && !e.metaKey) {
                        e.preventDefault();
                        if (_dotNetRef) _dotNetRef.invokeMethodAsync('OnOpenSearch');
                    }
                    break;
                case 'b':
                case 'B':
                    if (!e.ctrlKey && !e.metaKey) {
                        e.preventDefault();
                        if (_dotNetRef) _dotNetRef.invokeMethodAsync('OnToggleBookmarks');
                    }
                    break;
                case '+':
                case '=':
                    e.preventDefault();
                    if (_dotNetRef) _dotNetRef.invokeMethodAsync('OnFontSizeUp');
                    break;
                case '-':
                case '_':
                    e.preventDefault();
                    if (_dotNetRef) _dotNetRef.invokeMethodAsync('OnFontSizeDown');
                    break;
            }
        };

        document.addEventListener('keydown', _keyHandler);
    }

    // ── Fullscreen ──────────────────────────────────────────────────────

    function toggleFullscreen() {
        try {
            if (!document.fullscreenElement) {
                document.documentElement.requestFullscreen().catch(function() {});
            } else {
                document.exitFullscreen().catch(function() {});
            }
        } catch (ex) {
            // WebView environments may not support fullscreen
        }
    }

    function enterFullscreen() {
        try {
            if (!document.fullscreenElement) {
                document.documentElement.requestFullscreen().catch(function() {});
            }
        } catch (ex) { }
    }

    function exitFullscreen() {
        try {
            if (document.fullscreenElement) {
                document.exitFullscreen().catch(function() {});
            }
        } catch (ex) { }
    }

    // ── Text selection (for highlights & dictionary) ────────────────────

    function getSelection() {
        const sel = window.getSelection();
        if (!sel || sel.isCollapsed || !sel.rangeCount) return null;

        const range = sel.getRangeAt(0);
        const text = sel.toString().trim();
        if (!text) return null;

        const rect = range.getBoundingClientRect();

        return {
            text: text,
            startOffset: range.startOffset,
            endOffset: range.endOffset,
            x: rect.left + rect.width / 2,
            y: rect.top,
            width: rect.width,
            height: rect.height
        };
    }

    function clearSelection() {
        const sel = window.getSelection();
        if (sel) sel.removeAllRanges();
    }

    // ── Chrome auto-hide ────────────────────────────────────────────────

    function startChromeTimer(dotNetRef, delayMs) {
        _dotNetRef = dotNetRef;
        clearChromeTimer();
        _chromeTimeout = setTimeout(function () {
            if (_dotNetRef) _dotNetRef.invokeMethodAsync('OnHideChrome');
        }, delayMs || 3000);
    }

    function clearChromeTimer() {
        if (_chromeTimeout) {
            clearTimeout(_chromeTimeout);
            _chromeTimeout = null;
        }
    }

    // ── Scroll to position (for resuming) ───────────────────────────────

    function scrollToPosition(containerId, pageIndex) {
        _container = document.getElementById(containerId);
        if (!_container) return;
        recalculate();
        goToPage(pageIndex);  // goToPage uses _pageUnit after recalculate()
    }

    // ── Notify Blazor of page changes ───────────────────────────────────

    function notifyBlazor() {
        if (_dotNetRef) {
            _dotNetRef.invokeMethodAsync('OnPageChanged', _currentPage, _totalPages);
        }
    }

    // ── Set CSS custom properties ───────────────────────────────────────

    function setReaderStyle(property, value) {
        document.documentElement.style.setProperty(property, value);
    }

    // ── Cleanup ─────────────────────────────────────────────────────────

    function dispose() {
        if (_touchElement) {
            _touchElement.removeEventListener('touchstart', onTouchStart);
            _touchElement.removeEventListener('touchend', onTouchEnd);
            _touchElement = null;
        }
        if (_keyHandler) {
            document.removeEventListener('keydown', _keyHandler);
            _keyHandler = null;
        }
        if (_resizeObserver) {
            _resizeObserver.disconnect();
            _resizeObserver = null;
        }
        clearChromeTimer();
        exitFullscreen();
        _container = null;
        _dotNetRef = null;
        _currentPage = 0;
        _totalPages = 1;
        _pageUnit = 0;
    }

    // ── Public API ──────────────────────────────────────────────────────

    return {
        initPagination: initPagination,
        getTotalPages: getTotalPages,
        goToPage: goToPage,
        getCurrentPage: getCurrentPage,
        getTextOffset: getTextOffset,
        restoreTextOffset: restoreTextOffset,
        nextPage: nextPage,
        previousPage: previousPage,
        registerTouchGestures: registerTouchGestures,
        registerKeyboard: registerKeyboard,
        handleTapZone: handleTapZone,
        toggleFullscreen: toggleFullscreen,
        enterFullscreen: enterFullscreen,
        exitFullscreen: exitFullscreen,
        getSelection: getSelection,
        clearSelection: clearSelection,
        startChromeTimer: startChromeTimer,
        clearChromeTimer: clearChromeTimer,
        scrollToPosition: scrollToPosition,
        setReaderStyle: setReaderStyle,
        dispose: dispose
    };
})();
