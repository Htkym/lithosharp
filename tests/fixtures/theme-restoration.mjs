import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';
const shared = readFileSync(new URL('../../src/LithoSharp/Browser/theme-controller.mjs', import.meta.url), 'utf8');
const source = shared + '\n' + readFileSync(new URL('../../src/LithoSharp.Mdx/Browser/theme.js', import.meta.url),'utf8').replace('/*__BASE_PATH__*/',JSON.stringify('/docs/'));
function restore(values, dark, fail=false) {
 const root={dataset:{},style:{}};
 vm.runInNewContext(source,{document:{documentElement:root},matchMedia:()=>({matches:dark}),localStorage:{getItem:key=>{if(fail)throw Error('blocked');return values[key]??null;}}});
 return root;
}
test('saved light beats dark system before modules execute',()=>{assert.equal(restore({'lithosharp:/docs/:theme':'light'},true).dataset.theme,'light');});
test('saved dark aligns core and MDX attributes',()=>{const r=restore({'lithosharp:/docs/:theme':'dark'},false);assert.equal(r.dataset.theme,r.dataset.siteTheme);assert.equal(r.style.colorScheme,'dark');});
test('explicit system overrides legacy saved theme',()=>{const r=restore({'lithosharp:/docs/:theme':'system','lithosharp-theme':'light'},true);assert.equal(r.dataset.theme,'dark');assert.equal(r.dataset.themeMode,'system');});
test('legacy theme remains compatible',()=>assert.equal(restore({'lithosharp-theme':'dark'},false).dataset.theme,'dark'));
test('invalid preference uses system',()=>assert.equal(restore({'lithosharp:/docs/:theme':'bad'},true).dataset.theme,'dark'));
test('blocked storage still resolves system',()=>assert.equal(restore({},true,true).dataset.theme,'dark'));
const siteSource = readFileSync(new URL('../../site/website-theme.js', import.meta.url),'utf8');
test('static API bootstrap uses same base-scoped preference',()=>{
 const root={dataset:{},style:{}};
 vm.runInNewContext(shared + "\n" + siteSource,{document:{documentElement:root,currentScript:{dataset:{basePath:'/docs/'}}},matchMedia:()=>({matches:false}),localStorage:{getItem:key=>key==='lithosharp:/docs/:theme'?'dark':null}});
 assert.equal(root.dataset.theme,'dark'); assert.equal(root.dataset.siteTheme,'dark');
});
test('theme restoration has a distinct generated asset identity from CSS',()=>{
 const generator=readFileSync(new URL('../../src/LithoSharp.Mdx/DocumentationSite.cs',import.meta.url),'utf8');
 assert.match(generator,/Asset\("theme-restoration", "\.js", ThemeScript\.BrowserScript \+ "\\n" \+ Resource\("theme\.js"\)/);
 assert.match(generator,/Asset\("theme", "\.css"/);
 assert.doesNotMatch(generator,/Asset\("theme", "\.js"/);
});
function runtime(values = {}, dark = false, blocked = false) {
 const root = {dataset:{},style:{}};
 const buttons = ['data-ls-theme','data-site-theme-toggle'].map(kind => ({kind, dataset:{}, hidden:true, hasAttribute(k){return k===kind;}, setAttribute(k,v){this[k]=v;}, removeAttribute(k){delete this[k];}}));
 const listeners={}, mediaListeners={}, writes=[];
 const media={matches:dark,addEventListener:(key,fn)=>mediaListeners[key]=fn};
 const context=vm.createContext({document:{documentElement:root, querySelectorAll:()=>buttons,addEventListener:(key,fn)=>(listeners[key]??=[]).push(fn),dispatchEvent(){}},matchMedia:()=>media, CustomEvent:class {constructor(type,init){Object.assign(this,{type,...init});}},localStorage:{getItem:key=>{if(blocked)throw Error('blocked');return values[key]??null;},setItem:(key,value)=>{if(blocked)throw Error('blocked');writes.push([key,value]);values[key]=value;}}});
 vm.runInContext(shared,context);
 const invoke = code => vm.runInContext(`globalThis[Symbol.for('lithosharp.theme')].${code}`,context);
 const click = index => listeners.click.forEach(fn=>fn({target:{closest:()=>buttons[index]}}));
 return {root,buttons,listeners,mediaListeners,media,writes,context,invoke,click};
}
test('shared controller cycles MDX modes, persists scoped values, and shares the same three-state core toggle',()=>{
 const r=runtime();r.invoke("start('/docs/')");
 assert.equal(r.buttons[0].hidden,false);
 r.click(0);assert.equal(r.root.dataset.themeMode,'light');
 r.click(0);assert.equal(r.root.dataset.themeMode,'dark');
 r.click(0);assert.equal(r.root.dataset.themeMode,'system');
 assert.deepEqual(r.writes,[['lithosharp:/docs/:theme','light'],['lithosharp:/docs/:theme','dark'],['lithosharp:/docs/:theme','system']]);
 r.click(1);assert.equal(r.root.dataset.themeMode,'light');assert.equal(r.buttons[1]['aria-pressed'],undefined);
 r.click(1);assert.equal(r.root.dataset.themeMode,'dark');
 r.click(1);assert.equal(r.root.dataset.themeMode,'system');assert.equal(r.buttons[1]['aria-label'],'Theme: system');
});
test('multiple bundles and page starts install only one click and system listener',()=>{
 const r=runtime();r.invoke("start('/docs/')");vm.runInContext(shared,r.context);r.invoke("start('/docs/')");
 assert.equal(r.listeners.click.length,1);r.click(0);assert.equal(r.root.dataset.themeMode,'light');
 r.invoke("start('/docs/', (key, fallback) => key === 'theme' ? 'テーマ' : fallback)");
 assert.equal(r.buttons[0]['aria-label'],'テーマ: light');
});
test('system changes follow automatic mode, not explicit mode, and blocked storage stays usable',()=>{
 const r=runtime({},false,true);r.invoke("start('/docs/')");r.media.matches=true;r.mediaListeners.change();assert.equal(r.root.dataset.theme,'dark');
 r.click(0);r.mediaListeners.change();assert.equal(r.root.dataset.theme,'light');
});
test('distinct site base paths do not inherit another scoped setting',()=>{
 const r=runtime({'lithosharp:/one/:theme':'dark','lithosharp:/two/:theme':'light'});
 r.invoke("start('/one/')");assert.equal(r.root.dataset.theme,'dark');
 r.invoke("start('/two/')");assert.equal(r.root.dataset.theme,'light');
});
