import test from 'node:test';
import assert from 'node:assert/strict';

let element;
let permitted = [];
let denied = false;
const devices = new EventTarget();
devices.enumerateDevices = async () => { if (denied) throw new Error('denied'); return permitted; };
devices.getUserMedia = () => { throw new Error('Output must never request microphone access'); };
const handlers = new Map(); const positions = [];
const session = { setActionHandler: (key, value) => handlers.set(key,value), setPositionState: value => positions.push(value) };
Object.defineProperty(globalThis,'navigator',{value:{mediaDevices:devices,mediaSession:session},configurable:true});
globalThis.document = {getElementById: () => element};
globalThis.isSecureContext = true;
globalThis.localStorage = {getItem: () => null,setItem: () => {throw new Error('restricted storage');}};
globalThis.MediaMetadata = class { constructor(value) { Object.assign(this,value); } };
const output = await import('../../../src/MediaEngine.Web/wwwroot/js/playback-output.js');
const media = await import('../../../src/MediaEngine.Web/wwwroot/js/playback-media-session.js');
function audio() {
    const value = new EventTarget(); Object.assign(value,{volume:.6,sinkId:'',duration:180,currentTime:10,playbackRate:1.25,paused:true});
    value.setSinkId = async id => { if (id === 'broken') throw new Error('hardware unavailable'); value.sinkId = id; };
    return value;
}
test('output exposes only permitted devices and failure retains the checked sink', async () => {
    element = audio(); denied = false;
    permitted = [{kind:'audiooutput',deviceId:'headset',label:'Headset'},{kind:'audiooutput',deviceId:'broken',label:'Unavailable'}, {kind:'audioinput',deviceId:'microphone',label:'Microphone'}];
    const state = await output.attach(); assert.equal(state.supported,true); assert.equal(element.volume,.6);
    assert.equal((await output.select('headset')).activeDeviceId,'headset');
    const failure = await output.select('broken'); assert.equal(failure.activeDeviceId,'headset'); assert.match(failure.message,/previous output/);
    permitted = []; const disconnected = await output.list(); assert.match(disconnected.message,/disconnected/); assert.equal(element.sinkId,'headset');
    denied = true; assert.equal((await output.list()).supported,false); output.detach();
});
test('software volume is truthful when native writes are ignored', () => {
    const fixed = {}; Object.defineProperty(fixed,'volume',{get:()=>1,set:()=>{}});
    assert.equal(output.softwareVolumeSupported(fixed),false);
});
test('output rejects a playback change during permitted-device enumeration before touching the sink', async () => {
    element = audio(); element.dataset = {currentAssetId:'shown-asset',playbackRequestVersion:'4'};
    denied = false;
    const enumerate = devices.enumerateDevices;
    devices.enumerateDevices = async () => {
        element.dataset.playbackRequestVersion = '5';
        return [{kind:'audiooutput',deviceId:'headset',label:'Headset'}];
    };
    try {
        const result = await output.select('headset','shown-asset',4);
        assert.match(result.message,/Playback changed/);
        assert.equal(element.sinkId,'');
    } finally { devices.enumerateDevices = enumerate; }
});
test('media actions address the owner and positions follow finite native state with complete cleanup', async () => {
    element = audio(); const calls=[];
    const receiver = {invokeMethodAsync:(...args)=>{ calls.push(args); return Promise.resolve(); }};
    media.attach(element,receiver); media.attach(element,receiver);
    media.metadata({title:'Fixture song',artist:'Fixture artist',album:'Fixture album'});
    assert.equal(session.metadata.title,'Fixture song');
    handlers.get('play')({}); handlers.get('play')({}); handlers.get('seekto')({seekTime:40});
    assert.deepEqual(calls.map(row=>row.slice(0,2)),[['MediaSessionActionAsync','play'],['MediaSessionActionAsync','play'],['MediaSessionActionAsync','seekto']]);
    assert.equal(calls[2][2],40);
    assert.deepEqual(positions.at(-1),{duration:180,position:10,playbackRate:1.25});
    element.duration=Infinity; element.dispatchEvent(new Event('durationchange')); assert.equal(positions.at(-1),undefined);
    media.metadata(null); element.paused=false; element.dispatchEvent(new Event('play'));
    assert.equal(session.playbackState,'none');assert.equal(positions.at(-1),undefined);
    media.detach(element); assert.equal(session.metadata,null); assert.equal(session.playbackState,'none');
    assert.ok([...handlers.values()].every(value=>value===null));
    const count=positions.length; element.dispatchEvent(new Event('play')); assert.equal(positions.length,count);
});
