import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';
const source=readFileSync(new URL('../../../src/MediaEngine.Web/wwwroot/app.js',import.meta.url),'utf8');
const begin=source.indexOf('window.showMediaTileHover = function');
const end=source.indexOf('window.clearMediaTileHover = function',begin);
function expanded(classes,width=175,rowWidth=1600){
 const props=new Map();
 const classList={contains:name=>classes.includes(name),add(){}};
 const panel={classList,querySelector:()=>null};
 const row={querySelector:()=>null,getBoundingClientRect:()=>({width:rowWidth}),classList};
 const frame={getBoundingClientRect:()=>({width,height:260})};
 const card={classList,style:{getPropertyValue:name=>props.get(name),setProperty:(name,value)=>props.set(name,value)},closest:selector=>selector.includes('shelf')?row:null,querySelector:selector=>selector.includes('panel')?panel:frame};
 const window={requestAnimationFrame(){}};
 vm.runInNewContext(source.slice(begin,end),{window,document:{}});
 window.showMediaTileHover(card);
 return Number.parseFloat(props.get('--media-tile-expanded-width'));
}
test('portrait movie and TV shelf expansion stays within 440 pixels',()=>{
 for(const kind of ['is-media-movie','is-media-tv']){
  assert.equal(expanded(['is-portrait',kind],260),440);
  assert.ok(expanded(['is-portrait',kind])<=440);
  assert.equal(expanded(['is-portrait',kind],175,350),334);
 }
});
test('collection and landscape previews retain their existing width allowance',()=>{
 assert.equal(expanded(['is-landscape','is-media-movie']),520);
 assert.equal(expanded(['is-portrait','is-media-tv','is-collection-card']),520);
});
