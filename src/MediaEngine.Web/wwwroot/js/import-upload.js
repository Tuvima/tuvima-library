// Adding files to the Import folder from the browser (Library setup spec D44, WP12b).
//
// The files never reach the Dashboard's server in one piece. This module keeps the dropped or chosen File objects, asks the Dashboard
// relay (/import-uploads) to start an upload, sends the file in the pieces the Engine asks for (two at a time, each retried a few
// times), asks the Engine which pieces it holds whenever the connection drops so nothing it already has is sent again, and finally
// completes the upload. The page (Blazor) only decides which kind a file is for and shows the progress.

const BASE = '/import-uploads';
const TOKEN_HEADER = 'RequestVerificationToken';
const PENDING_KEY = 'tuvima.import-uploads.pending';

const DEFAULT_CONCURRENCY = 2;
const DEFAULT_RETRIES = 3;
const DEFAULT_BACKOFF_MS = 500;
const PROGRESS_INTERVAL_MS = 250;

// ---- Remembering unfinished uploads (this browser only) ----------------------------------------------------------------------------

function readPending(storage) {
    try {
        const parsed = JSON.parse(storage?.getItem(PENDING_KEY) ?? '[]');
        return Array.isArray(parsed) ? parsed : [];
    } catch (error) {
        console.warn('import-upload: the list of unfinished uploads could not be read, so it is ignored', error);
        return [];
    }
}

function writePending(storage, entries) {
    try {
        storage?.setItem(PENDING_KEY, JSON.stringify(entries));
    } catch (error) {
        console.warn('import-upload: the list of unfinished uploads could not be saved, so a reload cannot offer Resume', error);
    }
}

/** Remembers an unfinished upload (by name and size, which is how a file picked again is matched). One entry per name and size: a new start replaces the old one. */
export function savePending(storage, entry) {
    const others = readPending(storage).filter(item => item.uploadId !== entry.uploadId && !(item.name === entry.name && item.size === entry.size));
    writePending(storage, [...others, entry]);
}

export function removePending(storage, uploadId) {
    writePending(storage, readPending(storage).filter(item => item.uploadId !== uploadId));
}

/** The unfinished upload of a file with this name and size, or null. */
export function findPending(storage, name, size) {
    return readPending(storage).find(item => item.name === name && item.size === size) ?? null;
}

// ---- The upload itself -------------------------------------------------------------------------------------------------------------

const defaultSleep = ms => new Promise(resolve => setTimeout(resolve, ms));

async function readProblem(response) {
    try {
        const body = await response.json();
        return { code: body?.code ?? null, message: body?.detail ?? body?.title ?? null, importFolderPath: body?.import_folder_path ?? null };
    } catch (error) {
        console.warn(`import-upload: the answer (${response.status}) was not a readable problem body`, error);
        return { code: null, message: null, importFolderPath: null };
    }
}

class UploadStopped extends Error {
    constructor(outcome) {
        super(outcome.message ?? outcome.state);
        this.outcome = outcome;
    }
}

/**
 * Sends one file. Resolves with one of:
 *   { state: 'completed', uploadId, fileName, renamed, destinationPath }
 *   { state: 'refused', code, message, status }   the start was refused; nothing was sent
 *   { state: 'cancelled' }
 *   { state: 'failed', uploadId, message }        the connection stayed down; the upload can be resumed
 *   (a refusal for a file above the warning size has code 'large_upload_needs_confirm' and importFolderPath; send again with confirmLarge)
 * options: { fetchFn, base, token, kindKey, libraryId, confirmLarge, resumeUploadId, signal, concurrency, retries, backoffMs, sleep, onStarted, onProgress, onStale }
 * onStale(uploadId) is called when the remembered upload can no longer be resumed (the Engine does not have it, or it is closed), so it can be forgotten.
 */
