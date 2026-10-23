import test from 'node:test';
import assert from 'node:assert/strict';

const { uploadFile, savePending, findPending, removePending, cancelUpload, choose, begin, cancel, forget } = await import(new URL('../../../src/MediaEngine.Web/wwwroot/js/import-upload.js', import.meta.url).href);

// A stand-in file: only its name, size and slice are used.
function fakeFile(name, size) {
    return { name, size, slice: (start, end) => ({ start, end, size: end - start }) };
}

const json = (status, body) => ({ ok: status >= 200 && status < 300, status, json: async () => body });

// A stand-in for the Dashboard relay and the Engine behind it: it keeps the pieces it holds and records every call.
function fakeRelay({ pieceBytes = 10, refuseStart = null, failures = [] } = {}) {
    const state = { received: new Set(), calls: [], puts: [], upload: null, starts: 0 };
    const fetchFn = async (url, init = {}) => {
        const method = init.method ?? 'GET';
        state.calls.push(`${method} ${url}`);
        if (method === 'POST' && url === '/import-uploads/') {
            state.starts++;
            if (refuseStart) return json(refuseStart.status, refuseStart.body);
            const body = JSON.parse(init.body);
            state.upload = { id: 'u1', total: body.total_bytes, body };
            return json(200, { upload_id: 'u1', piece_bytes: pieceBytes, piece_count: Math.ceil(body.total_bytes / pieceBytes), total_bytes: body.total_bytes });
        }

        if (method === 'GET') {
            return json(200, { upload_id: 'u1', state: 'open', file_name: state.upload?.body.file_name ?? 'a.mkv', total_bytes: state.upload?.total ?? 0, piece_bytes: pieceBytes, piece_count: Math.ceil((state.upload?.total ?? 0) / pieceBytes), received_pieces: [...state.received].sort((a, b) => a - b) });
        }

        const piece = /\/pieces\/(\d+)$/.exec(url);
        if (method === 'PUT' && piece) {
            const index = Number(piece[1]);
            state.puts.push(index);
            const failure = failures.find(item => item.index === index && !item.done);
            if (failure) {
                failure.done = true;
                // The piece arrived but the answer was lost, or it never arrived.
                if (failure.arrives) state.received.add(index);
                throw new TypeError('network down');
            }

            state.received.add(index);
            return json(200, {});
        }

        if (method === 'POST' && url.endsWith('/complete')) {
            return json(200, { upload_id: 'u1', file_name: state.upload.body.file_name, renamed: false, destination_path: '/import/Movies/a.mkv' });
        }

        throw new Error(`unexpected ${method} ${url}`);
    };

    return { state, fetchFn };
}

const quiet = { sleep: async () => {} };
const warnings = [];
console.warn = (...args) => warnings.push(args.map(String).join(' '));

test('a file is started, sent in pieces by index and completed', async () => {
    const relay = fakeRelay();
    const progress = [];

    const outcome = await uploadFile(fakeFile('a.mkv', 25), { ...quiet, fetchFn: relay.fetchFn, token: 'T', kindKey: 'movies', onProgress: (sent, total) => progress.push([sent, total]) });

    assert.equal(outcome.state, 'completed');
    assert.equal(outcome.fileName, 'a.mkv');
    assert.deepEqual([...relay.state.puts].sort(), [0, 1, 2]);
    assert.deepEqual(relay.state.upload.body, { kind_key: 'movies', file_name: 'a.mkv', total_bytes: 25 });
    assert.deepEqual(progress.at(-1), [25, 25]);
});

test('every call carries the antiforgery token and only the start names the kind', async () => {
    const seen = [];
    const relay = fakeRelay();
    const fetchFn = (url, init) => { seen.push(init.headers?.RequestVerificationToken); return relay.fetchFn(url, init); };

    await uploadFile(fakeFile('a.mkv', 10), { ...quiet, fetchFn, token: 'T', kindKey: 'movies', libraryId: 'lib-1' });

    assert.ok(seen.length > 3);
    assert.ok(seen.every(token => token === 'T'));
    assert.equal(relay.state.upload.body.library_id, 'lib-1');
});

