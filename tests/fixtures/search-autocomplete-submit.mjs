import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
const script = readFileSync(new URL('../../src/LithoSharp/Search/autocomplete.mjs', import.meta.url), 'utf8');
function fixture() {
  const listeners = new Map(), inputListeners = new Map();
  let selected = null, clicked = 0, opened = true;
  const input = {value:'query', getAttribute:()=> 'Search', setAttribute(){}, removeAttribute(){}, addEventListener:(key, fn)=>inputListeners.set(key,fn)};
  const results = {setAttribute(){}, querySelector:()=>selected};
  const status = {}, panel = {};
  const widget = {dataset:{locale:'en'}, isConnected:true,
    querySelector:selector => selector.startsWith('input') ? input : selector === '[data-results]' ? results : selector === '[role=status]' ? status : panel,
    removeAttribute(){opened=false;}, hasAttribute:()=>opened,
    addEventListener:(key,fn)=>listeners.set(key,fn)};
  const document = {querySelectorAll:()=>[widget], addEventListener(){}};
  runInNewContext(script, {document, queryDocuments(){}, URL, location:{href:'https://example.test/',origin:'https://example.test'}});
  return {inputListeners, select(){selected={click(){clicked++;}};opened=true;},
    submit(){let prevented=false;listeners.get('submit')({preventDefault(){prevented=true;}});return {prevented,clicked};}};
}
test('empty, loading, and unavailable suggestions never submit the GET fallback', () => {
  assert.deepEqual(fixture().submit(), {prevented:true,clicked:0});
});
test('a selected suggestion navigates directly rather than submitting the search page', () => {
  const f=fixture();f.select();assert.deepEqual(f.submit(), {prevented:true,clicked:1});
});
test('IME confirmation never submits or follows a stale selected suggestion', () => {
  const f=fixture();f.select();f.inputListeners.get('compositionstart')();
  assert.deepEqual(f.submit(), {prevented:true,clicked:0});
  f.inputListeners.get('compositionend')();assert.deepEqual(f.submit(), {prevented:true,clicked:1});
});