export async function uploadFile(file, options) {
    const fetchFn = options.fetchFn ?? ((...args) => fetch(...args));
    const base = options.base ?? BASE;
    const sleep = options.sleep ?? defaultSleep;
    const concurrency = options.concurrency ?? DEFAULT_CONCURRENCY;
    const retries = options.retries ?? DEFAULT_RETRIES;
    const backoffMs = options.backoffMs ?? DEFAULT_BACKOFF_MS;
    const signal = options.signal;
    const headers = extra => ({ ...(options.token ? { [TOKEN_HEADER]: options.token } : {}), ...extra });
    const call = (path, init) => fetchFn(`${base}${path}`, { credentials: 'same-origin', signal, ...init });
    const backoff = attempt => sleep(backoffMs * 2 ** attempt);
    const cancelled = () => signal?.aborted === true;

    try {
        let upload = options.resumeUploadId ? await tryResume(options.resumeUploadId) : null;
        if (!upload) {
            upload = await start();
        }

        options.onStarted?.(upload.uploadId, upload);
        await sendPieces(upload);
        return await complete(upload.uploadId);
    } catch (error) {
        if (error instanceof UploadStopped) {
            return error.outcome;
        }

        if (cancelled() || error?.name === 'AbortError') {
            return { state: 'cancelled' };
        }

        console.warn('import-upload: the upload stopped unexpectedly', error);
        return { state: 'failed', message: 'The upload stopped unexpectedly. Choose the file again to carry on.' };
    }

    // Asks the Engine what it still holds for an upload this browser remembered; anything unusable falls back to a fresh start.
    async function tryResume(uploadId) {
        let response;
        try {
            response = await call(`/${uploadId}`, { method: 'GET', headers: headers({}) });
        } catch (error) {
            if (cancelled()) throw error;
            console.warn('import-upload: the remembered upload could not be checked, so the file starts again', error);
            return null;
        }

        if (!response.ok) {
            // 404 (gone) or any other answer: this upload cannot be carried on, so it must not be offered again.
            options.onStale?.(uploadId);
            return null;
        }

        const status = await response.json();
        if (status.state !== 'open' || status.total_bytes !== file.size || status.file_name !== file.name) {
            options.onStale?.(uploadId);
            return null;
        }

        return toUpload(status);
    }

    async function start() {
        const body = { kind_key: options.kindKey, file_name: file.name, total_bytes: file.size };
        if (options.confirmLarge) {
            body.confirm_large = true;
        }
        if (options.libraryId) {
            body.library_id = options.libraryId;
        }

        let response;
        try {
            response = await call('/', { method: 'POST', headers: headers({ 'Content-Type': 'application/json' }), body: JSON.stringify(body) });
        } catch (error) {
            if (cancelled()) throw error;
            console.warn('import-upload: the Library could not be reached to start the upload', error);
            throw new UploadStopped({ state: 'refused', code: 'engine_unreachable', message: 'The Library could not be reached. Try again in a moment.', status: 0 });
        }

        if (!response.ok) {
            const problem = await readProblem(response);
            throw new UploadStopped({ state: 'refused', code: problem.code, message: problem.message, status: response.status, importFolderPath: problem.importFolderPath });
        }

        return toUpload(await response.json());
    }

    function toUpload(json) {
        return {
            uploadId: json.upload_id,
            pieceBytes: json.piece_bytes,
            pieceCount: json.piece_count,
            totalBytes: json.total_bytes,
            received: new Set(json.received_pieces ?? []),
        };
    }

    function pieceLength(upload, index) {
        return Math.min(upload.pieceBytes, upload.totalBytes - index * upload.pieceBytes);
    }

    function report(upload) {
        let sent = 0;
        for (const index of upload.received) sent += pieceLength(upload, index);
        options.onProgress?.(sent, upload.totalBytes);
    }

    async function readStatus(upload) {
        const response = await call(`/${upload.uploadId}`, { method: 'GET', headers: headers({}) });
        if (!response.ok) {
            const problem = await readProblem(response);
            throw new UploadStopped({ state: 'failed', uploadId: upload.uploadId, message: problem.message ?? 'The Library no longer has this upload.', status: response.status, closed: response.status === 404 || response.status === 409 });
        }

        const status = await response.json();
        upload.received = new Set(status.received_pieces ?? []);
    }

    // Sends every piece the Engine does not hold yet. When the connection drops for good the Engine is asked again what it holds,
    // so a piece that did arrive is never sent twice.
    async function sendPieces(upload) {
        let reconnects = 0;
        for (;;) {
            try {
                await readStatus(upload);
            } catch (error) {
                if (error instanceof UploadStopped || cancelled()) throw error;
                if (++reconnects > retries) throw failedConnection(upload);
                await backoff(reconnects);
                continue;
            }

            report(upload);
            const missing = [];
            for (let index = 0; index < upload.pieceCount; index++) {
                if (!upload.received.has(index)) missing.push(index);
            }

            if (missing.length === 0) {
                return;
            }

            const before = upload.received.size;
            const finished = await sendMissing(upload, missing);
            if (!finished) {
                // A round that got some pieces through is not a failure to give up on; only rounds that move nothing count.
                if (upload.received.size > before) reconnects = 0;
                if (++reconnects > retries) throw failedConnection(upload);
                await backoff(reconnects);
            }
        }
    }

    function failedConnection(upload) {
        return new UploadStopped({
            state: 'failed',
            uploadId: upload.uploadId,
            message: 'The connection to the Library kept dropping. Choose the file again to carry on where it stopped.',
        });
    }

    // Two (or the set number of) workers take the next missing piece in order. Resolves false when a piece used up its tries.
    async function sendMissing(upload, missing) {
        const queue = [...missing];
        let dropped = false;
        const worker = async () => {
            while (!dropped && !cancelled()) {
                const index = queue.shift();
                if (index === undefined) return;
                if (!await sendPiece(upload, index)) {
                    dropped = true;
                    return;
                }
            }
        };

        await Promise.all(Array.from({ length: Math.max(1, Math.min(concurrency, queue.length)) }, worker));
        if (cancelled()) throw new UploadStopped({ state: 'cancelled' });
        return !dropped;
    }

    // One try at one piece. A failed try is not repeated blindly: the caller asks the Engine what it holds first (the answer may have
    // been lost after the piece arrived), so only a piece the Engine lacks is ever sent again.
    async function sendPiece(upload, index) {
        if (cancelled()) return false;
        const start = index * upload.pieceBytes;
        try {
            const response = await call(`/${upload.uploadId}/pieces/${index}`, {
                method: 'PUT',
                headers: headers({ 'Content-Type': 'application/octet-stream' }),
                body: file.slice(start, start + pieceLength(upload, index)),
            });
            if (response.ok) {
                upload.received.add(index);
                report(upload);
                return true;
            }

            const problem = await readProblem(response);
            const gone = response.status === 404 || response.status === 409 || response.status === 401 || response.status === 403;
            if (gone) {
                throw new UploadStopped({ state: 'failed', uploadId: upload.uploadId, message: problem.message ?? 'The upload was closed.', status: response.status, closed: response.status !== 401 && response.status !== 403 });
            }

            console.warn(`import-upload: piece ${index} was answered ${response.status} (${problem.code ?? 'no code'}); it will be sent again if the Library does not hold it`);
        } catch (error) {
            if (error instanceof UploadStopped || cancelled()) throw error;
            console.warn(`import-upload: piece ${index} did not get through`, error);
        }

        return false;
    }

    async function complete(uploadId) {
        for (let attempt = 0; attempt <= retries; attempt++) {
            let response;
            try {
                response = await call(`/${uploadId}/complete`, { method: 'POST', headers: headers({}) });
            } catch (error) {
                if (cancelled()) throw error;
                console.warn(`import-upload: finishing the upload failed (attempt ${attempt + 1})`, error);
                if (attempt < retries) await backoff(attempt);
                continue;
            }

            if (response.ok) {
                const result = await response.json();
                return { state: 'completed', uploadId, fileName: result.file_name, renamed: result.renamed === true, destinationPath: result.destination_path };
            }

            const problem = await readProblem(response);
            // Finishing again is harmless, so a passing refusal (the Engine could not move the file just now) is asked again.
            if (response.status === 409 && problem.code === 'destination_unavailable' && attempt < retries) {
                await backoff(attempt);
                continue;
            }

            throw new UploadStopped({ state: 'failed', uploadId, message: problem.message ?? 'The Library could not finish saving the file.', status: response.status, closed: response.status === 404 || response.status === 409 });
        }

        throw failedConnection({ uploadId });
    }
}

