import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {browserType} from './browser.mjs';
const source = await readFile(new URL('../../../src/LithoSharp/Browser/code-copy.mjs', import.meta.url), 'utf8');
const browser = await browserType.launch();
try {
  for (const mode of ['clipboard','fallback','denied']) {
    const page = await browser.newPage();
    await page.setContent('<html lang="en"><main><pre><span aria-hidden="true">99</span><code><span>let x = &lt;tag&gt;;</span>\n\t日本語 😀 &amp; value\n</code></pre><figure class="mdx-code"><pre><code>MDX owns this</code></pre></figure><div data-ls-no-copy><pre><code>excluded</code></pre></div></main></html>');
    assert.equal(await page.locator('.ls-code-copy').count(),0,'no-JS has no dead controls');
    await page.evaluate(mode => {
      window.copied = [];
      Object.defineProperty(navigator,'clipboard',{configurable:true,value:{writeText:async value=>{if(mode==='clipboard') window.copied.push(value); else throw new Error('Denied');}}});
      document.execCommand = command => { if(command !== 'copy') throw new Error('Unexpected command'); if(mode==='denied') return false; window.copied.push(document.querySelector('textarea').value); return true; };
    },mode);
    await page.addScriptTag({content:source});
    await page.addScriptTag({content:source});
    const button=page.locator('.ls-code-copy');
    assert.equal(await button.count(),1,'idempotent, existing MDX and opted-out blocks excluded');
    await button.focus(); await page.keyboard.press('Enter');
    await page.waitForFunction(()=>document.querySelector('.ls-code-status').textContent.length>0);
    assert.equal(await page.locator('textarea').count(),0,'temporary fallback removed');
    assert.equal(await button.evaluate(el=>el===document.activeElement),true,'focus retained');
    if(mode==='denied') assert.match(await page.locator('.ls-code-status').textContent(),/Copy unavailable/);
    else {
      assert.equal(await page.locator('.ls-code-status').textContent(),'Copied');
      assert.deepEqual(await page.evaluate(()=>window.copied),['let x = <tag>;\n\t日本語 😀 & value\n']);
      await page.keyboard.press('Space');
      await page.waitForFunction(()=>window.copied.length===2);
    }
    await page.evaluate(()=>{ document.querySelector('main').insertAdjacentHTML('beforeend','<pre><code>new page</code></pre>'); document.dispatchEvent(new CustomEvent('lithosharp:page')); });
    assert.equal(await page.locator('.ls-code-copy').count(),2,'new SPA blocks enhanced once');
    await page.close();
  }
  console.log('Code copy: exact text, modern/fallback/denied, focus, Enter/Space, status, no-JS, opt-out, MDX exclusion and repeated page setup passed');
} finally { await browser.close(); }
