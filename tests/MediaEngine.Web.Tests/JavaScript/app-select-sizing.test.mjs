import test from 'node:test';
import assert from 'node:assert/strict';
import {attach,detach} from '../../../src/MediaEngine.Web/wwwroot/js/app-select-sizing.js';

test('select label updates reuse the observer and disposal releases it',()=>{
    const observers=[];
    globalThis.ResizeObserver=class {
        constructor(update){this.update=update;this.disconnected=false;observers.push(this);}
        observe(){} disconnect(){this.disconnected=true;}
    };
    globalThis.MutationObserver=class {observe(){} disconnect(){}};
    globalThis.window=new EventTarget();
    const natural={scrollWidth:200},field={getBoundingClientRect:()=>({width:100})};
    const root=new EventTarget();root.dataset={};
    root.closest=()=>null;
    root.querySelector=selector=>selector==='.app-select__width-label'?natural:selector==='.mud-input-control'?field:null;
    root.getAttribute=()=>null;root.setAttribute=()=>{};root.removeAttribute=()=>{};
    attach(root,'First long label');attach(root,'Replacement label');
    assert.equal(observers.length,1);
    assert.equal(root.dataset.playbackTooltip,'Replacement label');
    natural.scrollWidth=50;observers[0].update();
    assert.equal(root.dataset.selectTruncated,'false');
    assert.equal(root.dataset.playbackTooltip,'');
    detach(root);assert.equal(observers[0].disconnected,true);
    attach(root,'Mounted again');assert.equal(observers.length,2);
    detach(root);assert.equal(observers[1].disconnected,true);
});
