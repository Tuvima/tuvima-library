import test from 'node:test';
import assert from 'node:assert/strict';
import {attach,detach} from '../../../src/MediaEngine.Web/wwwroot/js/playback-tooltip.js';
let observer, timers=[], tips=[];
class Element extends EventTarget {
    constructor(){super();this.attrs=new Map();this.dataset={};this.style={};}
    getAttribute(key){return this.attrs.get(key)??null;}
    setAttribute(key,value){this.attrs.set(key,value);}
    removeAttribute(key){this.attrs.delete(key);}
    querySelector(){return this.candidate??null;}
    closest(){return null;}
    matches(){return true;}
    getBoundingClientRect(){return {left:210,top:200,bottom:244,width:44,height:44};}
    remove(){tips=tips.filter(tip=>tip!==this);}
}
globalThis.MutationObserver=class {constructor(fn){observer=fn;}observe(){}disconnect(){}};
globalThis.document={body:{append:tip=>tips.push(tip)},createElement:()=>new Element()};
globalThis.window=new EventTarget(); globalThis.innerWidth=320; globalThis.innerHeight=568;
globalThis.setTimeout=fn=>{const timer={fn};timers.push(timer);return timer;};
globalThis.clearTimeout=timer=>{timers=timers.filter(item=>item!==timer);};
function event(type,properties={}) {const value=new Event(type);Object.assign(value,properties);return value;}
function flush(){while(timers.length)timers.shift().fn();}
test('disabled and noninteractive content has a focusable bounded tooltip and restores accessibility on Escape and detach',()=>{
    const root=new Element();root.dataset.playbackTooltip='Full label';root.setAttribute('tabindex','-1');
    const disabled=new Element();disabled.disabled=true;root.candidate=disabled;
    root.setAttribute('aria-describedby','existing-help'); attach(root);attach(root);
    assert.equal(root.getAttribute('tabindex'),'0'); assert.equal(root.dataset.tooltipWrapperTarget,'true');
    root.dispatchEvent(event('focusin')); assert.equal(tips.length,1);assert.equal(tips[0].textContent,'Full label');
    assert.match(root.getAttribute('aria-describedby'),/^existing-help playback-tooltip-/);
    assert.ok(parseFloat(tips[0].style.left)>=8);
    root.dispatchEvent(event('keydown',{key:'Escape'}));assert.equal(tips.length,0);assert.equal(root.getAttribute('aria-describedby'),'existing-help');
    detach(root);assert.equal(root.getAttribute('tabindex'),'-1');assert.equal(root.dataset.tooltipWrapperTarget,undefined);
});
test('touch activation cancels delayed help and suppression removes a visible tooltip',()=>{
    const root=new Element();root.dataset.playbackTooltip='Queue'; root.candidate=new Element();attach(root);
    root.dispatchEvent(event('pointerdown',{pointerType:'touch'}));root.dispatchEvent(event('pointerup',{pointerType:'touch'}));flush();assert.equal(tips.length,0);
    root.dispatchEvent(event('pointerenter',{pointerType:'mouse'}));flush();assert.equal(tips.length,1);
    root.dataset.playbackTooltipSuppressed='true';observer();assert.equal(tips.length,0);
    detach(root);
});
