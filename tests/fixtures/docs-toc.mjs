import {readFileSync} from 'node:fs';
import vm from 'node:vm';
import {test} from 'node:test';
import assert from 'node:assert/strict';
const source=readFileSync(new URL('../../src/LithoSharp/Browser/toc.mjs',import.meta.url),'utf8');
function fixture({short=false,hash='',hasToc=true,hasContent=true}={}) {
  const events=()=>({handlers:new Map(),addEventListener(k,f){if(!this.handlers.has(k))this.handlers.set(k,new Set());this.handlers.get(k).add(f);},removeEventListener(k,f){this.handlers.get(k)?.delete(f);},fire(k){for(const f of this.handlers.get(k)??[])f();}});
  const window=events(),document=events(),frames=new Map(); let next=0,context;
  const headings=[100,600,1200].map((top,i)=>({id:['a','b','c'][i],tagName:'H2',top,getClientRects(){return this.hidden?[]:[{}];},getBoundingClientRect(){return{top:this.top-context.scrollY};},compareDocumentPosition(other){return i<headings.indexOf(other)?4:2;}}));
  const links=headings.map(h=>({attrs:new Map([['href','#'+h.id]]),classList:{toggle(){},remove(){}},getAttribute(k){return this.attrs.get(k);},setAttribute(k,v){this.attrs.set(k,v);},removeAttribute(k){this.attrs.delete(k);}}));
  const article={contains:h=>headings.includes(h),getBoundingClientRect:()=>({top:100-context.scrollY,bottom:(short?700:2000)-context.scrollY,height:short?600:1900})};
  const toc={contains:target=>target===toc||links.includes(target),querySelectorAll:()=>links,setAttribute(){}};const header={bottom:80,getBoundingClientRect(){return{bottom:this.bottom};}};
  document.readyState='complete';document.scrollingElement={scrollHeight:short?900:2100};document.documentElement={};
  const main={...article};
  document.querySelector=s=>s==='main .docs-content'?(hasContent?article:null):s==='main.docs-main'?main:s==='.docs-header'?header:hasToc?toc:null;document.getElementById=id=>headings.find(h=>h.id===id);
  const observers=[];class Observer{constructor(fn){this.fn=fn;observers.push(this);}observe(target){this.target=target;}disconnect(){this.disconnected=true;}}
  context=vm.createContext({document,location:{href:'https://test.invalid/guide/',origin:'https://test.invalid',pathname:'/guide/',search:'',hash},innerHeight:900,scrollY:0,URL,Symbol,getComputedStyle:heading=>({visibility:'visible',scrollMarginTop:heading.margin??'0px'}),ResizeObserver:Observer,MutationObserver:Observer,requestAnimationFrame:fn=>{frames.set(++next,fn);return next;},cancelAnimationFrame:id=>frames.delete(id),addEventListener:window.addEventListener.bind(window),removeEventListener:window.removeEventListener.bind(window)});
  vm.runInContext(source,context);
  const flush=()=>{for(const[id,fn]of [...frames]){frames.delete(id);fn();}};flush();
  return{context,document,window,headings,header,observers,flush,frames,article,main,toc,links,current:()=>links.filter(l=>l.attrs.get('aria-current')==='location').map(l=>l.attrs.get('href'))};
}
test('short page selects the first heading, not the final item',()=>{assert.deepEqual(fixture({short:true}).current(),['#a']);});
test('scroll uses sticky header threshold and selects the final item at the bottom',()=>{const s=fixture();s.context.scrollY=550;s.window.fire('scroll');s.flush();assert.deepEqual(s.current(),['#b']);s.context.scrollY=1200;s.window.fire('scroll');s.flush();assert.deepEqual(s.current(),['#c']);});
test('initial hash and subsequent hash navigation are reflected without changing native links',()=>{const s=fixture({hash:'#b'});assert.deepEqual(s.current(),['#b']);s.context.location.hash='#c';s.window.fire('hashchange');s.flush();assert.deepEqual(s.current(),['#c']);s.context.scrollY=0;s.window.fire('scroll');s.flush();assert.deepEqual(s.current(),['#a']);});
test('history restoration and dynamic layout changes recalculate current heading',()=>{const s=fixture();s.context.scrollY=500;s.window.fire('popstate');s.flush();assert.deepEqual(s.current(),['#a']);s.header.bottom=140;s.observers[0].fn();s.flush();assert.deepEqual(s.current(),['#b']);});
test('SPA cleanup releases observers, listeners and pending frames, then reinitializes once',()=>{const s=fixture();s.window.fire('scroll');s.document.fire('lithosharp:before-navigate');s.flush();assert.deepEqual(s.current(),[]);assert.ok(s.observers.every(o=>o.disconnected));assert.equal(s.window.handlers.get('scroll').size,0);s.document.fire('lithosharp:page');s.flush();assert.deepEqual(s.current(),['#a']);assert.equal(s.window.handlers.get('scroll').size,1);});
test('a page without a document TOC installs no scroll listeners',()=>{const s=fixture({hasToc:false});assert.equal(s.window.handlers.size,0);assert.deepEqual(s.current(),[]);});

test('the final article section is selected before a tall footer reaches document bottom',()=>{const s=fixture();s.document.scrollingElement.scrollHeight=3000;s.context.scrollY=1150;s.window.fire('scroll');s.flush();assert.deepEqual(s.current(),['#c']);});
test('headings hidden in closed disclosures are excluded from current-location selection',()=>{const s=fixture();s.headings[1].hidden=true;s.context.scrollY=550;s.window.fire('scroll');s.flush();assert.deepEqual(s.current(),['#a']);s.headings[1].hidden=false;s.observers[1].fn([{target:s.headings[1]}]);s.flush();assert.deepEqual(s.current(),['#b']);});
test('content article is preferred over its main ancestor',()=>{const s=fixture();assert.equal(s.observers[1].target,s.article);assert.notEqual(s.observers[1].target,s.main);});
test('a TOC inside fallback main cannot reschedule itself through class mutations',()=>{const s=fixture({hasContent:false});assert.equal(s.observers[1].target,s.main);s.observers[1].fn([{target:s.links[0]},{target:s.toc}]);assert.equal(s.frames.size,0);assert.deepEqual(s.current(),['#a']);s.context.scrollY=550;s.observers[1].fn([{target:s.links[0]},{target:s.headings[1]}]);assert.equal(s.frames.size,1);s.flush();assert.deepEqual(s.current(),['#b']);s.observers[1].fn([{target:s.links[1]}]);assert.equal(s.frames.size,0);});
test('native hash landing keeps the heading active when scroll margin exceeds header offset',()=>{const s=fixture();s.headings[1].margin='128px';s.context.location.hash='#b';s.window.fire('hashchange');s.context.scrollY=600-128.2;s.window.fire('scroll');s.flush();assert.deepEqual(s.current(),['#b']);s.window.fire('resize');s.flush();assert.deepEqual(s.current(),['#b']);s.context.scrollY=450;s.window.fire('scroll');s.flush();assert.deepEqual(s.current(),['#a']);});