import * as fs from 'node:fs/promises';
import * as path from 'node:path';
import { createHash } from 'node:crypto';

const [configPath] = process.argv.slice(2);
if (!configPath) {
  throw new Error('Usage: node Collect-MigrationRoutes.mjs <collector-config.json>');
}

const config = JSON.parse(await fs.readFile(configPath, 'utf8'));
const repositoryRoot = path.resolve(config.repositoryRoot);
const siteDirectory = path.resolve(repositoryRoot, config.sitePath);
const buildDirectory = path.resolve(repositoryRoot, config.buildOutput);
const generatedDirectory = path.resolve(siteDirectory, '.docusaurus');
const outputDirectory = path.dirname(path.resolve(config.routeOracleOutput));

for (const required of [buildDirectory, generatedDirectory]) {
  if (!(await exists(required))) {
    throw new Error(`Expected original-build artifact is missing: ${required}`);
  }
}

function exists(filePath) {
  return fs.stat(filePath).then(() => true, () => false);
}

function publicPath(value) {
  const url = new URL(value, config.siteUrl);
  return url.pathname || '/';
}

function pathWithoutBase(route) {
  const base = normalizePrefix(config.basePath ?? '/');
  if (base === '/') return route;
  return route.startsWith(base) ? `/${route.slice(base.length)}` : route;
}

function normalizePrefix(value) {
  let prefix = `/${String(value ?? '/').replace(/^\/+|\/+$/g, '')}`;
  if (prefix === '/') return prefix;
  return `${prefix}/`;
}