/** Cancels an upload at the Engine, which deletes its partial file. */
export async function cancelUpload(uploadId, options = {}) {
    const fetchFn = options.fetchFn ?? ((...args) => fetch(...args));
    const response = await fetchFn(`${options.base ?? BASE}/${uploadId}`, {
        method: 'DELETE',
        credentials: 'same-origin',
        headers: options.token ? { [TOKEN_HEADER]: options.token } : {},
    });
    return response.ok || response.status === 404;
}

// ---- The page side: dropped files, the chooser and the running uploads --------------------------------------------------------------

const files = new Map();
const runs = new Map();
// Upload ids that a running file is using, so two identical files dropped together never share one upload.
const claimed = new Set();
const zones = new WeakMap();
let nextId = 1;

function storage() {
    try {
        return globalThis.localStorage ?? null;
    } catch (error) {
        console.warn('import-upload: this browser does not let the page keep a list of unfinished uploads', error);
        return null;
    }
}

function describe(fileList, dotnet, foldersSkipped) {
    const items = [];
    for (const file of fileList) {
        const id = `f${nextId++}`;
        files.set(id, file);
        const pending = findPending(storage(), file.name, file.size);
        items.push({
            id,
            name: file.name,
            size: file.size,
            type: file.type ?? '',
            resumeKind: pending?.kindKey ?? null,
            resumeLibrary: pending?.libraryId ?? null,
        });
    }

    if (items.length > 0 || foldersSkipped > 0) {
        dotnet.invokeMethodAsync('OnFilesChosen', items, foldersSkipped).catch(error => console.warn('import-upload: the page could not be told about the chosen files', error));
    }
}

