import {readFileSync} from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';
import {test} from 'node:test';
const source = readFileSync(new URL('../../src/LithoSharp/Browser/sidebar.mjs', import.meta.url),'utf8');
function setup({compact=false, saved, width=1400, depth=1}={}) {
  const events = target => Object.assign(target,{listeners:{},addEventListener(name,fn){this.listeners[name]=fn;},removeEventListener(name){delete this.listeners[name];}});
  const classes = () => ({add(){},remove(){}});
  const properties = new Map();
  const body=events({style:{setProperty:(k,v)=>properties.set(k,v),removeProperty:k=>properties.delete(k)},classList:classes()});
  let handle;
  const sidebar=events({id:'docs-sidebar',append:e=>handle=e,querySelectorAll:()=>[label],getBoundingClientRect:()=>({left:0,right:400})});
  let parent=sidebar; for(let i=0;i<depth;i++) parent={tagName:'UL',parentElement:parent};
  const label={textContent:'LithoSharp.Configuration.LongNestedType',parentElement:parent,getClientRects:()=>[{}]};
  const media=events({matches:compact});
  const storage=new Map(saved ? [['lithosharp:sidebar-width:/docs/',String(saved)]]:[]);
  const document=events({readyState:'complete',body,documentElement:{lang:'en'},querySelector:s=>s==='[data-docs-sidebar]'?sidebar:s==='.docs-brand'?{href:'https://example.test/docs/index.html'}:null,
    createElement:tag=>tag==='canvas'?{getContext:()=>({measureText:text=>({width:text.length*8})})}:events({classList:classes(),attributes:{},setAttribute(k,v){this.attributes[k]=v;},setPointerCapture(){},remove(){}})});
  const context=vm.createContext({document,matchMedia:()=>media,innerWidth:width,localStorage:{getItem:k=>storage.get(k),setItem:(k,v)=>storage.set(k,v),removeItem:k=>storage.delete(k)},location:{href:'https://example.test/docs/en/intro/'},URL,Symbol,Number,Math,getComputedStyle:()=>({font:'14px Arial',direction:'ltr'}),addEventListener(){},removeEventListener(){}});
  vm.runInContext(source,context);
  return {context,handle,properties,media,storage,label};
}
test('content width and full accessible title are used within viewport limits',()=>{
  const state=setup();assert.equal(state.properties.get('--docs-sidebar-width'),'394px');assert.equal(state.label.title,state.label.textContent);
  assert.equal(state.handle.attributes.role,'separator');assert.equal(state.handle.attributes['aria-controls'],'docs-sidebar');
});
test('keyboard bounds and persisted width survive initialization',()=>{
  const state=setup(); const key=key=>state.handle.listeners.keydown({key,preventDefault(){}});
  key('End');assert.equal(state.properties.get('--docs-sidebar-width'),'560px');key('Home');assert.equal(state.properties.get('--docs-sidebar-width'),'220px');key('ArrowRight');assert.equal(state.properties.get('--docs-sidebar-width'),'236px');
  assert.equal([...state.storage.values()][0],'236');assert.equal(setup({saved:236}).properties.get('--docs-sidebar-width'),'236px');
});
test('compact mode ignores desktop width and viewport bounds preserve the article',()=>{
  const state=setup({compact:true,saved:500,width:390});assert.equal(state.handle.hidden,true);assert.equal(state.properties.has('--docs-sidebar-width'),false);
  const desktop=setup({saved:500,width:900});assert.equal(desktop.properties.get('--docs-sidebar-width'),'378px');
});
test('pointer resizing is clamped and reset restores content-driven width',()=>{
  const state=setup();state.handle.listeners.pointerdown({button:0,pointerId:1,preventDefault(){}});state.handle.listeners.pointermove({pointerId:1,clientX:2000});state.handle.listeners.pointerup({pointerId:1});assert.equal(state.properties.get('--docs-sidebar-width'),'560px');
  state.handle.listeners.dblclick();assert.equal(state.properties.get('--docs-sidebar-width'),'394px');assert.equal(state.storage.size,0);
});

test('deep nesting contributes to automatic width but never exceeds the responsive maximum',()=>{
  const state=setup({depth:8,width:1000});assert.equal(state.properties.get('--docs-sidebar-width'),'420px');assert.equal(state.label.title,state.label.textContent);
});