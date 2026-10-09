// Consumer gate only. Canonical source, candidate issuance and pack recipes are owned by the component repository.
import fs from 'node:fs';
import path from 'node:path';
import {createHash} from 'node:crypto';
import {inflateRawSync} from 'node:zlib';
import {fileURLToPath} from 'node:url';

const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
const [action,...args]=process.argv.slice(2);
const component='src/LithoSharp.Markdown/';
const stamp=component+'Portable/MarkdownParserVersion.g.cs';
const ids=['LithoSharp.Markdown','LithoSharp.Markdown.Source'];
const sha=b=>createHash('sha256').update(b).digest('hex');
const json=p=>JSON.parse(fs.readFileSync(p,'utf8'));
function assert(ok,why){if(!ok)throw Error(why);}
function hashFiles(entries){
  const h=createHash('sha256');
  for(const {path:p,bytes} of entries){
    const pb=Buffer.from(p,'utf8'),n=Buffer.alloc(8);
    n.writeBigUInt64BE(BigInt(pb.length));h.update(n);h.update(pb);
    n.writeBigUInt64BE(BigInt(bytes.length));h.update(n);h.update(bytes);
  }
  return h.digest('hex');
}
function zip(p){
  const b=fs.readFileSync(p),out=new Map();assert(b.length>=22,'ZIP too short');let e=b.length-22;
  while(e>=Math.max(0,b.length-65557)&&b.readUInt32LE(e)!==0x06054b50)e--;
  assert(e>=Math.max(0,b.length-65557),'ZIP directory missing');
  const count=b.readUInt16LE(e+10);let at=b.readUInt32LE(e+16);
  for(let i=0;i<count;i++){
    assert(b.readUInt32LE(at)===0x02014b50,'Invalid ZIP entry');
    const flags=b.readUInt16LE(at+8),method=b.readUInt16LE(at+10),compressed=b.readUInt32LE(at+20);
    const size=b.readUInt32LE(at+24),nl=b.readUInt16LE(at+28),xl=b.readUInt16LE(at+30),cl=b.readUInt16LE(at+32);
    const name=b.subarray(at+46,at+46+nl).toString('utf8'),local=b.readUInt32LE(at+42);
    assert(!name.startsWith('/')&&!name.includes('\\')&&!name.includes(':')&&!name.split('/').includes('..')&&!out.has(name),'Unsafe/duplicate ZIP path');
    assert((flags&1)===0&&[0,8].includes(method)&&size<8*1024*1024,'Unsupported ZIP format/size');
    assert(b.readUInt32LE(local)===0x04034b50,'Invalid ZIP local entry');
    const start=local+30+b.readUInt16LE(local+26)+b.readUInt16LE(local+28),data=b.subarray(start,start+compressed);
    const bytes=method===8?inflateRawSync(data,{maxOutputLength:8*1024*1024}):data;
    assert(bytes.length===size,'ZIP entry length mismatch');out.set(name,bytes);
    at+=46+nl+xl+cl;
  }
  return out;
}
function checkLicenses(z,metadata){
  for(const license of metadata.licenses)
    assert(z.has(license.packagePath)&&sha(z.get(license.packagePath))===license.sha256,'License mismatch: '+license.packagePath);
}
function verifyFixedPackage(packagePath){
  const m=json(path.join(root,'eng/markdown/component-pair.json'));
  assert(/^2\.0\.0-preview\.[1-9][0-9]*$/.test(m.componentVersion)&&/^[0-9a-f]{40}$/.test(m.sourceCommit),'Invalid fixed pair identity');
  assert(m.parserVersion==='1/'+m.canonicalSourceHash,'Canonical parser identity mismatch');
  const kind=Object.keys(m.artifacts).find(k=>path.basename(packagePath)===m.artifacts[k].fileName);
  assert(kind,'Unexpected fixed component package');const a=m.artifacts[kind],bytes=fs.readFileSync(packagePath);
  assert(a.packageId===ids[kind==='runtime'?0:1]&&a.componentVersion===m.componentVersion,'Fixed package identity mismatch');
  assert(bytes.length===a.byteLength&&sha(bytes)===a.sha256,'Fixed artifact bytes/hash mismatch: '+a.fileName);
  const z=zip(packagePath),inside=JSON.parse(z.get('markdown/manifest.json').toString());
  const {state,artifacts,...selected}=m;
  assert(JSON.stringify(inside)===JSON.stringify(selected),'Fixed package source/contract manifest differs');
  checkLicenses(z,m);
  if(kind==='runtime')assert(sha(z.get('lib/net10.0/LithoSharp.Markdown.dll'))===a.assemblySha256,'Fixed runtime DLL mismatch');
  else {
    const entries=m.canonicalFiles.map(e=>{
      assert(e.path.startsWith(component)&&e.path!==stamp&&!e.path.split('/').includes('..'),'Invalid canonical path');
      const p=e.path.startsWith(component+'Portable/')?'src/'+e.path.slice(component.length):'contracts/'+path.basename(e.path);
      const bytes=z.get(p);assert(bytes&&bytes.length===e.byteLength&&sha(bytes)===e.sha256,'Fixed source payload differs: '+p);
      return {path:e.path,bytes};
    }).sort((a,b)=>Buffer.compare(Buffer.from(a.path),Buffer.from(b.path)));
    assert(hashFiles(entries)===m.canonicalSourceHash,'Fixed source canonical hash differs');
    assert(sha(z.get(m.generatedStamp.packagePath))===m.generatedStamp.sha256,'Fixed source stamp differs');
    const expected=m.canonicalFiles.filter(e=>e.path.startsWith(component+'Portable/')).length+1;
    assert([...z.keys()].filter(p=>p.endsWith('.cs')).length===expected,'Unexpected source Compile input');
    assert(![...z.keys()].some(p=>p.startsWith('lib/')||p.startsWith('buildTransitive/')||p.startsWith('contentFiles/')||p.includes('/Public/')||p.includes('/Properties/')||p.endsWith('AssemblyInfo.cs')),'Forbidden source payload');
  }
  return {kind,...a,canonicalSourceHash:m.canonicalSourceHash,parserVersion:m.parserVersion};
}
function verifyFixed(feed){
  const m=json(path.join(root,'eng/markdown/component-pair.json'));
  const expected=Object.values(m.artifacts).map(a=>a.fileName).sort();
  const actual=fs.readdirSync(feed).filter(p=>p.endsWith('.nupkg')).sort();
  assert(expected.every(p=>actual.includes(p)),'Selected immutable pair missing from feed');
  // Historical immutable versions may remain; consumers restore the exact selected version.
  const verified=expected.map(p=>verifyFixedPackage(path.join(feed,p)));
  console.log(JSON.stringify({state:'FIXED_PAIR_BYTES_SOURCE_PASS',componentVersion:m.componentVersion,verified}));
}
if(action==='verify-fixed'&&args.length===1)verifyFixed(args[0]);
else if(action==='verify-fixed-package'&&args.length===1)console.log(JSON.stringify({state:'FIXED_PACKAGE_BYTES_SOURCE_PASS',verified:verifyFixedPackage(args[0])}));
else throw Error('Usage: Verify-FixedPair.mjs verify-fixed <feed>; verify-fixed-package <nupkg>');