/** Starts listening for dropped files on the zone (only when dropping is wanted: a fine pointer, not a touch screen). */
export function attach(anchor, dotnet, dropEnabled) {
    if (zones.has(anchor)) return;
    // The anchor is a hidden marker inside the page's list; the list (its parent) is what files are dropped on.
    const zone = anchor.parentElement ?? anchor;
    const controller = new AbortController();
    const options = { signal: controller.signal };
    const hasFiles = event => Array.from(event.dataTransfer?.types ?? []).includes('Files');
    if (dropEnabled) {
        zone.addEventListener('dragover', event => {
            if (!hasFiles(event)) return;
            event.preventDefault();
            event.dataTransfer.dropEffect = 'copy';
            zone.classList.add('upload-dragging');
        }, options);
        zone.addEventListener('dragleave', event => {
            if (!zone.contains(event.relatedTarget)) zone.classList.remove('upload-dragging');
        }, options);
        zone.addEventListener('drop', event => {
            if (!hasFiles(event)) return;
            event.preventDefault();
            zone.classList.remove('upload-dragging');
            const dropped = [];
            let folders = 0;
            const items = Array.from(event.dataTransfer.items ?? []);
            const list = Array.from(event.dataTransfer.files ?? []);
            list.forEach((file, position) => {
                const entry = items[position]?.webkitGetAsEntry?.();
                if (entry?.isDirectory) folders++;
                else dropped.push(file);
            });
            describe(dropped, dotnet, folders);
        }, options);
    }

    zones.set(anchor, { controller, zone });
}

export function detach(anchor) {
    const held = zones.get(anchor);
    held?.controller.abort();
    zones.delete(anchor);
    held?.zone.classList.remove('upload-dragging');
}

/** Opens the browser's file chooser (from the Add files button). */
export function choose(dotnet) {
    const input = document.createElement('input');
    input.type = 'file';
    input.multiple = true;
    input.addEventListener('change', () => describe(Array.from(input.files ?? []), dotnet, 0), { once: true });
    input.click();
}

/**
 * Starts sending a file already described to the page, and returns at once: the upload goes on in this module while the person moves
 * around the Dashboard, and its progress and end are reported to the page's upload state (dotnet) as they happen.
 * kindKey and libraryId were chosen by the person (or by the page they are on); confirmLarge answers the large-upload warning.
 */
export function begin(fileId, kindKey, libraryId, token, dotnet, confirmLarge = false) {
    runUpload(fileId, kindKey, libraryId, token, dotnet, confirmLarge === true)
        .catch(error => console.warn('import-upload: an upload stopped unexpectedly', error));
}

