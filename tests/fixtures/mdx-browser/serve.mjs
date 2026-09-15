import {createServer} from 'node:http';
import {readFile, stat} from 'node:fs/promises';
import path from 'node:path';
let root = path.resolve(process.argv[2]);
const revisions = process.argv.slice(4).map(value => path.resolve(value));
// Optional sub-path reverse-proxy simulation: only URLs under LS_BASE_PATH
// are served, with the prefix stripped before file resolution.
const basePath = (process.env.LS_BASE_PATH ?? '').replace(/\/$/, '');
const types = {'.html':'text/html', '.js':'text/javascript', '.css':'text/css', '.json':'application/json', '.svg':'image/svg+xml', '.woff2':'font/woff2'};
async function notFound(response) {
  try {
    const body = await readFile(path.join(root, '404.html'));
    response.writeHead(404, {'Content-Type': 'text/html'});
    response.end(body);
    return;
  } catch { response.writeHead(404); response.end('Not found'); }
}
createServer(async (request, response) => {
  try {
    if (request.method === 'POST' && request.url === '/_fixture/next' && revisions.length) {
      root = revisions.shift(); response.end('Switched fixture revision'); return;
    }
    let pathname = decodeURIComponent(new URL(request.url, 'http://localhost').pathname);
    if (basePath) {
      if (pathname !== basePath && !pathname.startsWith(basePath + '/')) { await notFound(response); return; }
      pathname = pathname.slice(basePath.length) || '/';
    }
    let file = path.resolve(root, '.' + pathname);
    if (file !== root && !file.startsWith(root + path.sep)) throw new Error('Invalid path');
    if ((await stat(file)).isDirectory()) file = path.join(file, 'index.html');
    response.setHeader('Content-Type', types[path.extname(file)] ?? 'application/octet-stream');
    response.end(await readFile(file));
  } catch { await notFound(response); }
}).listen(Number(process.argv[3] ?? 4317), '127.0.0.1');