test('pieces go two at a time', async () => {
    const relay = fakeRelay();
    let running = 0;
    let peak = 0;
    const fetchFn = async (url, init) => {
        if (init.method === 'PUT') {
            running++;
            peak = Math.max(peak, running);
            await new Promise(resolve => setTimeout(resolve, 5));
            running--;
        }

        return relay.fetchFn(url, init);
    };

    await uploadFile(fakeFile('a.mkv', 60), { ...quiet, fetchFn, kindKey: 'movies' });

    assert.equal(peak, 2);
});

test('a refused start sends nothing and says why', async () => {
    const relay = fakeRelay({ refuseStart: { status: 409, body: { code: 'no_import_folder', detail: 'Set an Import folder first.' } } });

    const outcome = await uploadFile(fakeFile('a.mkv', 25), { ...quiet, fetchFn: relay.fetchFn, kindKey: 'movies' });

    assert.deepEqual(outcome, { state: 'refused', code: 'no_import_folder', message: 'Set an Import folder first.', status: 409, importFolderPath: null });
    assert.deepEqual(relay.state.puts, []);
    assert.equal(relay.state.calls.length, 1);
});

test('a dropped connection resumes from the missing pieces and never resends a piece the Engine holds', async () => {
    // Piece 1 reaches the Engine but the answer is lost; piece 2 never reaches it.
    const relay = fakeRelay({ failures: [{ index: 1, arrives: true }, { index: 2, arrives: false }] });

    const outcome = await uploadFile(fakeFile('a.mkv', 40), { ...quiet, fetchFn: relay.fetchFn, kindKey: 'movies', concurrency: 1 });

    assert.equal(outcome.state, 'completed');
    const counts = relay.state.puts.reduce((map, index) => map.set(index, (map.get(index) ?? 0) + 1), new Map());
    assert.equal(counts.get(0), 1);
    assert.equal(counts.get(1), 1, 'piece 1 arrived, so it is not sent again');
    assert.equal(counts.get(2), 2, 'piece 2 never arrived, so it is sent again');
    assert.equal(counts.get(3), 1);
    assert.ok(relay.state.calls.some(call => call.startsWith('GET ')), 'the Engine was asked what it holds');
});

test('an upload that keeps failing gives up with a message and stays resumable', async () => {
    const relay = fakeRelay();
    const fetchFn = async (url, init) => { if (init.method === 'PUT') throw new TypeError('offline'); return relay.fetchFn(url, init); };

    const outcome = await uploadFile(fakeFile('a.mkv', 20), { ...quiet, fetchFn, kindKey: 'movies', retries: 2 });

    assert.equal(outcome.state, 'failed');
    assert.equal(outcome.uploadId, 'u1');
    assert.match(outcome.message, /Choose the file again/);
});

test('a remembered upload is resumed: only the missing pieces are sent and no new upload is started', async () => {
    const relay = fakeRelay();
    relay.state.upload = { id: 'u1', total: 30, body: { file_name: 'a.mkv', total_bytes: 30 } };
    relay.state.received.add(0).add(1);

    const outcome = await uploadFile(fakeFile('a.mkv', 30), { ...quiet, fetchFn: relay.fetchFn, kindKey: 'movies', resumeUploadId: 'u1' });

    assert.equal(outcome.state, 'completed');
    assert.equal(relay.state.starts, 0);
    assert.deepEqual(relay.state.puts, [2]);
});

test('a remembered upload the Engine no longer has starts again from the beginning', async () => {
    const relay = fakeRelay();
    const fetchFn = (url, init) => (init.method === 'GET' && !relay.state.upload ? Promise.resolve(json(404, { code: 'upload_not_found' })) : relay.fetchFn(url, init));

    const outcome = await uploadFile(fakeFile('a.mkv', 20), { ...quiet, fetchFn, kindKey: 'movies', resumeUploadId: 'gone' });

    assert.equal(outcome.state, 'completed');
    assert.equal(relay.state.starts, 1);
    assert.deepEqual([...relay.state.puts].sort(), [0, 1]);
});

