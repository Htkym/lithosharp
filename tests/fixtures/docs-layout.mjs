import {readFileSync} from 'node:fs';
import vm from 'node:vm';
import {test} from 'node:test';
import assert from 'node:assert/strict';
const source=readFileSync(new URL('../../src/LithoSharp/Browser/layout.mjs',import.meta.url),'utf8');
function fixture({docs=true,footerHeight=70}={}) {
  const events=()=>({listeners:new Map(),addEventListener(k,f){if(!this.listeners.has(k))this.listeners.set(k,new Set());this.listeners.get(k).add(f);},removeEventListener(k,f){this.listeners.get(k)?.delete(f);},fire(k){for(const f of this.listeners.get(k)??[])f();}});
  const window=events(),document=events(),values=new Map(),observers=[];
  const shell={},header={height:80,getBoundingClientRect(){return{height:this.height};}},footer={height:footerHeight,getBoundingClientRect(){return{height:this.height};}};
  const body={classList:{contains:()=>docs},querySelector:s=>s==='.docs-shell'?shell:s==='.docs-header'?header:footer,style:{setProperty:(k,v)=>values.set(k,v)}};
  const category={open:false,matches:s=>s==='details.api-category-card'};
  Object.assign(document,{body,readyState:'complete',getElementById:id=>id==='category'?category:null});
  class ResizeObserver{constructor(fn){this.fn=fn;observers.push(this);}observe(){}disconnect(){this.disconnected=true;}}
  const context=vm.createContext({document,Symbol,ResizeObserver,getComputedStyle:()=>({paddingBlockStart:'48px',paddingBlockEnd:'64px'}),location:{hash:'#category'},addEventListener:window.addEventListener.bind(window),removeEventListener:window.removeEventListener.bind(window)});
  vm.runInContext(source,context);
  return {document,window,values,header,footer,observers,category,context};
}
test('measures shared header, wrapped footer and shell padding without a menu',()=>{const f=fixture();assert.equal(f.values.get('--docs-header-height'),'80px');assert.equal(f.values.get('--docs-footer-height'),'70px');assert.equal(f.values.get('--docs-shell-block-spacing'),'112px');f.footer.height=140;f.observers[0].fn();assert.equal(f.values.get('--docs-footer-height'),'140px');});
test('native API category fragment opens the matching disclosure',()=>{assert.equal(fixture().category.open,true);});
test('navigation releases measurements and reinitializes without duplicate resize listeners',()=>{const f=fixture();f.document.fire('lithosharp:before-navigate');assert.ok(f.observers[0].disconnected);assert.equal(f.window.listeners.get('resize').size,0);f.document.fire('lithosharp:page');assert.equal(f.window.listeners.get('resize').size,1);vm.runInContext(source,f.context);assert.equal(f.window.listeners.get('resize').size,1);});
test('non-documentation pages are not modified',()=>{assert.equal(fixture({docs:false}).values.size,0);});
