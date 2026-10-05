const storageKey = 'tuvima.audio-output';
const audio = () => document.getElementById('listen-audio-engine');
const volumeSupport = new WeakMap();
let listener;
export function softwareVolumeSupported(element) {
    if (!element) return false;
    if (volumeSupport.has(element)) return volumeSupport.get(element);
    const previous = element.volume;
    let supported = false;
    try { const probe = previous > .5 ? .25 : .75; element.volume = probe; supported = Math.abs(element.volume - probe) < .01; }
    catch { supported = false; }
    finally { try { element.volume = previous; } catch {} }
    volumeSupport.set(element, supported);
    return supported;
}
export async function list() {
    const element = audio();
    const fallback = { supported:false, devices:[], activeDeviceId:element?.sinkId || '', softwareVolumeSupported:softwareVolumeSupported(element) };
    if (!element?.setSinkId || !globalThis.isSecureContext || !navigator.mediaDevices?.enumerateDevices) return fallback;
    try {
        const permitted = (await navigator.mediaDevices.enumerateDevices()).filter(device => device.kind === 'audiooutput' && device.deviceId && device.label);
        const devices = [{deviceId:'',label:'System default'}, ...permitted.filter(device => device.deviceId !== 'default').map(device => ({deviceId:device.deviceId,label:device.label}))];
        const disconnected = fallback.activeDeviceId && !devices.some(device => device.deviceId === fallback.activeDeviceId);
        return {...fallback,supported:devices.length > 1,devices,message:disconnected ? 'Your selected output disconnected. Choose an available output or use your device sound settings.' : null};
    } catch { return fallback; } // Permission/policy failures are expected; never obtain microphone access.
}
export async function select(deviceId, assetId, requestVersion) {
    const element = audio();
    const current = () => element && element === audio() && (assetId == null || element.dataset?.currentAssetId === assetId
        && Number(element.dataset?.playbackRequestVersion) === Number(requestVersion));
    const state = await list();
    if (!current()) return {...state,message:'Playback changed before output selection completed.'};
    if (!state.supported || typeof deviceId !== 'string' || !state.devices.some(device => device.deviceId === deviceId))
        return {...state,message:'That output is unavailable. Use your device sound settings.'};
    try {
        await element.setSinkId(deviceId);
        if (!current()) return {...await list(),message:'Playback changed before output selection completed.'};
        try { localStorage.setItem(storageKey, deviceId); } catch { /* Device preference is optional in restricted storage. */ }
        return await list();
    } catch { return {...state,message:'The browser could not switch output. Your previous output is retained.'}; }
}
export async function attach(receiver) {
    detach();
    if (receiver && navigator.mediaDevices?.addEventListener) {
        listener = async () => { try { await receiver.invokeMethodAsync('AudioCapabilitiesChangedAsync', await list()); } catch {} };
        navigator.mediaDevices.addEventListener('devicechange', listener);
    }
    const state = await list();
    try { const saved = localStorage.getItem(storageKey); if (state.supported && saved && state.devices.some(device => device.deviceId === saved)) await select(saved); }
    catch { /* Optional preference must not prevent the native player from attaching. */ }
    return await list();
}
export function detach() { if (listener) navigator.mediaDevices?.removeEventListener('devicechange', listener); listener = null; }
if (typeof window !== 'undefined') window.listenOutput = {list,select};
