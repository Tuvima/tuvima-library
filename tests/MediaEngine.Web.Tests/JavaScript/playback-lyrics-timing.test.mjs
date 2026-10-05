import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
const source=readFileSync(new URL('../../../src/MediaEngine.Web/wwwroot/js/playback-lyrics.js',import.meta.url),'utf8');
const {fillFraction,dotFill,activeIndexAt}=await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
test('word fill handles boundaries and unusable end times',()=>{
 assert.equal(fillFraction(6,6,8),0); assert.equal(fillFraction(7,6,8),.5); assert.equal(fillFraction(8,6,8),1);
 assert.equal(fillFraction(7,6,8,true),1); assert.equal(fillFraction(5,6,null),0); assert.equal(fillFraction(6,6,null),1);
});
test('instrumental dots fill at thirds and stop at the gap end',()=>{
 assert.equal(dotFill(1/3,1),1); assert.equal(dotFill(2/3,2),1); assert.equal(dotFill(.5,2,true),.15);
 assert.equal(activeIndexAt([{start:0,end:6,instrumental:true},{start:6}],5.99),0);
 assert.equal(activeIndexAt([{start:0,end:6,instrumental:true},{start:6}],6),1);
});
