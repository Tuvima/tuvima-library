// ============================================================================
// Book reader pictures — Tuvima Library
//
// Two small, dependency-free helpers for the book reader (book-reader.js):
//   * working out what kind of picture a file is when the book's own label for it is
//     missing or wrong (real books ship `media-type=""` or "application/octet-stream"),
//   * putting a quiet "Picture unavailable" note where one picture cannot be shown.
// Neither widens what a book may do: only well-known picture types come out of the first,
// and the second only adds plain text.
// ============================================================================

export const PICTURE_UNAVAILABLE_TEXT = 'Picture unavailable';
export const PICTURE_PLACEHOLDER_CLASS = 'tuvima-picture-unavailable';

const TYPES_BY_EXTENSION = {
    png: 'image/png',
    jpg: 'image/jpeg',
    jpeg: 'image/jpeg',
    jpe: 'image/jpeg',
    jfif: 'image/jpeg',
    gif: 'image/gif',
    webp: 'image/webp',
    avif: 'image/avif',
    bmp: 'image/bmp',
    ico: 'image/x-icon',
    svg: 'image/svg+xml',
};

/** "OEBPS/Images/Fig-1.JPG?x=1" -> "image/jpeg"; '' when the name says nothing useful. */
export function pictureTypeFromName(name) {
    const clean = String(name ?? '').split(/[?#]/)[0];
    const dot = clean.lastIndexOf('.');
    if (dot < 0 || clean.lastIndexOf('/') > dot) return '';
    return TYPES_BY_EXTENSION[clean.slice(dot + 1).toLowerCase()] ?? '';
}

const startsWith = (bytes, signature, offset = 0) => signature.every((value, i) => bytes[offset + i] === value);
const ascii = (bytes, from, to) => String.fromCharCode(...Array.from(bytes.subarray(from, to)));

/** The first bytes of a picture file -> its type, or '' when they are not a picture format the reader knows. */
export function pictureTypeFromBytes(bytes) {
    if (!bytes || bytes.length < 4) return '';
    if (startsWith(bytes, [0x89, 0x50, 0x4e, 0x47])) return 'image/png';
    if (startsWith(bytes, [0xff, 0xd8, 0xff])) return 'image/jpeg';
    if (startsWith(bytes, [0x47, 0x49, 0x46, 0x38])) return 'image/gif';
    if (startsWith(bytes, [0x42, 0x4d])) return 'image/bmp';
    if (startsWith(bytes, [0x00, 0x00, 0x01, 0x00])) return 'image/x-icon';
    if (bytes.length >= 12 && ascii(bytes, 0, 4) === 'RIFF' && ascii(bytes, 8, 12) === 'WEBP') return 'image/webp';
    if (bytes.length >= 12 && ascii(bytes, 4, 8) === 'ftyp' && /^(avif|avis)$/.test(ascii(bytes, 8, 12))) return 'image/avif';
    return '';
}

async function headBytes(data) {
    if (typeof Blob !== 'undefined' && data instanceof Blob) return new Uint8Array(await data.slice(0, 16).arrayBuffer());
    if (data instanceof ArrayBuffer) return new Uint8Array(data, 0, Math.min(16, data.byteLength));
    if (ArrayBuffer.isView(data)) return new Uint8Array(data.buffer, data.byteOffset, Math.min(16, data.byteLength));
    return null;
}

/**
 * The picture type for a file whose declared type could not be used: what its first bytes say, then what its name says.
 * (The bytes win: a ".png" that is really a JPEG still shows.) '' means "not a picture the reader knows".
 */
export async function inferPictureType(name, data) {
    const fromBytes = pictureTypeFromBytes(await headBytes(data));
    if (fromBytes) return fromBytes;
    // A name that says ".svg" only routes the file into the SVG clean-up; it is never trusted as a picture as it stands.
    return pictureTypeFromName(name);
}

/**
 * Put a "Picture unavailable" note in place of one picture that could not load. Safe to call twice for the same picture.
 * The page is a hardened book page (no scripts), so this only adds plain text and styles set through the DOM.
 */
export function showPictureUnavailable(img) {
    const doc = img.ownerDocument;
    if (!img.parentNode || img.dataset?.tuvimaBroken === '1') return null;
    const note = doc.createElement('span');
    note.className = PICTURE_PLACEHOLDER_CLASS;
    note.setAttribute('role', 'img');
    note.setAttribute('aria-label', img.getAttribute('alt') ? `${PICTURE_UNAVAILABLE_TEXT}: ${img.getAttribute('alt')}` : PICTURE_UNAVAILABLE_TEXT);
    note.textContent = PICTURE_UNAVAILABLE_TEXT;
    const style = note.style;
    style.setProperty('display', 'inline-block', 'important');
    style.setProperty('box-sizing', 'border-box', 'important');
    style.setProperty('max-width', '100%', 'important');
    style.setProperty('padding', '0.5em 0.9em', 'important');
    style.setProperty('border', '1px dashed currentColor', 'important');
    style.setProperty('border-radius', '6px', 'important');
    style.setProperty('opacity', '0.6', 'important');
    style.setProperty('font-size', '0.85em', 'important');
    style.setProperty('font-style', 'normal', 'important');
    style.setProperty('text-indent', '0', 'important');
    style.setProperty('text-align', 'center', 'important');
    img.dataset.tuvimaBroken = '1';
    img.replaceWith(note);
    return note;
}

const hasSource = img => !!(img.getAttribute('src') || img.getAttribute('srcset'));

/**
 * Watch every `<img>` on a book page: one that fails (damaged file, unsupported format, type nobody could work out)
 * gets the "Picture unavailable" note and the rest of the page is untouched. Pictures that already failed before this
 * ran are found by asking the browser to decode them (an SVG without a built-in size also reports a width of 0, but it
 * decodes fine). Pictures without any address (an outside picture the clean-up removed) are left alone.
 */
export function markBrokenPictures(doc) {
    if (!doc?.addEventListener) return;
    // `error` does not bubble, but it does travel down the capture phase.
    doc.addEventListener('error', e => {
        const img = e.target;
        if (img?.localName === 'img' && hasSource(img)) showPictureUnavailable(img);
    }, true);
    for (const img of Array.from(doc.querySelectorAll('img'))) {
        if (hasSource(img) && img.complete && img.naturalWidth === 0) {
            Promise.resolve(img.decode?.()).catch(() => showPictureUnavailable(img));
        }
    }
}
