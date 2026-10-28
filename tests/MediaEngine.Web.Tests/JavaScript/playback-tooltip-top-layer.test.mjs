import test from 'node:test';
import assert from 'node:assert/strict';
import {attach,detach} from '../../../src/MediaEngine.Web/wwwroot/js/playback-tooltip.js';

// A tooltip must sit in the browser's top layer (a manual popover) and, when a modal dialog is open, inside that dialog, so the
// dialog can never cover it (Libraries spec D14, bug F9).
let timers=[];
class Element extends EventTarget {
    constructor(tag='span'){super();this.tag=tag;this.attrs=new Map();this.dataset={};this.style={};this.children=[];this.parent=null;this.popoverShown=false;}
    getAttribute(key){return this.attrs.get(key)??null;}
    setAttribute(key,value){this.attrs.set(key,value);}
    removeAttribute(key){this.attrs.delete(key);}
    querySelector(){return this.candidate??null;}
    closest(){return null;}
    matches(selector){return selector===':modal'?this.modal===true:true;}
    getBoundingClientRect(){return {left:210,top:200,bottom:244,width:44,height:44};}
    append(child){child.parent?.removeChild(child);child.parent=this;this.children.push(child);}
    removeChild(child){this.children=this.children.filter(item=>item!==child);}
    remove(){this.parent?.removeChild(this);this.parent=null;}
    showPopover(){this.popoverShown=true;}
}
const body=new Element('body');
let dialogs=[];
globalThis.MutationObserver=class {observe(){}disconnect(){}};
globalThis.document={body,fullscreenElement:null,querySelectorAll:selector=>selector==='dialog[open]'?dialogs:[],createElement:tag=>new Element(tag)};
globalThis.window=new EventTarget(); globalThis.innerWidth=1024; globalThis.innerHeight=768;
globalThis.setTimeout=fn=>{const timer={fn};timers.push(timer);return timer;};
globalThis.clearTimeout=timer=>{timers=timers.filter(item=>item!==timer);};
function event(type,properties={}) {const value=new Event(type);Object.assign(value,properties);return value;}
function flush(){while(timers.length)timers.shift().fn();}
function openDialog(modal=true){const dialog=new Element('dialog');dialog.modal=modal;dialogs.push(dialog);return dialog;}

test('a tooltip shown over an open modal dialog is a manual popover inside that dialog',()=>{
    const dialog=openDialog();
    const root=new Element();root.dataset.playbackTooltip='About this folder';root.candidate=new Element('button');attach(root);
    root.dispatchEvent(event('pointerenter',{pointerType:'mouse'}));flush();
    assert.equal(dialog.children.length,1);
    const tip=dialog.children[0];
    assert.equal(tip.textContent,'About this folder');
    assert.equal(tip.getAttribute('popover'),'manual');
    assert.equal(tip.popoverShown,true);
    assert.equal(body.children.length,0);
    assert.equal(tip.style.margin,'0');
    assert.equal(tip.style.inset,'auto');
    root.dispatchEvent(event('pointerleave'));
    assert.equal(dialog.children.length,0);
    detach(root);dialogs=[];
});

test('with several dialogs open the tooltip goes into the topmost one, and a non-modal dialog is ignored',()=>{
    const lower=openDialog();const top=openDialog();openDialog(false);
    const root=new Element();root.dataset.playbackTooltip='Top one';root.candidate=new Element('button');attach(root);
    root.dispatchEvent(event('pointerenter',{pointerType:'mouse'}));flush();
    assert.equal(top.children.length,1);assert.equal(lower.children.length,0);
    root.dispatchEvent(event('pointerleave'));detach(root);dialogs=[];
});

test('with no dialog open the tooltip is still a manual popover, on the page body',()=>{
    dialogs=[];
    const root=new Element();root.dataset.playbackTooltip='Plain page';root.candidate=new Element('button');attach(root);
    root.dispatchEvent(event('pointerenter',{pointerType:'mouse'}));flush();
    assert.equal(body.children.length,1);
    assert.equal(body.children[0].getAttribute('popover'),'manual');
    assert.equal(body.children[0].popoverShown,true);
    root.dispatchEvent(event('pointerleave'));assert.equal(body.children.length,0);detach(root);
});
