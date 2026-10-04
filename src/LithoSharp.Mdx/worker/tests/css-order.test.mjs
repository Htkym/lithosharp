import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtemp,mkdir,writeFile,rm} from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import {createHash} from 'node:crypto';
import {compileSite} from '../compiler.mjs';

const digest=bytes=>createHash('sha256').update(bytes).digest('hex');
function checkedAssets(result){
  const assets=new Map();
  for(const asset of result.assets){assert(!assets.has(asset.path),'Duplicate emitted asset');assert.equal(digest(Buffer.from(asset.bytes,'base64')),asset.hash);assets.set(asset.path,asset)}
  for(const asset of assets.values())for(const reference of asset.imports)assert(assets.has(reference),'Unresolved emitted CSS/JS/font reference: '+reference);
  for(const page of result.pages)for(const reference of [page.entry,...page.css].filter(Boolean))assert(assets.has(reference),'Unresolved page asset: '+reference);
  return assets;
}
function pageCss(result,page){const assets=checkedAssets(result);return page.css.map(file=>Buffer.from(assets.get(file).bytes,'base64').toString()).join('\n')}
function assertThemePriority(result){
  for(const page of result.pages){
    const css=pageCss(result,page),mathBorder=css.search(/\.katex \*\s*\{[^}]*border-color:currentColor/),themeBorder=css.search(/\.mdx-warning,\.mdx-caution\s*\{[^}]*border-color:#ab6900/),mathMargin=css.search(/\.katex-display\s*\{[^}]*margin:1em 0/),themeMargin=css.search(/\.mdx-admonition\s*\{[^}]*margin-block:1rem/);
    assert(mathBorder>=0&&themeBorder>mathBorder,'Theme warning border must follow KaTeX descendant border: '+page.id);
    assert(mathMargin>=0&&themeMargin>mathMargin,'Theme block margin must follow KaTeX display margin: '+page.id);
    assert(css.includes('font-display:block'),'Current KaTeX font descriptor must stay visible');
  }
}

test('emitted CSS preserves theme cascade priority in static, page, selective and shared/custom page closures across cache invalidation',async()=>{
  const root=await mkdtemp(path.join(os.tmpdir(),'lithosharp-css-order-'));
  try{
    const projectRoot=path.join(root,'project'),workRoot=path.join(root,'work');await mkdir(projectRoot);await mkdir(workRoot);
    const sources={
      'static.mdx':'# Static\n\nPlain content.',
      'island.mdx':"import Counter from './Counter.jsx';\n\n# Island\n\n<Island component={Counter} props={{initial:3}} schema={{type:'object',properties:{initial:{type:'integer'}},additionalProperties:false}} strategy=\"load\" />",
      'page.mdx':"import Counter from './Counter.jsx';\n\n# Page\n\n<Counter />",
      'a.mdx':"import Widget from './Widget.jsx';import './a.css';\n\n# A\n\n<div className=\"fixture-a\"><Widget /></div>",
      'b.mdx':"import Widget from './Widget.jsx';import './b.css';\n\n# B\n\n<div className=\"fixture-b\"><Widget /></div>"
    };
    const files={...sources,'Counter.jsx':"import {useState} from 'react';import styles from './counter.module.css';export default function Counter({initial=3}){const[n,set]=useState(initial);return <button className={styles.counter} onClick={()=>set(n+1)}>Count {n}</button>}",'counter.module.css':'.counter{color:navy}','Widget.jsx':"import './shared.css';import styles from './widget.module.css';export default function Widget(){return <strong className={'fixture-shared '+styles.widget}>Shared widget</strong>}",'widget.module.css':'.widget{font-weight:600}','shared.css':'.fixture-shared{color:teal}','a.css':'.fixture-a{outline:1px solid navy}','b.css':'.fixture-b{outline:1px solid maroon}'};
    for(const [file,bytes] of Object.entries(files))await writeFile(path.join(projectRoot,file),bytes);
    const request={projectRoot,workRoot,assetBaseUrl:'/_mdx',basePath:'/',sources,hydration:'selective',cacheable:true,resolutionFingerprint:'css-order-fixture-locked-worker',staticComponents:['Widget'],linkMap:{},pages:Object.keys(sources).map((source,index)=>({id:'css'+index,source,url:'/css'+index+'/',title:source,locale:'en',props:{}}))};
    const check=result=>{
      assert.deepEqual(result.pages.map(page=>page.hydration),['static','selective','page','static','static']);
      assertThemePriority(result);
      const a=pageCss(result,result.pages[3]),b=pageCss(result,result.pages[4]);
      assert(a.includes('.fixture-shared')&&a.includes('.fixture-a')&&!a.includes('.fixture-b'),'A keeps its own and shared CSS only');
      assert(b.includes('.fixture-shared')&&b.includes('.fixture-b')&&!b.includes('.fixture-a'),'B keeps its own and shared CSS only');
    };
    const cold=await compileSite(request);check(cold);assert(cold.bundledPages>0);
    const warm=await compileSite(request);check(warm);assert.equal(warm.bundledPages,0);assert.deepEqual(warm.pages.map(page=>page.css),cold.pages.map(page=>page.css));
    await writeFile(path.join(projectRoot,'shared.css'),'.fixture-shared{color:navy}');
    const changed=await compileSite(request);check(changed);assert(changed.bundledPages>0,'Changed shared stylesheet must invalidate cached browser graph');
    const file=path.join(projectRoot,'shared.css');assert.notEqual(cold.inputs.find(input=>input.file===file)?.hash,changed.inputs.find(input=>input.file===file)?.hash);
    assert(pageCss(changed,changed.pages[3]).includes('.fixture-shared{color:navy}'));assert(pageCss(changed,changed.pages[4]).includes('.fixture-shared{color:navy}'));
    const changedWarm=await compileSite(request);check(changedWarm);assert.equal(changedWarm.bundledPages,0);
  }finally{await rm(root,{recursive:true,force:true})}
});
