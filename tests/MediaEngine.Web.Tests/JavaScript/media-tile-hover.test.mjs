import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';
const source=readFileSync(new URL('../../../src/MediaEngine.Web/wwwroot/app.js',import.meta.url),'utf8');
const begin=source.indexOf('window.showMediaTileHover = function');
const end=source.indexOf('window.clearMediaTileHover = function',begin);
function expanded(classes,width=175,rowWidth=1600,height=260,capture=false){
 const props=new Map(); let mounted=false; const added=[];
 const classList={contains:name=>classes.includes(name),add(name){added.push(name)},remove(){}};
 const panel={classList,style:{setProperty(){}},querySelector:()=>null};
 const row={querySelector:()=>null,getBoundingClientRect:()=>({width:rowWidth}),classList};
 const frame={getBoundingClientRect:()=>({width,height})};
 const card={classList,style:{getPropertyValue:name=>props.get(name),setProperty:(name,value)=>props.set(name,value)},closest:selector=>selector.includes('shelf')?row:null,querySelector:selector=>selector.includes('panel')?panel:frame};
 const window={innerWidth:rowWidth,requestAnimationFrame(){},mountMediaTileHover(){mounted=true}};
 vm.runInNewContext(source.slice(begin,end),{window,document:{}});
 window.showMediaTileHover(card);
 return capture ? {width:Number.parseFloat(props.get('--media-tile-expanded-width')),added,mounted} : Number.parseFloat(props.get('--media-tile-expanded-width'));
}
test('Watch backdrop width follows the resting height and stays within the viewport',()=>{
 for(const kind of ['is-media-movie','is-media-tv']){
  assert.equal(expanded(['is-portrait',kind],175,1920,260),462);
  assert.equal(expanded(['is-portrait',kind],175,1000,230),409);
  assert.equal(expanded(['is-portrait',kind],175,350,230),326);
  assert.equal(expanded(['is-landscape',kind],320,1600,180),320);
 }
});
test('stable shelf height uses resting offset size even while a preview is transformed',()=>{
 const props=new Map();
 const row={style:{height:'',setProperty:(key,value)=>props.set(key,value)},querySelectorAll:()=>[tile]};
 const tile={offsetHeight:300,getBoundingClientRect:()=>({height:700}),closest:()=>row};
 const window={getComputedStyle:()=>({paddingTop:'8',paddingBottom:'12'})};
 const begin=source.indexOf('window.updateMediaTileShelfStableHeight = function');
 const end=source.indexOf('window.registerMediaShelfBoundaries',begin);
 vm.runInNewContext(source.slice(begin,end),{window});
 window.updateMediaTileShelfStableHeight(row);
 assert.equal(row.style.height,'320px');
 assert.equal(props.get('--media-tile-row-height'),'300px');
});

test('Watch preview placement preserves fractional cover height without adding detail height',()=>{
 const props=new Map();
 const classList={contains:name=>['is-media-movie','is-banner-popover'].includes(name),add(){}};
 const style={left:'',top:'',removeProperty:key=>props.delete(key),setProperty:(key,value)=>props.set(key,value)};
 const panel={classList,style,querySelector:()=>({getBoundingClientRect:()=>({height:90})}),
  getBoundingClientRect:()=>({width:410,height:230.45})};
 const frame={getBoundingClientRect:()=>({left:150,top:200,width:153.63,height:230.45})};
 const card={querySelector:selector=>selector.includes('panel')?panel:frame};
 const window={innerWidth:1536,innerHeight:864,requestAnimationFrame:cb=>cb(),getComputedStyle:()=>({maxHeight:'230.45px'})};
 const begin=source.indexOf('window.positionMediaTileHover = function');
 const end=source.indexOf('window.correctMediaTileHoverViewport = function',begin);
 vm.runInNewContext(source.slice(begin,end),{window,document:{documentElement:{clientWidth:1536,clientHeight:864}}});
 window.positionMediaTileHover(card);
 assert.equal(props.get('--media-tile-hover-anchor-height'),'230.45px');
 assert.equal(props.get('--media-tile-hover-top'),'200px');
});

test('Home preview remains inline instead of mounting a floating overlay',()=>{
 const state=expanded(['is-portrait','is-media-movie'],175,1536,230,true);
 assert.ok(state.added.includes('is-inline-expanded'));
 assert.ok(state.added.includes('is-hover-active'));
 assert.equal(state.mounted,false);
 assert.equal(state.width,409);
});

test('shelf height stays frozen through expansion and contraction callbacks',()=>{
 const row={classList:{contains:name=>name==='has-active-in-row-hover'},style:{height:'320px',setProperty(){throw new Error('changed frozen row')}},querySelectorAll(){throw new Error('measured animated geometry')}};
 const window={};
 const begin=source.indexOf('window.updateMediaTileShelfStableHeight = function');
 const end=source.indexOf('window.registerMediaShelfBoundaries',begin);
 vm.runInNewContext(source.slice(begin,end),{window});
 window.updateMediaTileShelfStableHeight(row);
 assert.equal(row.style.height,'320px');
});

function packContinue(width, viewport, cards) {
 const groups=cards.map(({width,count})=>{
  const tiles=Array.from({length:count},()=>({getBoundingClientRect:()=>({width})}));
  const scroll={querySelectorAll:()=>tiles};
  return {style:{width:'',removeProperty(){this.width=''}},querySelector:()=>scroll};
 });
 const row={clientWidth:width,querySelectorAll:()=>groups};
 const window={innerWidth:viewport};
 const begin=source.indexOf('window.packContinueGroups = function');
 const end=source.indexOf('window.updateMediaTileShelfVisibleWidth',begin);
 vm.runInNewContext(source.slice(begin,end),{window,getComputedStyle:()=>({gap:'24px',paddingLeft:'8px',paddingRight:'8px'})});
 window.packContinueGroups(row);
 return groups.map(g=>g.style.width);
}
test('desktop Continue reserves two whole cards per group rather than shrinking to one',()=>{
 assert.deepEqual(packContinue(1430,1536,[{width:254.01,count:2},{width:123.01,count:2},{width:184.01,count:5}]),['645px','383px','505px']);
});
test('wide Continue can spend remaining space on additional whole cards',()=>{
 assert.deepEqual(packContinue(1900,1920,[{width:254.01,count:2},{width:123.01,count:2},{width:184.01,count:5}]),['645px','383px','713px']);
});
test('Continue does not invent a second card when only one exists',()=>{
 assert.deepEqual(packContinue(1430,1536,[{width:254.01,count:1},{width:123.01,count:2}]),['367px','383px']);
});
test('tablet and mobile groups use the full available CSS row',()=>{
 assert.deepEqual(packContinue(700,768,[{width:200,count:2},{width:120,count:2}]),['','']);
 assert.deepEqual(packContinue(320,390,[{width:200,count:2},{width:120,count:2}]),['','']);
});