test('cancelling stops the upload without a failure', async () => {
    const relay = fakeRelay();
    const controller = new AbortController();
    const fetchFn = async (url, init) => {
        if (init.method === 'PUT') controller.abort();
        return relay.fetchFn(url, init);
    };

    const outcome = await uploadFile(fakeFile('a.mkv', 50), { ...quiet, fetchFn, kindKey: 'movies', signal: controller.signal, concurrency: 1 });

    assert.equal(outcome.state, 'cancelled');
});

test('cancelling an upload asks the relay to delete it', async () => {
    const calls = [];

    const ok = await cancelUpload('u1', { token: 'T', fetchFn: async (url, init) => { calls.push([init.method, url, init.headers.RequestVerificationToken]); return { ok: true, status: 200 }; } });

    assert.equal(ok, true);
    assert.deepEqual(calls, [['DELETE', '/import-uploads/u1', 'T']]);
});

test('unfinished uploads are remembered by name and size, and a storage that fails is logged, not fatal', () => {
    const data = new Map();
    const storage = { getItem: key => data.get(key) ?? null, setItem: (key, value) => data.set(key, value) };

    savePending(storage, { uploadId: 'u1', name: 'a.mkv', size: 30, kindKey: 'movies', libraryId: null });
    savePending(storage, { uploadId: 'u2', name: 'b.mkv', size: 40, kindKey: 'movies', libraryId: null });

    assert.equal(findPending(storage, 'a.mkv', 30).uploadId, 'u1');
    assert.equal(findPending(storage, 'a.mkv', 31), null);
    removePending(storage, 'u1');
    assert.equal(findPending(storage, 'a.mkv', 30), null);

    warnings.length = 0;
    const broken = { getItem: () => { throw new Error('blocked'); }, setItem: () => { throw new Error('blocked'); } };
    assert.equal(findPending(broken, 'a.mkv', 30), null);
    savePending(broken, { uploadId: 'u3', name: 'c', size: 1 });
    assert.ok(warnings.length >= 2);
});

test('a new start for the same name and size replaces the remembered one', () => {
    const data = new Map();
    const storage = { getItem: key => data.get(key) ?? null, setItem: (key, value) => data.set(key, value) };

    savePending(storage, { uploadId: 'old', name: 'Lecture.mp4', size: 30, kindKey: 'other_videos', libraryId: 'lib-1' });
    savePending(storage, { uploadId: 'new', name: 'Lecture.mp4', size: 30, kindKey: 'movies', libraryId: null });

    assert.equal(JSON.parse(data.get('tuvima.import-uploads.pending')).length, 1);
    assert.equal(findPending(storage, 'Lecture.mp4', 30).uploadId, 'new');
});

test('a remembered upload the Engine says is gone is forgotten, not offered again', async () => {
    const relay = fakeRelay();
    const stale = [];
    const fetchFn = (url, init) => (init.method === 'GET' && !relay.state.upload ? Promise.resolve(json(404, { code: 'upload_not_found' })) : relay.fetchFn(url, init));

    await uploadFile(fakeFile('a.mkv', 20), { ...quiet, fetchFn, kindKey: 'movies', resumeUploadId: 'gone', onStale: id => stale.push(id) });

    assert.deepEqual(stale, ['gone']);
});

test('a remembered upload that is closed or for another file is forgotten', async () => {
    const relay = fakeRelay();
    relay.state.upload = { id: 'u1', total: 99, body: { file_name: 'a.mkv', total_bytes: 99 } };
    const stale = [];

    await uploadFile(fakeFile('a.mkv', 20), { ...quiet, fetchFn: relay.fetchFn, kindKey: 'movies', resumeUploadId: 'u1', onStale: id => stale.push(id) });

    assert.deepEqual(stale, ['u1']);
});

