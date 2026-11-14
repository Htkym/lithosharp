import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {spawnSync} from 'node:child_process';
import test from 'node:test';
import {checkPublishedPayload,verifyFixedPackage,verifyPublicationReceipt} from './Verify-FixedPair.mjs';

const directory=path.dirname(fileURLToPath(import.meta.url));
const pair=JSON.parse(fs.readFileSync(path.join(directory,'component-pair.json')));
const feed=path.join(directory,'feed');
const packagePath=kind=>path.join(feed,pair.artifacts[kind].fileName);

test('the selected archives retain their exact metadata and Source identity',()=>{
  for(const kind of ['runtime','source'])assert.equal(verifyFixedPackage(packagePath(kind)).kind,kind);
});

test('a preview archive cannot become stable by renaming the selection',()=>{
  if(!pair.componentVersion.includes('-'))return;
  const renamed=structuredClone(pair);renamed.componentVersion='2.0.0';
  for(const a of Object.values(renamed.artifacts)){
    a.componentVersion=renamed.componentVersion;a.fileName=a.packageId+'.'+renamed.componentVersion+'.nupkg';
  }
  assert.throws(()=>verifyFixedPackage(renamed.artifacts.runtime.fileName,renamed,fs.readFileSync(packagePath('runtime'))),/source\/contract manifest differs/);
});

test('missing or historical provenance cannot qualify the new stable pair',()=>{
  const stable={...pair,componentVersion:'2.0.0'};
  const temporary=fs.mkdtempSync(path.join(os.tmpdir(),'published-pair-guard-'));
  try {
    assert.throws(()=>verifyPublicationReceipt(stable,feed,temporary),/requires observed publication provenance/);
    const historical=JSON.parse(fs.readFileSync(path.join(directory,'release-provenance.json')));historical.componentVersion='1.2.0';
    fs.writeFileSync(path.join(temporary,'release-provenance.json'),JSON.stringify(historical));
    assert.throws(()=>verifyPublicationReceipt(stable,feed,temporary),/provenance differs/);
  } finally {
    fs.rmSync(temporary,{recursive:true});
  }
});

test('publication comparison rejects different package payloads',()=>{
  const runtime=fs.readFileSync(packagePath('runtime'));
  checkPublishedPayload(runtime,runtime);
  assert.throws(()=>checkPublishedPayload(runtime,fs.readFileSync(packagePath('source'))),/differs from the sealed pair/);
});

test('stable staging refuses preview input before any download or write',()=>{
  const output=path.join(os.tmpdir(),'unissued-stable-'+process.pid);
  const result=spawnSync(process.execPath,[path.join(directory,'Verify-FixedPair.mjs'),'stage-published-stable','2.0.0-preview.1','1','0'.repeat(64),output],{encoding:'utf8',windowsHide:true});
  assert.notEqual(result.status,0);assert.match(result.stderr,/Require the exact stable Syntamark target/);
  assert.equal(fs.existsSync(output),false);
});
