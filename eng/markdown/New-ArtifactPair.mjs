// Explicit stages only: prepare performs Git/file I/O; seal/verify-restored follow authorized dotnet work.
import fs from 'node:fs';
import path from 'node:path';
import {createHash} from 'node:crypto';
import {execFileSync} from 'node:child_process';
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
function git(args){return execFileSync('git',args,{cwd:root,windowsHide:true,maxBuffer:4*1024*1024});}
function blob(commit,p){return git(['show',commit+':'+p]);}
function write(p,b){fs.mkdirSync(path.dirname(p),{recursive:true});fs.writeFileSync(p,b,{flag:'wx'});}
function writeJson(p,v){write(p,JSON.stringify(v,null,2)+'\n');}
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
function prepare(commit,version,out){
  assert(/^[0-9a-f]{40}$/.test(commit),'Require one full source commit');
  assert(/^2\.0\.0-preview\.[1-9][0-9]*$/.test(version),'Require one new immutable 2.0.0-preview.N');
  out=path.resolve(out);assert(!fs.existsSync(out),'Never overwrite an artifact stage; choose new output/version');
  assert(git(['rev-parse',commit+'^{commit}']).toString().trim()===commit,'Source commit mismatch');
  const all=git(['ls-tree','-r','--name-only',commit,'--',component]).toString().trim().split('\n');
  const selected=all.filter(p=>(p.startsWith(component+'Portable/')&&p.endsWith('.cs')&&p!==stamp)||p.startsWith(component+'Contracts/'));
  selected.sort((a,b)=>Buffer.compare(Buffer.from(a),Buffer.from(b)));
  const entries=selected.map(p=>({path:p,bytes:blob(commit,p)})),hash=hashFiles(entries);
  const stampBytes=blob(commit,stamp),parserVersion='1/'+hash;
  assert(stampBytes.toString().includes('"'+parserVersion+'"'),'Canonical hash differs from the selected Git stamp');
  const dependencies=JSON.parse(blob(commit,component+'Contracts/markdown-dependencies.json'));
  assert(JSON.stringify(dependencies.dependencies)===JSON.stringify({YamlDotNet:'18.1.0'}),'Unexpected portable dependencies');
  const metadata={
    componentVersion:version,sourceCommit:commit,canonicalSourceHash:hash,parserVersion,
    contractVersion:'1.0',profileId:'lithosharp-markdown/1',
    hashEncoding:'UTF8 path byte order + uint64 BE path/content lengths + Git blob bytes',
    excludedGeneratedStamp:stamp,
    dependencies:[{packageId:'YamlDotNet',version:'18.1.0',range:'[18.1.0]',
      assemblyFullName:'YamlDotNet, Version=18.0.0.0, Culture=neutral, PublicKeyToken=ec19458f3c15af5e',
      informationalVersion:'18.1.0',identityRole:'expected; actual loaded metadata must be verified by consumer'}],
    canonicalFiles:entries.map(e=>({path:e.path,byteLength:e.bytes.length,sha256:sha(e.bytes)})),
    generatedStamp:{packagePath:'src/Portable/MarkdownParserVersion.g.cs',sha256:sha(stampBytes)},
    licenses:[],runtimeFiles:[],packagingInputs:[]
  };
  const licenseMap=[['LICENSE','LICENSE'],['licenses/YamlDotNet.LICENSE.txt','licenses/YamlDotNet.LICENSE.txt']];
  // Keep notices specific to the portable component; site-only dependencies are not distributed.
  const notice=Buffer.from('LithoSharp.Markdown and LithoSharp.Markdown.Source include YamlDotNet 18.1.0 (MIT).\nSee licenses/YamlDotNet.LICENSE.txt. Portable parser author/license are preserved in LICENSE.\n');
  for(const [original,packagePath] of licenseMap){
    const bytes=blob(commit,original);metadata.licenses.push({sourcePath:original,packagePath,sha256:sha(bytes)});
    for(const kind of ['runtime','source'])write(path.join(out,kind,packagePath),bytes);
  }
  metadata.licenses.push({packagePath:'licenses/THIRD-PARTY-NOTICES.md',sha256:sha(notice)});
  for(const kind of ['runtime','source'])write(path.join(out,kind,'licenses/THIRD-PARTY-NOTICES.md'),notice);
  for(const e of entries){
    const relative=e.path.slice(component.length);
    if(relative.startsWith('Portable/')){
      write(path.join(out,'runtime',relative),e.bytes);
      write(path.join(out,'source','src',relative),e.bytes);
    }else write(path.join(out,'source','contracts',path.basename(relative)),e.bytes);
  }
  write(path.join(out,'runtime','Portable/MarkdownParserVersion.g.cs'),stampBytes);
  write(path.join(out,'source','src/Portable/MarkdownParserVersion.g.cs'),stampBytes);
  for(const original of all.filter(p=>p.startsWith(component+'Public/')||p.startsWith(component+'Properties/'))){
    const bytes=blob(commit,original),stagePath=original.slice(component.length);
    metadata.runtimeFiles.push({path:original,stagePath,byteLength:bytes.length,sha256:sha(bytes)});
    write(path.join(out,'runtime',stagePath),bytes);
  }
  for(const template of ['runtime.csproj','source.csproj','LithoSharp.Markdown.Source.targets']){
    const input=fs.readFileSync(path.join(root,'eng/markdown/package',template));
    const text=input.toString().replaceAll('__VERSION__',version).replaceAll('__COMMIT__',commit).replaceAll('__PARSER_VERSION__',parserVersion);
    const target=template.endsWith('.targets')?path.join(out,'source','build',template):
      path.join(out,template.split('.')[0],template);
    metadata.packagingInputs.push({path:'eng/markdown/package/'+template,sha256:sha(input),stagePath:path.relative(out,target).replaceAll('\\','/'),stagedSha256:sha(Buffer.from(text))});
    write(target,text);
  }
  for(const kind of ['runtime','source']){
    writeJson(path.join(out,kind,'markdown/manifest.json'),metadata);
    write(path.join(out,kind,'README.md'),'# '+ids[kind==='runtime'?0:1]+'\n\nImmutable local candidate '+version+'.\n\nCanonical parser '+parserVersion+' from '+commit+'.\nSource hosts explicitly enable LithoSharpMarkdownIncludeSource and reference exact [V], PrivateAssets=all, IncludeAssets=build.\nThey reference YamlDotNet [18.1.0] directly. Public facade and runtime friend metadata are excluded from source payload.\n');
  }
  fs.mkdirSync(path.join(out,'feed'));fs.mkdirSync(path.join(out,'packages'));
  write(path.join(out,'NuGet.Config'),'<?xml version="1.0" encoding="utf-8"?>\n<configuration><packageSources><clear/><add key="pair-local" value="'+path.join(out,'feed').replaceAll('\\','/')+'"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources><packageSourceMapping><packageSource key="pair-local"><package pattern="LithoSharp.Markdown"/><package pattern="LithoSharp.Markdown.Source"/></packageSource><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping></configuration>\n');
  writeJson(path.join(out,'prepared.json'),{task:'MD-05',state:'PREPARED_NOT_PACKED',preparedAt:new Date().toISOString(),metadata,
    output:out,feed:path.join(out,'feed'),packages:path.join(out,'packages')});
  console.log(JSON.stringify({state:'PREPARED_NOT_PACKED',componentVersion:version,sourceCommit:commit,canonicalSourceHash:hash,files:entries.length,output:out}));
}
function checkStage(out){
  out=path.resolve(out);const {metadata:m}=json(path.join(out,'prepared.json'));
  for(const e of m.canonicalFiles){
    const relative=e.path.slice(component.length);
    const destinations=relative.startsWith('Portable/')?[path.join(out,'runtime',relative),path.join(out,'source','src',relative)]:[path.join(out,'source','contracts',path.basename(relative))];
    for(const p of destinations)assert(sha(fs.readFileSync(p))===e.sha256,'Staged canonical bytes changed: '+p);
  }
  for(const kind of ['runtime','source']){
    const p=kind==='runtime'?'Portable/MarkdownParserVersion.g.cs':m.generatedStamp.packagePath;
    assert(sha(fs.readFileSync(path.join(out,kind,p)))===m.generatedStamp.sha256,'Staged stamp changed');
    assert(JSON.stringify(json(path.join(out,kind,'markdown/manifest.json')))===JSON.stringify(m),'Staged metadata changed');
    for(const e of m.licenses)assert(sha(fs.readFileSync(path.join(out,kind,e.packagePath)))===e.sha256,'Staged license changed');
  }
  const expected=m.canonicalFiles.filter(e=>e.path.startsWith(component+'Portable/')).map(e=>e.path.slice(component.length));
  expected.push('Portable/MarkdownParserVersion.g.cs',...m.runtimeFiles.map(e=>e.stagePath));
  const actual=[];
  function sourceFiles(dir,relative=''){
    for(const item of fs.readdirSync(dir,{withFileTypes:true})){
      if(item.isDirectory()&&['bin','obj'].includes(item.name))continue;
      const p=relative+item.name;
      if(item.isDirectory())sourceFiles(path.join(dir,item.name),p+'/');
      else {assert(item.isFile(),'Unsupported stage entry');if(p.endsWith('.cs'))actual.push(p);}
    }
  }
  sourceFiles(path.join(out,'runtime'));
  assert(JSON.stringify(actual.sort())===JSON.stringify(expected.sort()),'Runtime Compile source set differs from selected Git files');
  for(const e of m.runtimeFiles)assert(sha(fs.readFileSync(path.join(out,'runtime',e.stagePath)))===e.sha256&&sha(blob(m.sourceCommit,e.path))===e.sha256,'Runtime-only source changed');
  for(const e of m.packagingInputs){
    const input=fs.readFileSync(path.join(root,e.path));assert(sha(input)===e.sha256,'Packaging recipe changed after prepare');
    const text=input.toString().replaceAll('__VERSION__',m.componentVersion).replaceAll('__COMMIT__',m.sourceCommit).replaceAll('__PARSER_VERSION__',m.parserVersion);
    const name=path.basename(e.path),p=name.endsWith('.targets')?path.join(out,'source','build',name):path.join(out,name.split('.')[0],name);
    assert(fs.readFileSync(p,'utf8')===text&&sha(fs.readFileSync(p))===e.stagedSha256,'Staged package project/target changed');
  }
  return m;
}
function seal(out){
  out=path.resolve(out);const metadata=checkStage(out);
  assert(!fs.existsSync(path.join(out,'pair-manifest.json')),'Never reseal/overwrite a pair');
  const artifacts={};
  for(let i=0;i<ids.length;i++){
    const kind=i===0?'runtime':'source',p=path.join(out,'feed',ids[i]+'.'+metadata.componentVersion+'.nupkg');
    const bytes=fs.readFileSync(p),z=zip(p),inside=JSON.parse(z.get('markdown/manifest.json').toString());
    assert(JSON.stringify(inside)===JSON.stringify(metadata),'Pair metadata mismatch');
    checkLicenses(z,metadata);
    const nuspec=z.get(ids[i]+'.nuspec').toString();
    assert(nuspec.includes('<id>'+ids[i]+'</id>')&&nuspec.includes('<version>'+metadata.componentVersion+'</version>'),'Package identity mismatch');
    if(kind==='runtime'){
      assert(/<dependency id="YamlDotNet" version="\[18\.1\.0\]"/.test(nuspec),'Runtime dependency is not exact');
      assert(z.has('lib/net10.0/LithoSharp.Markdown.dll'),'Runtime facade DLL missing');
    }else{
      assert(![...z.keys()].some(p=>p.startsWith('lib/')||p.startsWith('buildTransitive/')||p.startsWith('contentFiles/')||p.includes('/Public/')||p.includes('/Properties/')||p.endsWith('AssemblyInfo.cs')),'Forbidden source payload');
      const expected=metadata.canonicalFiles.filter(e=>e.path.startsWith(component+'Portable/')).map(e=>['src/'+e.path.slice(component.length),e.sha256]);
      expected.push([metadata.generatedStamp.packagePath,metadata.generatedStamp.sha256]);
      const actual=[...z.keys()].filter(p=>p.endsWith('.cs'));
      assert(actual.length===expected.length,'Unexpected source files');
      for(const [name,hash] of expected)assert(z.has(name)&&sha(z.get(name))===hash,'Source byte mismatch: '+name);
      for(const e of metadata.canonicalFiles.filter(e=>e.path.startsWith(component+'Contracts/')))
        assert(z.has('contracts/'+path.basename(e.path))&&sha(z.get('contracts/'+path.basename(e.path)))===e.sha256,'Contract byte mismatch');
      const targets=z.get('build/LithoSharp.Markdown.Source.targets').toString();
      assert(targets.includes('LITHOSHARP_MARKDOWN_SOURCE')&&targets.includes(metadata.parserVersion),'Source target/stamp missing');
      assert(!/<dependencies>[\s\S]*<dependency /.test(nuspec),'Source hosts must directly reference their dependencies');
    }
    artifacts[kind]={packageId:ids[i],componentVersion:metadata.componentVersion,fileName:path.basename(p),sha256:sha(bytes),byteLength:bytes.length};
    if(kind==='runtime')artifacts[kind].assemblySha256=sha(z.get('lib/net10.0/LithoSharp.Markdown.dll'));
  }
  writeJson(path.join(out,'pair-manifest.json'),{...metadata,state:'PACKED_NOT_HOST_VERIFIED',artifacts});
  console.log(JSON.stringify({state:'PACKED_NOT_HOST_VERIFIED',artifacts}));
}
function verifyRestored(out,canaryPath){
  out=path.resolve(out);const m=json(path.join(out,'pair-manifest.json')),c=json(canaryPath);
  assert(c.status==='PASS'&&c.componentVersion===m.componentVersion&&c.parserVersion===m.parserVersion,'Canary metadata mismatch');
  assert(c.runtimeAssemblySha256===m.artifacts.runtime.assemblySha256,'Runtime loaded DLL differs from nupkg');
  assert(c.sourceParseCompiled&&c.noSourceFacade&&c.noSourceFriendMetadata&&c.expectedStampRejected&&c.expectedDependencyRejected,'Required source host guards missing');
  assert(c.yamlAssembly===m.dependencies[0].assemblyFullName&&c.yamlInformation===m.dependencies[0].informationalVersion&&c.sourceYamlAssembly===m.dependencies[0].assemblyFullName&&c.sourceYamlInformation===m.dependencies[0].informationalVersion,'Actual runtime/source loaded dependency differs');
  const restored={};
  for(const [kind,a] of Object.entries(m.artifacts)){
    const p=path.join(out,'packages',a.packageId.toLowerCase(),m.componentVersion,a.packageId.toLowerCase()+'.'+m.componentVersion+'.nupkg');
    assert(sha(fs.readFileSync(p))===a.sha256,'Restored '+kind+' artifact hash mismatch');restored[kind]={sha256:a.sha256};
  }
  writeJson(path.join(out,'restored-verification.json'),{task:'MD-05',state:'PASS',checkedAt:new Date().toISOString(),componentVersion:m.componentVersion,
    sourceCommit:m.sourceCommit,canonicalSourceHash:m.canonicalSourceHash,parserVersion:m.parserVersion,restored,canary:c,
    scope:'Fixed local pair + actual Generator Parse body on net10 CLR; both-repo and real Roslyn-host matrix remains IN-01'});
  console.log(JSON.stringify({state:'PASS',componentVersion:m.componentVersion,parserVersion:m.parserVersion,restored}));
}
if(action==='prepare'&&args.length===3)prepare(...args);
else if(action==='check-stage'&&args.length===1){const m=checkStage(args[0]);console.log(JSON.stringify({state:'STAGE_BYTES_PASS',componentVersion:m.componentVersion,canonicalSourceHash:m.canonicalSourceHash}));}
else if(action==='seal'&&args.length===1)seal(args[0]);
else if(action==='verify-restored'&&args.length===2)verifyRestored(...args);
else throw Error('Usage: New-ArtifactPair.mjs prepare <source SHA> <2.0.0-preview.N> <new absolute stage>; check-stage <stage>; seal <stage>; verify-restored <stage> <canary JSON>');
