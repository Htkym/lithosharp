import {createServer} from 'node:http';
import {readFile, stat} from 'node:fs/promises';
import path from 'node:path';
import {pathToFileURL} from 'node:url';
const deliveryProbe = `(()=>{if(globalThis.__deliveryProbe)return;globalThis.__deliveryProbe=true;
if(new URL(location.href).searchParams.has('block-storage'))Object.defineProperty(window,'localStorage',{get(){throw new DOMException('Blocked','SecurityError')}});
const report=(kind,message,details={})=>fetch(new URL('delivery-events',document.querySelector('script[data-delivery-probe]').src),{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({kind,message:String(message).slice(0,2048),url:location.pathname,...details}),keepalive:true}).catch(()=>{});
addEventListener('securitypolicyviolation',e=>report('csp',e.violatedDirective+': '+e.blockedURI,{sourceFile:e.sourceFile,lineNumber:e.lineNumber,columnNumber:e.columnNumber,sample:e.sample,disposition:e.disposition,originalPolicy:e.originalPolicy}));
addEventListener('error',e=>{if(e.message)report('error',e.message);else if(e.target?.src||e.target?.href)report('resource',e.target.src||e.target.href)},true);
addEventListener('unhandledrejection',e=>report('rejection',e.reason?.message||e.reason));
for(const name of ['lithosharp:hydration-error','lithosharp:chunk-error'])document.addEventListener(name,e=>report(name,e.detail?.message||JSON.stringify(e.detail)));
})();`;

export function createFixtureServer({root: initialRoot, revisions: nextRoots = [], basePath: prefix = '', csp,
  probe = false, onDeliveryEvent = () => {}}) {
let root = path.resolve(initialRoot);
const revisions = nextRoots.map(value => path.resolve(value));
// Optional sub-path reverse-proxy simulation: only URLs under LS_BASE_PATH
// are served, with the prefix stripped before file resolution.
const basePath = prefix.replace(/\/$/, '');
const types = {'.html':'text/html', '.js':'text/javascript', '.css':'text/css', '.json':'application/json', '.svg':'image/svg+xml', '.woff2':'font/woff2'};
function html(bytes) {
  if (!probe) return bytes;
  const text = bytes.toString('utf8');
  if (text.split('<head>').length !== 2) throw new Error('Delivery probe needs exactly one generated head.');
  return text.replace('<head>', `<head><script data-delivery-probe src="${basePath}/_fixture/delivery-probe.js"></script>`);
}
async function notFound(response) {
  try {
    const body = await readFile(path.join(root, '404.html'));
    response.writeHead(404, {'Content-Type': 'text/html'});
    response.end(html(body));
    return;
  } catch { response.writeHead(404); response.end('Not found'); }
}
return createServer(async (request, response) => {
  if (csp) response.setHeader('Content-Security-Policy', csp);
  try {
    if (request.method === 'POST' && request.url === '/_fixture/next' && revisions.length) {
      root = revisions.shift(); response.end('Switched fixture revision'); return;
    }
    let pathname = decodeURIComponent(new URL(request.url, 'http://localhost').pathname);
    if (basePath) {
      if (pathname !== basePath && !pathname.startsWith(basePath + '/')) { await notFound(response); return; }
      pathname = pathname.slice(basePath.length) || '/';
    }
    if (probe && pathname === '/_fixture/delivery-probe.js') {
      response.setHeader('Content-Type', 'text/javascript'); response.end(deliveryProbe); return;
    }
    if (probe && pathname === '/_fixture/delivery-events' && request.method === 'POST') {
      const chunks = []; let size = 0;
      for await (const chunk of request) { size += chunk.length; if (size > 8192) { response.writeHead(413); response.end(); return; } chunks.push(chunk); }
      onDeliveryEvent(JSON.parse(Buffer.concat(chunks).toString('utf8')));
      response.writeHead(204); response.end(); return;
    }
    let file = path.resolve(root, '.' + pathname);
    if (file !== root && !file.startsWith(root + path.sep)) throw new Error('Invalid path');
    if ((await stat(file)).isDirectory()) file = path.join(file, 'index.html');
    response.setHeader('Content-Type', types[path.extname(file)] ?? 'application/octet-stream');
    const bytes = await readFile(file);
    response.end(path.extname(file) === '.html' ? html(bytes) : bytes);
  } catch { await notFound(response); }
});
}

if (process.argv[1] && pathToFileURL(path.resolve(process.argv[1])).href === import.meta.url)
  createFixtureServer({root: process.argv[2], revisions: process.argv.slice(4),
    basePath: process.env.LS_BASE_PATH ?? '', csp: process.env.LS_CSP})
    .listen(Number(process.argv[3] ?? 4317), '127.0.0.1');