function sourceToRepositoryCandidates(source) {
  let value = source.replaceAll('\\', '/');
  if (value.startsWith('@site/')) value = path.posix.join(config.sitePath, value.slice('@site/'.length));
  else if (value.startsWith('~docs/')) value = path.posix.join(config.sitePath, value.slice('~docs/'.length));
  else if (value.startsWith('~blog/')) value = path.posix.join(config.sitePath, value.slice('~blog/'.length));
  else if (value === config.sitePath || value.startsWith(`${config.sitePath.replace(/\/$/, '')}/`)) value = path.posix.normalize(value);
  else if (path.posix.isAbsolute(value)) value = path.relative(repositoryRoot, value).replaceAll('\\', '/');
  else value = path.posix.join(config.sitePath, value);

  value = path.posix.normalize(value).replace(/^\.\//, '');
  if (value === '..' || value.startsWith('../')) return [];
  // Versioned and translated trees are sometimes recorded relative to the
  // repository root even under an @site/ alias (e.g. site/versioned_docs
  // resolving through website/). Prefer the joined path; fall back to the
  // site-relative spelling only when the joined path maps to no input.
  const sitePrefix = config.sitePath.replace(/\/$/, '');
  if (value !== sitePrefix && value.startsWith(`${sitePrefix}/`)) {
    return [value, value.slice(sitePrefix.length + 1)];
  }
  return [value];
}

function migrationSourcePath(repositoryCandidates) {
  const mappings = [...config.inputMappings].sort((left, right) => right.source.length - left.source.length);
  for (const repositoryRelativePath of repositoryCandidates) {
    for (const mapping of mappings) {
      const root = mapping.source.replaceAll('\\', '/').replace(/\/$/, '');
      if (repositoryRelativePath !== root && !repositoryRelativePath.startsWith(`${root}/`)) continue;
      const suffix = repositoryRelativePath === root ? '' : repositoryRelativePath.slice(root.length + 1);
      return [mapping.target.replaceAll('\\', '/').replace(/\/$/, ''), suffix].filter(Boolean).join('/');
    }
  }
  return null;
}

function sourceLocale(repositoryPath, metadataLocale) {
  if (typeof metadataLocale === 'string' && metadataLocale.length > 0) return metadataLocale;
  const match = repositoryPath?.match(/(?:^|\/)i18n\/([^/]+)\//);
  return match?.[1] ?? config.defaultLocale ?? null;
}

function collectMetadata(value, found) {
  if (Array.isArray(value)) {
    for (const item of value) collectMetadata(item, found);
    return;
  }
  if (typeof value !== 'object' || value === null) return;
  if (typeof value.permalink === 'string' && typeof value.source === 'string'
      && /\.(?:md|mdx)$/i.test(value.source)) {
    const candidates = sourceToRepositoryCandidates(value.source);
    const sourcePath = candidates.length === 0 ? null : migrationSourcePath(candidates);
    const via = sourcePath === null
      ? null
      : candidates.find((item) => migrationSourcePath([item]) === sourcePath) ?? candidates[0];
    found.push({
      path: publicPath(value.permalink),
      repositoryPath: via,
      sourcePath,
      locale: sourceLocale(candidates.find((item) => /(?:^|\/)i18n\/[^/]+\//.test(item)) ?? via, value.locale),
      version: typeof value.version === 'string' ? value.version : null,
      format: path.posix.extname(value.source).slice(1).toLowerCase(),
    });
  }
  for (const child of Object.values(value)) collectMetadata(child, found);
}

async function findJsonFiles(root) {
  if (!(await exists(root))) return [];
  const files = [];
  async function visit(directory) {
    for (const entry of await fs.readdir(directory, { withFileTypes: true })) {
      const full = path.join(directory, entry.name);
      if (entry.isSymbolicLink()) continue;
      if (entry.isDirectory()) await visit(full);
      else if (entry.isFile() && entry.name.endsWith('.json')) files.push(full);
    }
  }
  await visit(root);
  return files.sort((left, right) => left.localeCompare(right));
}

const metadata = [];
for (const pluginDirectory of [
  path.join(generatedDirectory, 'docusaurus-plugin-content-docs'),
  path.join(generatedDirectory, 'docusaurus-plugin-content-blog'),
]) {
  for (const file of await findJsonFiles(pluginDirectory)) {
    try {
      collectMetadata(JSON.parse(await fs.readFile(file, 'utf8')), metadata);
    } catch (error) {
      if (error instanceof SyntaxError) continue;
      throw error;
    }
  }
}

const metadataByPath = new Map();
function addMetadata(path, item) {
  const list = metadataByPath.get(path) ?? [];
  list.push(item);
  metadataByPath.set(path, list);
}
for (const item of metadata) {
  addMetadata(item.path, item);
  // Docusaurus metadata permalinks omit the trailing slash that generated
  // directory pages carry; index both spellings so HTML-derived routes match.
  if (!item.path.endsWith('/')) {
    addMetadata(`${item.path}/`, item);
  }
}

function classifyDerivedRoute(route) {
  let pathWithoutBaseUrl = pathWithoutBase(route);
  let locale = null;
  const segments = pathWithoutBaseUrl.split('/').filter(Boolean);
  if (segments.length > 1 && (config.locales ?? []).includes(segments[0]) && segments[0] !== config.defaultLocale) {
    locale = segments[0];
    pathWithoutBaseUrl = `/${segments.slice(1).join('/')}/`;
  }
  for (const prefixValue of config.blogRoutePrefixes ?? []) {
    const prefix = normalizePrefix(prefixValue);
    if (pathWithoutBaseUrl === prefix.slice(0, -1) || pathWithoutBaseUrl === prefix) {
      return { kind: 'blogIndex', locale, reason: 'Generated blog index; it is not a one-to-one article page.' };
    }
    if (!pathWithoutBaseUrl.startsWith(prefix)) continue;
    const tail = pathWithoutBaseUrl.slice(prefix.length);
    if (/^authors(?:\/|$)/.test(tail)) return { kind: 'blogAuthor', locale, reason: 'Generated author index.' };
    if (/^tags(?:\/|$)/.test(tail)) return { kind: 'blogTag', locale, reason: 'Generated tag index.' };
    if (/^archive(?:\/|$)/.test(tail)) return { kind: 'blogArchive', locale, reason: 'Generated archive index.' };
    if (/^page\/\d+(?:\/|$)/.test(tail)) return { kind: 'blogPagination', locale, reason: 'Generated blog pagination route.' };
  }

  for (const prefixValue of config.documentRoutePrefixes ?? []) {
    const prefix = normalizePrefix(prefixValue);
    if (pathWithoutBaseUrl.startsWith(prefix)) {
      return { kind: 'categoryIndex', locale, reason: 'Generated docs-prefix route has no mapped source document.' };
    }
  }

  return { kind: 'other', locale, reason: 'Generated page has no mapped document/blog source.' };
}

async function findHtmlFiles(root) {
  const files = [];
  async function visit(directory) {
    for (const entry of await fs.readdir(directory, { withFileTypes: true })) {
      const full = path.join(directory, entry.name);
      if (entry.isSymbolicLink()) continue;
      if (entry.isDirectory()) await visit(full);
      else if (entry.isFile() && entry.name.toLowerCase().endsWith('.html')) files.push(full);
    }
  }
  await visit(root);
  return files.sort((left, right) => left.localeCompare(right));
}

function routeFromHtml(relativePath, html) {
  const canonicalTag = html.match(/<link\b(?=[^>]*\brel=["']canonical["'])[^>]*>/i)?.[0]
    ?? html.match(/<link\b(?=[^>]*\bhref=["'][^"']+["'])[^>]*\brel=["']canonical["'][^>]*>/i)?.[0];
  const href = canonicalTag?.match(/\bhref=["']([^"']+)["']/i)?.[1];
  if (href) {
    try {
      return publicPath(href.replaceAll('&amp;', '&'));
    } catch {
      // Fall back to the generated output path, which is still included in the route audit.
    }
  }

  const normalized = relativePath.replaceAll('\\', '/');
  if (normalized === 'index.html') return '/';
  if (normalized.endsWith('/index.html')) return `/${normalized.slice(0, -'index.html'.length)}`;
  if (normalized.endsWith('.html')) return `/${normalized.slice(0, -'.html'.length)}`;
  return `/${normalized}`;
}

const routes = [];
const routesByPath = new Map();
const sourceMap = [];
const conflicts = [];
for (const file of await findHtmlFiles(buildDirectory)) {
  const relative = path.relative(buildDirectory, file).replaceAll('\\', '/');
  const routePath = routeFromHtml(relative, await fs.readFile(file, 'utf8'));
  const sourceRecords = metadataByPath.get(routePath) ?? [];
  const mapped = sourceRecords.find(record => record.sourcePath !== null);
  let route;
  if (mapped) {
    route = { path: routePath, kind: 'document', locale: mapped.locale, reason: null };
    sourceMap.push({
      path: routePath,
      repositorySourcePath: mapped.repositoryPath,
      sourcePath: mapped.sourcePath,
      version: mapped.version,
      locale: mapped.locale,
      format: mapped.format,
    });
  } else {
    const inferred = classifyDerivedRoute(routePath);
    route = { path: routePath, ...inferred };
    if (sourceRecords.length > 0) {
      route.reason = `Page source is outside the selected migration inputs: ${sourceRecords.map(record => record.repositoryPath ?? 'unresolved').join(', ')}`;
      route.kind = 'other';
    }
  }

  const previous = routesByPath.get(route.path);
  if (!previous) {
    routes.push(route);
    routesByPath.set(route.path, route);
  }
  else if (previous.kind !== route.kind) {
    // Prefer an actual content source over a derived-page heuristic; preserve the collision for review.
    if (route.kind === 'document') Object.assign(previous, route);
    conflicts.push({ path: route.path, firstKind: previous.kind, secondKind: route.kind });
  }
}

routes.sort((left, right) => left.path.localeCompare(right.path));
sourceMap.sort((left, right) => left.path.localeCompare(right.path));
const routeOracle = {
  schemaVersion: '1.0',
  sourceVersion: config.sourceVersion,
  basePath: config.basePath,
  routes,
};
const sourceMapReport = {
  schemaVersion: '1.0',
  routeCount: sourceMap.length,
  routes: sourceMap,
  duplicateOrConflictingRoutes: conflicts,
};

await fs.mkdir(outputDirectory, { recursive: true });
await fs.writeFile(config.routeOracleOutput, `${JSON.stringify(routeOracle, null, 2)}\n`);
await fs.writeFile(config.routeSourceMapOutput, `${JSON.stringify(sourceMapReport, null, 2)}\n`);

const hashFile = async file => createHash('sha256').update(await fs.readFile(file)).digest('hex');
console.log(JSON.stringify({
  siteId: config.siteId,
  htmlPageCount: (await findHtmlFiles(buildDirectory)).length,
  routeCount: routes.length,
  mappedDocumentCount: sourceMap.length,
  routeOracleSha256: await hashFile(config.routeOracleOutput),
  sourceMapSha256: await hashFile(config.routeSourceMapOutput),
  unclassifiedCount: routes.filter(route => route.kind === 'unclassified').length,
  conflicts,
}, null, 2));
