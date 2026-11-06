// The whole page is the drop target for files (spec view-section D9). A dashed outline shows while files are dragged over the window;
// dropped files go into the page's hidden file input, so the page handles them exactly like files picked with "Add media".
export function attach(overlay, input) {
    const hasFiles = (event) => Array.from(event.dataTransfer?.types ?? []).includes('Files');
    let depth = 0;
    const show = (on) => overlay.classList.toggle('is-active', on);
    const onEnter = (event) => { if (!hasFiles(event)) return; depth++; show(true); };
    const onOver = (event) => { if (!hasFiles(event)) return; event.preventDefault(); event.dataTransfer.dropEffect = 'copy'; };
    const onLeave = (event) => { if (!hasFiles(event)) return; depth = Math.max(0, depth - 1); if (depth === 0) show(false); };
    const onDrop = (event) => {
        if (!hasFiles(event)) return;
        event.preventDefault();
        depth = 0;
        show(false);
        if (!event.dataTransfer.files.length) return;
        input.files = event.dataTransfer.files;
        input.dispatchEvent(new Event('change', { bubbles: true }));
    };
    window.addEventListener('dragenter', onEnter);
    window.addEventListener('dragover', onOver);
    window.addEventListener('dragleave', onLeave);
    window.addEventListener('drop', onDrop);
    return {
        dispose() {
            window.removeEventListener('dragenter', onEnter);
            window.removeEventListener('dragover', onOver);
            window.removeEventListener('dragleave', onLeave);
            window.removeEventListener('drop', onDrop);
        },
    };
}