test('a file above the warning size is refused with the folder path until the person confirms, then goes ahead', async () => {
    const asked = [];
    const fetchFn = async (url, init) => {
        if (init.method === 'POST' && url === '/import-uploads/') {
            const body = JSON.parse(init.body);
            asked.push(body.confirm_large === true);
            if (!body.confirm_large) return json(409, { code: 'large_upload_needs_confirm', detail: 'Large uploads can fail.', import_folder_path: '/import/Movies' });
        }

        return fakeRelay().fetchFn(url, init);
    };

    const refused = await uploadFile(fakeFile('big.mkv', 20), { ...quiet, fetchFn, kindKey: 'movies' });
    assert.equal(refused.state, 'refused');
    assert.equal(refused.code, 'large_upload_needs_confirm');
    assert.equal(refused.importFolderPath, '/import/Movies');

    const relay = fakeRelay();
    const confirmedBodies = [];
    const confirmedFetch = (url, init) => { if (init.method === 'POST' && url === '/import-uploads/') confirmedBodies.push(JSON.parse(init.body)); return relay.fetchFn(url, init); };
    const confirmed = await uploadFile(fakeFile('big.mkv', 20), { ...quiet, fetchFn: confirmedFetch, kindKey: 'movies', confirmLarge: true });
    assert.equal(confirmed.state, 'completed');
    assert.equal(confirmedBodies[0].confirm_large, true);
    assert.deepEqual(asked, [false]);
});

// The page side, with a stand-in browser: a chooser whose files the test supplies, local storage, and a Dashboard relay.
function fakeBrowser({ fetchFn }) {
    const data = new Map();
    const storage = { getItem: key => data.get(key) ?? null, setItem: (key, value) => data.set(key, value) };
    const input = { files: [], click() {}, addEventListener(type, handler) { this.handler = handler; } };
    globalThis.localStorage = storage;
    globalThis.document = { createElement: () => input };
    globalThis.fetch = fetchFn;
    const calls = [];
    const waiters = [];
    const dotnet = {
        invokeMethodAsync(method, ...args) {
            calls.push([method, ...args]);
            waiters.splice(0).forEach(resolve => resolve());
            return Promise.resolve();
        },
    };
    const until = async predicate => { while (!predicate()) await new Promise(resolve => { waiters.push(resolve); setTimeout(resolve, 5); }); };
    const pick = file => { choose(dotnet); input.files = [file]; input.handler(); return calls.filter(call => call[0] === 'OnFilesChosen').at(-1)[1][0]; };
    return { storage, dotnet, calls, until, pick };
}

test('a refused start forgets the remembered upload, so the same file asks the kind again instead of failing for good', async () => {
    const browser = fakeBrowser({ fetchFn: async () => json(400, { code: 'unknown_library', detail: "That library isn't one this kind of file can be added to." }) });
    savePending(browser.storage, { uploadId: 'stale', name: 'Lecture.mp4', size: 30, kindKey: 'other_videos', libraryId: 'deleted-library' });

    const item = browser.pick(fakeFile('Lecture.mp4', 30));
    assert.equal(item.resumeKind, 'other_videos');
    begin(item.id, 'other_videos', 'deleted-library', 'T', browser.dotnet);
    await browser.until(() => browser.calls.some(call => call[0] === 'OnUploadFinished'));

    assert.equal(browser.calls.find(call => call[0] === 'OnUploadFinished')[2].code, 'unknown_library');
    assert.equal(findPending(browser.storage, 'Lecture.mp4', 30), null);
    // Choosing it again no longer remembers a kind or library.
    assert.equal(browser.pick(fakeFile('Lecture.mp4', 30)).resumeKind, null);
});

