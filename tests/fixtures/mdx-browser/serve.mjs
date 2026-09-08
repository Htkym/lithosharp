import {createServer} from 'node:http';
import {readFile, stat} from 'node:fs/promises';
import path from 'node:path';
let root = path.resolve(process.argv[2]);
const revisions = process.argv.slice(4).map(value => path.resolve(value));
const types = {'.html':'text/html', '.js':'text/javascript', '.css':'text/css', '.json':'application/json', '.svg':'image/svg+xml', '.woff2':'font/woff2'};
createServer(async (request, response) => {
  try {
    if (request.method === 'POST' && request.url === '/_fixture/next' && revisions.length) {
      root = revisions.shift(); response.end('Switched fixture revision'); return;
    }
    let file = path.resolve(root, '.' + decodeURIComponent(new URL(request.url, 'http://localhost').pathname));
    if (file !== root && !file.startsWith(root + path.sep)) throw new Error('Invalid path');
    if ((await stat(file)).isDirectory()) file = path.join(file, 'index.html');
    response.setHeader('Content-Type', types[path.extname(file)] ?? 'application/octet-stream');
    response.end(await readFile(file));
  } catch { response.writeHead(404); response.end('Not found'); }
}).listen(Number(process.argv[3] ?? 4317), '127.0.0.1');
