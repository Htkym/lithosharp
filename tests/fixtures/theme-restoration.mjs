import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';
const source = readFileSync(new URL('../../src/LithoSharp.Mdx/Browser/theme.js', import.meta.url),'utf8').replace('/*__BASE_PATH__*/',JSON.stringify('/docs/'));
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
 vm.runInNewContext(siteSource,{document:{documentElement:root,currentScript:{dataset:{basePath:'/docs/'}}},matchMedia:()=>({matches:false}),localStorage:{getItem:key=>key==='lithosharp:/docs/:theme'?'dark':null}});
 assert.equal(root.dataset.theme,'dark'); assert.equal(root.dataset.siteTheme,'dark');
});
test('theme restoration has a distinct generated asset identity from CSS',()=>{
 const generator=readFileSync(new URL('../../src/LithoSharp.Mdx/DocumentationSite.cs',import.meta.url),'utf8');
 assert.match(generator,/Asset\("theme-restoration", "\.js", Resource\("theme\.js"\)/);
 assert.match(generator,/Asset\("theme", "\.css"/);
 assert.doesNotMatch(generator,/Asset\("theme", "\.js"/);
});
const website = readFileSync(new URL('../../site/website.mjs',import.meta.url),'utf8');
const controller = website.slice(website.indexOf('// The documentation runtime owns'), website.indexOf('const initialized ='));
test('utility theme controller reveals switch and cycles three states using the same key',()=>{
 const root={dataset:{themeMode:'system'},style:{}};
 const button={hidden:true,setAttribute(k,v){this[k]=v;}};
 const listeners={},saved=[];
 vm.runInNewContext(controller,{document:{documentElement:root,querySelector:s=>s.includes('_docs')?null:{dataset:{basePath:'/docs/'}},querySelectorAll:()=>[button],addEventListener:(k,f)=>listeners[k]=f},matchMedia:()=>({matches:false,addEventListener(){}}),localStorage:{setItem:(...v)=>saved.push(v)}});
 assert.equal(button.hidden,false);
 const event={target:{closest:()=>button}};
 listeners.click(event);assert.equal(root.dataset.themeMode,'light');
 listeners.click(event);assert.equal(root.dataset.themeMode,'dark');
 listeners.click(event);assert.equal(root.dataset.themeMode,'system');
 assert.deepEqual(saved,[['lithosharp:/docs/:theme','light'],['lithosharp:/docs/:theme','dark'],['lithosharp:/docs/:theme','system']]);
});