test('cancelling while the start is still on its way deletes the upload once the start returns an id', async () => {
    const relay = fakeRelay();
    let releaseStart;
    const startGate = new Promise(resolve => { releaseStart = resolve; });
    const deleted = [];
    const fetchFn = async (url, init) => {
        if (init.method === 'POST' && url === '/import-uploads/') await startGate;
        if (init.method === 'DELETE') { deleted.push(url); return json(200, {}); }
        return relay.fetchFn(url, init);
    };
    const browser = fakeBrowser({ fetchFn });

    const item = browser.pick(fakeFile('film.mkv', 30));
    begin(item.id, 'movies', null, 'T', browser.dotnet);
    cancel(item.id, 'T');
    assert.deepEqual(deleted, []);
    releaseStart();
    await browser.until(() => deleted.length > 0);

    assert.deepEqual(deleted, ['/import-uploads/u1']);
    assert.deepEqual(relay.state.puts, []);
});

test('two identical files dropped together never share one remembered upload', async () => {
    const held = new Map([['remembered', new Set()]]);
    let next = 0;
    const piecesSentTo = [];
    const fetchFn = async (url, init) => {
        const piece = /import-uploads\/([^/]+)\/pieces\/(\d+)$/.exec(url);
        const status = /import-uploads\/([^/]+)$/.exec(url);
        if (init.method === 'POST' && url === '/import-uploads/') {
            const id = `fresh-${++next}`;
            held.set(id, new Set());
            return json(200, { upload_id: id, piece_bytes: 10, piece_count: 3, total_bytes: 30 });
        }

        if (init.method === 'GET' && status) {
            return json(200, { upload_id: status[1], state: 'open', file_name: 'a.mkv', total_bytes: 30, piece_bytes: 10, piece_count: 3, received_pieces: [...held.get(status[1])] });
        }

        if (init.method === 'PUT' && piece) { piecesSentTo.push(piece[1]); held.get(piece[1]).add(Number(piece[2])); return json(200, {}); }
        if (url.endsWith('/complete')) return json(200, { file_name: 'a.mkv', renamed: false, destination_path: '/x' });
        throw new Error(`unexpected ${init.method} ${url}`);
    };
    const browser = fakeBrowser({ fetchFn });
    savePending(browser.storage, { uploadId: 'remembered', name: 'a.mkv', size: 30, kindKey: 'movies', libraryId: null });

    const first = browser.pick(fakeFile('a.mkv', 30));
    const second = browser.pick(fakeFile('a.mkv', 30));
    begin(first.id, 'movies', null, 'T', browser.dotnet);
    begin(second.id, 'movies', null, 'T', browser.dotnet);
    await browser.until(() => browser.calls.filter(call => call[0] === 'OnUploadFinished').length === 2);

    const targets = new Set(piecesSentTo);
    assert.equal(targets.size, 2, 'each file has its own upload');
    assert.ok(targets.has('remembered'), 'one of them carried on the remembered upload');
    assert.ok([...targets].some(id => id.startsWith('fresh-')), 'the other started its own');
});

test('a failed upload that is dismissed resumes when the same file is chosen again in the same tab', async () => {
    const relay = fakeRelay();
    let down = true;
    const fetchFn = async (url, init) => {
        if (down && init.method === 'PUT') throw new TypeError('network down');
        return relay.fetchFn(url, init);
    };
    const browser = fakeBrowser({ fetchFn });

    const first = browser.pick(fakeFile('film.mkv', 30));
    begin(first.id, 'movies', null, 'T', browser.dotnet);
    await browser.until(() => browser.calls.some(call => call[0] === 'OnUploadFinished'));
    assert.equal(browser.calls.find(call => call[0] === 'OnUploadFinished')[2].state, 'failed');

    forget(first.id);
    down = false;
    const again = browser.pick(fakeFile('film.mkv', 30));
    begin(again.id, 'movies', null, 'T', browser.dotnet);
    await browser.until(() => browser.calls.filter(call => call[0] === 'OnUploadFinished').length === 2);

    assert.equal(browser.calls.filter(call => call[0] === 'OnUploadFinished')[1][2].state, 'completed');
    assert.equal(relay.state.starts, 1, 'the second choice carried on the first upload instead of starting another');
});
