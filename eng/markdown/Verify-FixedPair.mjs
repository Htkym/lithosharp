// Consumer gate only. Canonical source, candidate issuance and pack recipes are owned by the component repository.
import fs from 'node:fs';
import path from 'node:path';
import {createHash} from 'node:crypto';
import {inflateRawSync} from 'node:zlib';
import {fileURLToPath} from 'node:url';

const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
const [action,...args]=process.argv.slice(2);
const component='src/Syntamark/';
const stamp=component+'Portable/MarkdownParserVersion.g.cs';
const ids=['Syntamark','Syntamark.Source'];
const repository='https://github.com/Htkym/syntamark';
const api='https://api.github.com/repos/Htkym/syntamark';
const stable=v=>/^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.test(v);
const requiresPublication=v=>stable(v)&&!['1.0.0','1.2.0'].includes(v);
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
  const b=Buffer.isBuffer(p)?p:fs.readFileSync(p),out=new Map(),names=new Set();assert(b.length>=22,'ZIP too short');let e=b.length-22;
  while(e>=Math.max(0,b.length-65557)&&b.readUInt32LE(e)!==0x06054b50)e--;
  assert(e>=Math.max(0,b.length-65557),'ZIP directory missing');
  const count=b.readUInt16LE(e+10);assert(count<=4096,'ZIP entry limit exceeded');let at=b.readUInt32LE(e+16),total=0;
  for(let i=0;i<count;i++){
    assert(b.readUInt32LE(at)===0x02014b50,'Invalid ZIP entry');
    const flags=b.readUInt16LE(at+8),method=b.readUInt16LE(at+10),compressed=b.readUInt32LE(at+20);
    const size=b.readUInt32LE(at+24),nl=b.readUInt16LE(at+28),xl=b.readUInt16LE(at+30),cl=b.readUInt16LE(at+32);
    const name=b.subarray(at+46,at+46+nl).toString('utf8'),local=b.readUInt32LE(at+42);
    assert(!name.startsWith('/')&&!name.includes('\\')&&!name.includes(':')&&!name.split('/').includes('..')&&!out.has(name),'Unsafe/duplicate ZIP path');
    const normalized=name.normalize('NFC').toLowerCase();assert(!names.has(normalized),'Case/normalization duplicate ZIP path');names.add(normalized);
    assert((flags&1)===0&&[0,8].includes(method)&&size<8*1024*1024,'Unsupported ZIP format/size');
    total+=size;assert(total<=64*1024*1024,'ZIP inflated-byte limit exceeded');
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
export function verifyFixedPackage(packagePath,m=json(path.join(root,'eng/markdown/component-pair.json')),bytes=fs.readFileSync(packagePath)){
  assert((/^(?:1\.(?:0|2)\.0|2\.0\.0-preview\.[1-9][0-9]*)$/.test(m.componentVersion)||stable(m.componentVersion))&&/^[0-9a-f]{40}$/.test(m.sourceCommit),'Invalid fixed pair identity');
  assert(/^[0-9a-f]{64}$/.test(m.canonicalSourceHash)&&m.sourceRepository.url===repository,'Invalid fixed pair source');
  assert(m.parserVersion==='1/'+m.canonicalSourceHash,'Canonical parser identity mismatch');
  const kind=Object.keys(m.artifacts).find(k=>path.basename(packagePath)===m.artifacts[k].fileName);
  assert(kind,'Unexpected fixed component package');const a=m.artifacts[kind];
  assert(a.packageId===ids[kind==='runtime'?0:1]&&a.componentVersion===m.componentVersion,'Fixed package identity mismatch');
  assert(bytes.length===a.byteLength&&sha(bytes)===a.sha256,'Fixed artifact bytes/hash mismatch: '+a.fileName);
  const z=zip(bytes),inside=JSON.parse(z.get('markdown/manifest.json').toString());
  const {state,artifacts,...selected}=m;
  assert(JSON.stringify(inside)===JSON.stringify(selected),'Fixed package source/contract manifest differs');
  assert([...z.keys()].filter(p=>p.toLowerCase().endsWith('.nuspec')).length===1,'Require exactly one root package nuspec');
  const nuspec=z.get(a.packageId+'.nuspec')?.toString();
  assert(nuspec&&!nuspec.includes('<!')&&nuspec.includes('<id>'+a.packageId+'</id>')&&nuspec.includes('<version>'+m.componentVersion+'</version>'),'Package nuspec identity differs');
  const attributes=Object.fromEntries([...nuspec.matchAll(/<repository\s+([^>]+)\/?\s*>/g)].flatMap(e=>[...e[1].matchAll(/(type|url|commit)="([^"]*)"/g)].map(a=>[a[1],a[2]])));
  assert(attributes.type==='git'&&attributes.url===repository&&attributes.commit===m.sourceCommit,'Package repository metadata differs');
  if(m.contractVersion==='2.0')assert(!/<dependency\s/.test(nuspec),'Native parser package must have no package dependencies');
  checkLicenses(z,m);
  if(kind==='runtime')assert(sha(z.get('lib/net10.0/Syntamark.dll'))===a.assemblySha256,'Fixed runtime DLL mismatch');
  else {
    const entries=m.canonicalFiles.map(e=>{
      assert(e.path.startsWith(component)&&e.path!==stamp&&!e.path.split('/').includes('..'),'Invalid canonical path');
      const p=e.path.startsWith(component+'Portable/')?'src/'+e.path.slice(component.length):'contracts/'+path.basename(e.path);
      const bytes=z.get(p);assert(bytes&&bytes.length===e.byteLength&&sha(bytes)===e.sha256,'Fixed source payload differs: '+p);
      return {path:e.path,bytes};
    }).sort((a,b)=>Buffer.compare(Buffer.from(a.path),Buffer.from(b.path)));
    assert(hashFiles(entries)===m.canonicalSourceHash,'Fixed source canonical hash differs');
    assert(sha(z.get(m.generatedStamp.packagePath))===m.generatedStamp.sha256,'Fixed source stamp differs');
    assert(z.get(m.generatedStamp.packagePath).toString().includes('const string Value = "'+m.parserVersion+'"'),'Source parser stamp identity differs');
    const targets=z.get('build/Syntamark.Source.targets')?.toString();
    assert(targets?.includes('<SyntamarkSourcePackageVersion>'+m.componentVersion+'</SyntamarkSourcePackageVersion>')&&targets.includes('<SyntamarkSourceParserVersion>'+m.parserVersion+'</SyntamarkSourceParserVersion>'),'Source build target identity differs');
    const expected=m.canonicalFiles.filter(e=>e.path.startsWith(component+'Portable/')).map(e=>'src/'+e.path.slice(component.length)).concat(m.generatedStamp.packagePath).sort();
    assert(JSON.stringify([...z.keys()].filter(p=>p.toLowerCase().endsWith('.cs')).sort())===JSON.stringify(expected),'Unexpected source Compile input');
    assert(![...z.keys()].some(p=>p.startsWith('lib/')||p.startsWith('buildTransitive/')||p.startsWith('contentFiles/')||p.includes('/Public/')||p.includes('/Properties/')||p.endsWith('AssemblyInfo.cs')),'Forbidden source payload');
  }
  return {kind,...a,canonicalSourceHash:m.canonicalSourceHash,parserVersion:m.parserVersion};
}
function requireStableContract(m){
  assert(stable(m.componentVersion)&&m.componentVersion===json(path.join(root,'eng/version-matrix.json')).syntamarkTarget,'Require the exact stable Syntamark target');
  assert(m.state==='PACKED_NOT_HOST_VERIFIED'&&m.contractVersion==='2.0'&&m.yamlProfileId==='native-yaml/1'&&m.dependencies.length===0,'Stable pair must retain the sealed native YAML contract');
  assert(Object.keys(m.artifacts).sort().join(',')==='runtime,source','Stable pair must contain exactly runtime and Source');
}
const nugetUrl=(id,v)=>`https://api.nuget.org/v3-flatcontainer/${id.toLowerCase()}/${v}/${id.toLowerCase()}.${v}.nupkg`;
export function checkPublishedPayload(sealed,published){
  const expected=zip(sealed),actual=zip(published);
  // Signature presence is not signature validation; compare every original payload entry.
  if(!expected.has('.signature.p7s'))actual.delete('.signature.p7s');
  assert(expected.size===actual.size,'Published package entry set differs from the sealed pair');
  for(const [name,bytes] of expected)assert(actual.has(name)&&bytes.equals(actual.get(name)),'Published payload differs from the sealed pair: '+name);
}
function selectPublishedPair(archive,published,manifestHash){
  const entries=zip(archive),manifest=entries.get('pair-manifest.json');
  assert(manifest&&sha(manifest)===manifestHash,'Reviewed producer manifest bytes differ');
  const sealed=JSON.parse(manifest.toString());requireStableContract(sealed);
  const artifacts={},evidence={};
  for(const [kind,a] of Object.entries(sealed.artifacts)){
    assert(a.fileName===a.packageId+'.'+sealed.componentVersion+'.nupkg','Sealed artifact filename differs');
    const original=entries.get('feed/'+a.fileName),bytes=published[kind];
    assert(original&&bytes,'Published runtime/Source pair missing');
    verifyFixedPackage(a.fileName,sealed,original);checkPublishedPayload(original,bytes);
    artifacts[kind]={...a,sha256:sha(bytes),byteLength:bytes.length};
    evidence[kind]={packageId:a.packageId,sealedReleaseSha256:a.sha256,sealedReleaseByteLength:a.byteLength,
      nugetUrl:nugetUrl(a.packageId,sealed.componentVersion),nugetSha256:sha(bytes),nugetByteLength:bytes.length,
      packagePayloadMatchesSealedRelease:true};
  }
  const selected={...sealed,artifacts};
  for(const [kind,a] of Object.entries(artifacts))verifyFixedPackage(a.fileName,selected,published[kind]);
  return {selected,evidence};
}
export function verifyPublicationReceipt(m,feed,directory=path.join(root,'eng/markdown')){
  requireStableContract(m);
  const receiptPath=path.join(directory,'release-provenance.json');
  assert(fs.existsSync(receiptPath),'Stable pair requires observed publication provenance');
  const p=json(receiptPath);
  assert(p.scope==='PUBLISHED_FORMAL_PAIR'&&p.published===true&&p.componentVersion===m.componentVersion&&p.sourceCommit===m.sourceCommit&&p.sourceRepository===repository,'Stable publication provenance differs from selected pair');
  assert(/^[1-9]\d*$/.test(String(p.producerRunId))&&/^[1-9]\d*$/.test(String(p.producerArtifactId))&&p.producerRunUrl===repository+'/actions/runs/'+p.producerRunId,'Invalid official producer origin');
  assert(p.producerArtifactName==='main-markdown-pair-'+m.componentVersion+'-'+m.sourceCommit,'Producer artifact identity differs');
  const archive=fs.readFileSync(path.join(directory,'publication-artifact.zip'));
  assert(sha(archive)===p.producerArtifactSha256,'Raw producer artifact archive differs');
  const published=Object.fromEntries(Object.entries(m.artifacts).map(([kind,a])=>[kind,fs.readFileSync(path.join(feed,a.fileName))]));
  const {selected,evidence}=selectPublishedPair(archive,published,p.sealedManifestSha256);
  assert(JSON.stringify(selected)===JSON.stringify(m)&&JSON.stringify(evidence)===JSON.stringify(p.artifacts),'Published pair bytes or provenance differ');
  assert(sha(fs.readFileSync(path.join(directory,'component-pair.json')))===p.pairManifestSha256&&p.parserVersion===m.parserVersion&&p.canonicalSourceHash===m.canonicalSourceHash,'Publication manifest/parser binding differs');
  return p;
}
async function download(url,archive=false){
  const headers={'User-Agent':'LithoSharp-published-pair-verifier',Accept:archive?'application/octet-stream':'application/vnd.github+json'};
  if(url.startsWith(api+'/')&&(process.env.GH_TOKEN||process.env.GITHUB_TOKEN))headers.Authorization='Bearer '+(process.env.GH_TOKEN||process.env.GITHUB_TOKEN);
  const response=await fetch(url,{headers});
  assert(response.ok,'Official publication unavailable: '+url+' HTTP '+response.status);
  const chunks=[];let size=0;
  for await(const chunk of response.body){size+=chunk.length;assert(size<=16*1024*1024,'Official publication exceeds archive limit');chunks.push(chunk);}
  return Buffer.concat(chunks);
}
async function producerRun(id,m){
  assert(/^[1-9]\d*$/.test(String(id)),'Require one actual producer run ID');
  const run=JSON.parse((await download(api+'/actions/runs/'+id)).toString());
  assert(run.conclusion==='success'&&run.event==='workflow_dispatch'&&run.path==='.github/workflows/component.yml'&&run.head_sha===m.sourceCommit&&run.head_branch==='main'&&Number.isSafeInteger(run.run_attempt)&&run.run_attempt>0&&run.display_title==='Formal Markdown pair '+m.componentVersion,'Official producer run does not qualify this stable pair');
  const ref=JSON.parse((await download(api+'/git/ref/tags/v'+m.componentVersion)).toString());
  assert(ref.ref==='refs/tags/v'+m.componentVersion,'Official stable source tag missing');
  let object=ref.object,depth=0;
  while(object.type==='tag'){
    assert(++depth<=8&&/^[0-9a-f]{40}$/.test(object.sha),'Invalid annotated stable tag');
    object=JSON.parse((await download(api+'/git/tags/'+object.sha)).toString()).object;
  }
  assert(object.type==='commit'&&object.sha===m.sourceCommit,'Official stable source tag differs from sealed pair');
  return run.run_attempt;
}
async function publishedPackages(m){
  return Object.fromEntries(await Promise.all(Object.entries(m.artifacts).map(async([kind,a])=>[kind,await download(nugetUrl(a.packageId,m.componentVersion),true)])));
}
async function stagePublishedStable(version,runId,manifestHash,out){
  assert(stable(version)&&version===json(path.join(root,'eng/version-matrix.json')).syntamarkTarget,'Require the exact stable Syntamark target');
  assert(/^[1-9]\d*$/.test(runId)&&/^[0-9a-f]{64}$/.test(manifestHash),'Require the actual producer run and reviewed sealed manifest hash');
  out=path.resolve(out);assert(!fs.existsSync(out),'Never overwrite a stage or the active fixed pair');
  const listing=JSON.parse((await download(api+'/actions/runs/'+runId+'/artifacts?per_page=100')).toString());
  const matches=listing.artifacts.filter(a=>a.name.startsWith('main-markdown-pair-'+version+'-'));
  assert(matches.length===1&&!matches[0].expired,'Require the actual unexpired formal producer artifact');
  const artifact=matches[0],archive=await download(api+'/actions/artifacts/'+artifact.id+'/zip',true);
  if(artifact.digest)assert(artifact.digest==='sha256:'+sha(archive),'Official producer artifact digest differs');
  const sealed=JSON.parse(zip(archive).get('pair-manifest.json')?.toString()??'null');
  assert(sealed?.componentVersion===version,'Official producer artifact is not the requested stable pair');requireStableContract(sealed);
  assert(artifact.name==='main-markdown-pair-'+version+'-'+sealed.sourceCommit,'Official producer artifact name differs');
  const producerRunAttempt=await producerRun(runId,sealed);
  const published=await publishedPackages(sealed),{selected,evidence}=selectPublishedPair(archive,published,manifestHash);
  const manifest=JSON.stringify(selected,null,2)+'\n';
  const provenance={scope:'PUBLISHED_FORMAL_PAIR',published:true,checkedAt:new Date().toISOString(),componentVersion:version,
    sourceRepository:repository,sourceCommit:sealed.sourceCommit,producerRunId:runId,producerRunAttempt,producerRunUrl:repository+'/actions/runs/'+runId,
    producerArtifactId:String(artifact.id),producerArtifactName:artifact.name,producerArtifactSha256:sha(archive),
    sealedManifestSha256:manifestHash,pairManifestPath:'eng/markdown/component-pair.json',pairManifestSha256:sha(manifest),
    canonicalSourceHash:sealed.canonicalSourceHash,parserVersion:sealed.parserVersion,consumerAcceptance:'NOT_RUN',artifacts:evidence};
  fs.mkdirSync(path.join(out,'feed'),{recursive:true});
  const write=(p,bytes)=>fs.writeFileSync(path.join(out,p),bytes,{flag:'wx'});
  for(const [kind,a] of Object.entries(selected.artifacts))write('feed/'+a.fileName,published[kind]);
  write('component-pair.json',manifest);write('publication-artifact.zip',archive);write('release-provenance.json',JSON.stringify(provenance,null,2)+'\n');
  verifyPublicationReceipt(selected,path.join(out,'feed'),out);
  console.log(JSON.stringify({state:'STAGED_NOT_ACCEPTED',componentVersion:version,output:out,consumerAcceptance:'NOT_RUN'}));
}
async function verifyPublishedStable(feed){
  const m=json(path.join(root,'eng/markdown/component-pair.json')),p=verifyPublicationReceipt(m,feed);
  // Ingestion captures the official producer archive once; Actions artifact retention must not break later releases.
  await producerRun(p.producerRunId,m);
  const published=await publishedPackages(m);
  for(const [kind,a] of Object.entries(m.artifacts))assert(published[kind].length===a.byteLength&&sha(published[kind])===a.sha256,'Selected raw archive differs from official NuGet.org: '+a.packageId);
  console.log(JSON.stringify({state:'OFFICIAL_STABLE_PUBLICATION_PASS',componentVersion:m.componentVersion,consumerAcceptance:'NOT_RUN'}));
}
function verifyFixed(feed){
  const m=json(path.join(root,'eng/markdown/component-pair.json'));
  if(requiresPublication(m.componentVersion))verifyPublicationReceipt(m,feed);
  const expected=Object.values(m.artifacts).map(a=>a.fileName).sort();
  const actual=fs.readdirSync(feed).filter(p=>p.endsWith('.nupkg')).sort();
  assert(expected.every(p=>actual.includes(p)),'Selected immutable pair missing from feed');
  // Historical immutable versions may remain; consumers restore the exact selected version.
  const verified=expected.map(p=>verifyFixedPackage(path.join(feed,p)));
  console.log(JSON.stringify({state:'FIXED_PAIR_BYTES_SOURCE_PASS',componentVersion:m.componentVersion,verified}));
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url)){
  if(action==='verify-fixed'&&args.length===1)verifyFixed(args[0]);
  else if(action==='verify-fixed-package'&&args.length===1){
    const m=json(path.join(root,'eng/markdown/component-pair.json'));
    if(requiresPublication(m.componentVersion))verifyPublicationReceipt(m,path.dirname(args[0]));
    console.log(JSON.stringify({state:'FIXED_PACKAGE_BYTES_SOURCE_PASS',verified:verifyFixedPackage(args[0])}));
  }
  else if(action==='stage-published-stable'&&args.length===4)await stagePublishedStable(...args);
  else if(action==='verify-published-stable'&&args.length===1)await verifyPublishedStable(args[0]);
  else throw Error('Usage: Verify-FixedPair.mjs verify-fixed <feed>; verify-fixed-package <nupkg>; stage-published-stable <stable version> <producer run ID> <reviewed sealed manifest SHA256> <new stage>; verify-published-stable <feed>');
}
