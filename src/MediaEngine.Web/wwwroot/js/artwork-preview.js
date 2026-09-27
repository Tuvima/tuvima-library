const connections = new WeakMap();

export function connect(preview, input) {
    if (connections.has(preview)) return;
    const controller = new AbortController();
    const options = { signal: controller.signal };
    const isFile = event => Array.from(event.dataTransfer?.types ?? []).includes('Files');
    preview.addEventListener('dragover', event => {
        if (!isFile(event)) return;
        event.preventDefault();
        event.dataTransfer.dropEffect = input.disabled ? 'none' : 'copy';
        preview.classList.toggle('is-dragging', !input.disabled);
    }, options);
    preview.addEventListener('dragleave', event => {
        if (!preview.contains(event.relatedTarget)) preview.classList.remove('is-dragging');
    }, options);
    preview.addEventListener('drop', event => {
        event.preventDefault();
        preview.classList.remove('is-dragging');
        if (input.disabled || !event.dataTransfer?.files.length) return;
        const transfer = new DataTransfer();
        transfer.items.add(event.dataTransfer.files[0]);
        input.files = transfer.files;
        input.dispatchEvent(new Event('change', { bubbles: true }));
    }, options);
    connections.set(preview, controller);
}

export function choose(input) { input.value = ''; input.click(); }
export function disconnect(preview) {
    connections.get(preview)?.abort();
    connections.delete(preview);
}
