// While Vault mode is on: pressing Escape twice locks it at once, and real use of the page restarts the idle timer.
// Loaded only after the Engine has said this device session is unlocked; nothing here runs for any other profile.
const doubleEscapeMs = 700;
const activityEveryMs = 30000;

let handler = null;
let activityHandler = null;
let lastEscape = 0;
let lastActivity = 0;

export function start(dotNetRef) {
    stop();
    lastEscape = 0;
    lastActivity = 0;

    handler = (event) => {
        if (event.key !== 'Escape') {
            return;
        }

        const now = Date.now();
        if (now - lastEscape <= doubleEscapeMs) {
            lastEscape = 0;
            dotNetRef.invokeMethodAsync('OnDoubleEscape');
        } else {
            lastEscape = now;
        }
    };

    activityHandler = () => {
        const now = Date.now();
        if (now - lastActivity >= activityEveryMs) {
            lastActivity = now;
            dotNetRef.invokeMethodAsync('OnActivity');
        }
    };

    document.addEventListener('keydown', handler, true);
    for (const name of ['pointerdown', 'keydown', 'wheel', 'touchstart']) {
        document.addEventListener(name, activityHandler, { capture: true, passive: true });
    }
}

export function stop() {
    if (handler) {
        document.removeEventListener('keydown', handler, true);
        handler = null;
    }

    if (activityHandler) {
        for (const name of ['pointerdown', 'keydown', 'wheel', 'touchstart']) {
            document.removeEventListener(name, activityHandler, true);
        }
        activityHandler = null;
    }
}
