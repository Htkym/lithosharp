import { readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
const output = path.resolve(process.argv[2]);
const pages = new Map();
function walk(directory) {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const file = path.join(directory, entry.name);
    if (entry.isDirectory()) walk(file);
    else if (entry.name.endsWith('.html')) pages.set(path.relative(output, file).replaceAll('\\', '/'), readFileSync(file, 'utf8'));
  }
}
walk(output);
const incoming = new Set();
const resolved = new Map();
const edges = new Map();
for (const [name, html] of pages) {
  const menu = html.match(/<aside\b[^>]*id="docs-sidebar"[\s\S]*?<\/aside>/)?.[0];
  if (!['index.html', '404.html', 'search.html'].includes(name)) assert(menu, `Missing shared menu: ${name}`);
  if (!menu) continue;
  assert((menu.match(/aria-current="page"/g) ?? []).length <= 1, `Duplicate active menu entries: ${name}`);
  assert((menu.match(/>API (?:index|一覧)<\/a>/g) ?? []).length <= 1, `Duplicate API catalog subtree: ${name}`);
  const links = new Set();
  edges.set(name, links);
  for (const match of menu.matchAll(/href="([^"]+)"/g)) {
    const url = new URL(match[1].replaceAll('&amp;', '&'), 'https://example.invalid');
    let target = decodeURIComponent(url.pathname);
    if (target.endsWith('/')) target += 'index.html';
    let exact = resolved.get(target);
    if (!exact) {
      const parts = target.split('/').filter(Boolean);
      for (let i = 0; i < parts.length; i++) {
        const candidate = parts.slice(i).join('/');
        if (pages.has(candidate)) { exact = candidate; break; }
      }
      resolved.set(target, exact);
    }    assert(exact, `Menu target does not exist: ${name} -> ${match[1]}`);
    incoming.add(exact);
    links.add(exact);
  }
}
// 404 is an error response, not a navigation destination. Search remains available in the header.
const excluded = new Set(['404.html', 'search.html']);
const reachable = new Set();
const queue = ['en/introduction/index.html', 'ja/introduction/index.html'];
while (queue.length) { const page = queue.pop(); if (reachable.has(page)) continue; reachable.add(page); queue.push(...(edges.get(page) ?? [])); }
for (const file of pages.keys()) if (!excluded.has(file)) assert(reachable.has(file), `Unreachable from shared menus: ${file}`);
console.log(`Shared navigation verified: ${pages.size} pages, ${incoming.size} menu destinations; 404/search utility exceptions.`);
