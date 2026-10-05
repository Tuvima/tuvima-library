import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import fs from 'node:fs';
const source = fs.readFileSync(new URL('../../../src/MediaEngine.Web/wwwroot/app.js',import.meta.url),'utf8');
const code = source.slice(source.indexOf('window.detailOrigin = (() => {'),source.indexOf('// Register before the first detail link'));
function setup() {
    const frames = [], timers = [], store = new Map(), containers = [];
    let mutation;
    const window = {location:{pathname:'/listen',search:'',href:'http://fixture/listen'},scrollY:400,
        scrollTo:({top})=>{window.scrollY=top;}, requestAnimationFrame:fn=>frames.push(fn),setTimeout:fn=>timers.push(fn),
        addEventListener:()=>{},history:{back:()=>{}},};
    const document = {body:{},activeElement:null,addEventListener:()=>{},querySelectorAll:()=>containers};
    class MutationObserver {constructor(fn){mutation=fn;}observe(){}disconnect(){}}
    vm.runInNewContext(code,{window,document,MutationObserver,URL,HTMLAnchorElement:class {},Element:class {},
        sessionStorage:{getItem:key=>store.get(key),setItem:(key,value)=>store.set(key,value)},
        requestAnimationFrame:fn=>frames.push(fn),setTimeout:fn=>timers.push(fn),console});
    return {window,store,containers,mutation:()=>mutation(),flushFrames:()=>{while(frames.length) frames.shift()();},flushTimers:()=>{while(timers.length) timers.shift()();}};
}
test('player detail intent defeats delayed restoration and resets late mounted containers only for that navigation',()=>{
    const h=setup();
    h.store.set('tuvima:detail-origin:/details/musicalbum/1',JSON.stringify({scrollY:600,scrollContainers:[{key:'main',top:900}]}));
    h.window.location.pathname='/details/musicalbum/1';
    h.window.detailOrigin.restore(); // The old scheduled restore has not completed.
    h.window.detailOrigin.fresh('/details/musicalbum/1');
    const pane={scrollTop:880,getAttribute:()=> 'main',querySelector:()=>null}; h.containers.push(pane);
    h.mutation(); h.flushFrames(); h.flushTimers();
    assert.equal(h.window.scrollY,0); assert.equal(pane.scrollTop,0);
    // Normal later navigation to the same route still uses its saved position.
    h.store.set('tuvima:detail-origin:/details/musicalbum/1',JSON.stringify({scrollY:220,scrollContainers:[{key:'main',top:330}]}));
    h.window.detailOrigin.restore(); h.flushFrames(); h.flushTimers();
    assert.equal(h.window.scrollY,220); assert.equal(pane.scrollTop,330);
});
test('player detail intent resets the shell main pane that detail pages scroll inside',()=>{
    // The harness returns every registered container, so also pin the real selector.
    assert.match(code,/freshScrollSelector = '[^']*\.context-sidebar-shell__main/);
    assert.doesNotMatch(code,/\.context-sidebar__content/);
    const h=setup();
    const shellMain={scrollTop:700,getAttribute:()=>null,querySelector:()=>null};
    h.window.location.pathname='/details/person/2';
    h.window.detailOrigin.fresh('/details/person/2');
    h.containers.push(shellMain);
    h.flushFrames();
    assert.equal(shellMain.scrollTop,0);
});
test('normal detail restoration preserves browsing position without a player intent',()=>{
    const h=setup(); h.store.set('tuvima:detail-origin:/listen',JSON.stringify({scrollY:480}));
    h.window.detailOrigin.restore(); h.flushFrames(); h.flushTimers(); assert.equal(h.window.scrollY,480);
});
test('modified queue keys never also change transport volume',()=>{
    let handler; const calls=[];
    const block=source.slice(source.indexOf('    function registerPlayerShortcuts('),source.indexOf('    function audioEngineElement('));
    const context={unregisterPlayerShortcuts:()=>{},isEditableShortcutTarget:()=>false,setShortcutHandler:()=>{},console};
    vm.runInNewContext(block,context);
    context.registerPlayerShortcuts({addEventListener:(_,fn)=>{handler=fn;}},{invokeMethodAsync:(...args)=>{calls.push(args);return Promise.resolve();}});
    handler({key:'ArrowDown',altKey:true,preventDefault:()=>{throw new Error('Must retain the queue key');}});
    assert.equal(calls.length,0);
    handler({key:'ArrowDown',preventDefault:()=>{}});
    assert.deepEqual(calls,[['HandlePlayerShortcut','volume-down']]);
});