async function runUpload(fileId, kindKey, libraryId, token, dotnet, confirmLarge) {
    const file = files.get(fileId);
    if (!file) {
        await tell(dotnet, 'OnUploadFinished', fileId, { state: 'failed', message: 'The file is no longer available. Choose it again.', closed: true });
        return;
    }

    const controller = new AbortController();
    const run = { controller, uploadId: runs.get(fileId)?.uploadId ?? null, token, cancelRequested: false };
    runs.set(fileId, run);
    const pending = findPending(storage(), file.name, file.size);
    // Taken before anything is awaited, so a second identical file starting in the same moment finds it taken.
    let resumeId = run.uploadId;
    if (!resumeId && pending && !claimed.has(pending.uploadId)) {
        resumeId = pending.uploadId;
        claimed.add(resumeId);
    }

    let lastReport = 0;
    const outcome = await uploadFile(file, {
        token,
        kindKey,
        libraryId,
        confirmLarge,
        resumeUploadId: resumeId,
        signal: controller.signal,
        onStale: uploadId => {
            removePending(storage(), uploadId);
            claimed.delete(uploadId);
        },
        onStarted: uploadId => {
            run.uploadId = uploadId;
            claimed.add(uploadId);
            if (run.cancelRequested) {
                // Cancel was pressed while the start was still on its way: now that there is an id, delete what it created.
                controller.abort();
                deleteAtEngine(uploadId, run.token);
                return;
            }

            savePending(storage(), { uploadId, name: file.name, size: file.size, kindKey, libraryId: libraryId ?? null });
        },
        onProgress: (sent, total) => {
            const now = Date.now();
            if (sent < total && now - lastReport < PROGRESS_INTERVAL_MS) return;
            lastReport = now;
            dotnet.invokeMethodAsync('OnUploadProgress', fileId, sent, total).catch(error => console.warn('import-upload: progress could not be shown', error));
        },
    });

    if (outcome.state !== 'failed' || outcome.closed === true) {
        if (run.uploadId) claimed.delete(run.uploadId);
        if (resumeId) claimed.delete(resumeId);
    }

    if (outcome.state === 'cancelled') {
        // Cancel already deleted the partial file and removed the row.
        runs.delete(fileId);
        files.delete(fileId);
        return;
    }

    if (outcome.state === 'failed' && outcome.closed !== true) {
        // The connection stayed down: the file and the Engine's upload are both kept so Try again can carry on.
        await tell(dotnet, 'OnUploadFinished', fileId, outcome);
        return;
    }

    if (outcome.state === 'refused' && outcome.code === 'large_upload_needs_confirm') {
        // Nothing was created; the file is kept so Upload anyway can send it with the person's confirmation.
        runs.delete(fileId);
        await tell(dotnet, 'OnUploadFinished', fileId, outcome);
        return;
    }

    // Finished, refused or closed: whatever this browser remembered about the file is now out of date.
    if (run.uploadId) removePending(storage(), run.uploadId);
    if (pending && !claimed.has(pending.uploadId)) removePending(storage(), pending.uploadId);
    runs.delete(fileId);
    files.delete(fileId);
    await tell(dotnet, 'OnUploadFinished', fileId, outcome);
}

// The page may have gone while the upload ran; telling it then fails, which is only worth a note.
async function tell(dotnet, method, ...args) {
    try {
        await dotnet.invokeMethodAsync(method, ...args);
    } catch (error) {
        console.warn(`import-upload: the page could not be told (${method}); it was probably closed`, error);
    }
}

function deleteAtEngine(uploadId, token) {
    cancelUpload(uploadId, { token })
        .then(() => removePending(storage(), uploadId))
        .catch(error => console.warn('import-upload: the Library could not be asked to delete the partial file; the sweep removes it after a day', error));
}

/** Stops a running upload (or drops a failed one) and deletes its partial file at the Engine. */
export function cancel(fileId, token) {
    const run = runs.get(fileId);
    if (run && run.uploadId === null) {
        // The start is still on its way: let it answer so the upload it creates can be deleted (see onStarted).
        run.cancelRequested = true;
        run.token = token;
        files.delete(fileId);
        return;
    }

    run?.controller.abort();
    const uploadId = run?.uploadId ?? null;
    if (uploadId) claimed.delete(uploadId);
    runs.delete(fileId);
    files.delete(fileId);
    if (uploadId) deleteAtEngine(uploadId, token);
}

/** Forgets a file the person dismissed or that was refused (nothing is kept at the Engine for it). */
export function forget(fileId) {
    // Release the upload so choosing the same file again in this tab resumes it rather than starting over.
    const uploadId = runs.get(fileId)?.uploadId;
    if (uploadId) claimed.delete(uploadId);
    runs.delete(fileId);
    files.delete(fileId);
}
