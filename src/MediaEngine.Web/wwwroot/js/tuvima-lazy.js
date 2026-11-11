// tuvima-lazy.js — Tuvima Library
// On-demand loaders for the two heavy vendor files that only some pages use, so a first page load does not
// download them: hls.js (video, and audio that needs adaptive streaming) and the MapLibre stylesheet (the Places map).
// Each loader runs once and keeps its Promise; a failed load is forgotten so the next caller can try again.
// App.razor passes the fingerprinted hls address through <body data-hls-src="...">.
(function (root) {
    var fallbackHlsSrc = '/vendor/hls/hls.min.js';
    var mapLibreStylesheet = '/vendor/maplibre/maplibre-gl.css';
    var pending = {};

    function once(key, start) {
        if (!pending[key]) {
            pending[key] = start().catch(function (error) {
                delete pending[key];
                throw error;
            });
        }
        return pending[key];
    }

    function loadHls() {
        return once('hls', function () {
            if (root.Hls) return Promise.resolve(root.Hls);
            var configured = root.document.body && root.document.body.dataset && root.document.body.dataset.hlsSrc;
            var src = configured || fallbackHlsSrc;
            return new Promise(function (resolve, reject) {
                var script = root.document.createElement('script');
                script.src = src;
                script.addEventListener('load', function () {
                    if (root.Hls) resolve(root.Hls);
                    else reject(new Error('tuvimaLazy: "' + src + '" loaded but did not provide Hls'));
                });
                script.addEventListener('error', function () {
                    if (script.remove) script.remove();
                    reject(new Error('tuvimaLazy: failed to load script "' + src + '"'));
                });
                root.document.head.appendChild(script);
            });
        });
    }

    function loadStylesheet(href) {
        return once('css:' + href, function () {
            return new Promise(function (resolve, reject) {
                var link = root.document.createElement('link');
                link.rel = 'stylesheet';
                link.href = href;
                link.addEventListener('load', function () { resolve(); });
                link.addEventListener('error', function () {
                    if (link.remove) link.remove();
                    reject(new Error('tuvimaLazy: failed to load stylesheet "' + href + '"'));
                });
                // Sit before the app's own stylesheets, exactly where the stylesheet used to be linked,
                // so the app's overrides of the vendor rules keep winning.
                var first = root.document.head.querySelector('link[rel="stylesheet"]');
                if (first) root.document.head.insertBefore(link, first);
                else root.document.head.appendChild(link);
            });
        });
    }

    root.tuvimaLazy = {
        loadHls: loadHls,
        loadStylesheet: loadStylesheet,
        loadMapLibreStyles: function () { return loadStylesheet(mapLibreStylesheet); }
    };
})(typeof window !== 'undefined' ? window : globalThis);
