import {readFile, realpath, mkdir, writeFile} from 'node:fs/promises';
import path from 'node:path';
import {createRequire} from 'node:module';
import {pathToFileURL, fileURLToPath} from 'node:url';
import {createHash} from 'node:crypto';
import {PassThrough} from 'node:stream';
import {compile} from '@mdx-js/mdx';
import {build} from 'esbuild';
import remarkGfm from 'remark-gfm';
import remarkDirective from 'remark-directive';
import remarkMath from 'remark-math';
import rehypeKatex from 'rehype-katex';
import rehypeSlug from 'rehype-slug';
import {visit} from 'unist-util-visit';
import Prism from 'prismjs';
import loadLanguages from 'prismjs/components/index.js';

const directory = path.dirname(fileURLToPath(import.meta.url));
const workerRequire = createRequire(import.meta.url);
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const relative = (root, file) => path.relative(root, file).split(path.sep).join('/');
const inside = (root, file) => { const value = path.relative(root, file); return value === '' || (!path.isAbsolute(value) && value !== '..' && !value.startsWith(`..${path.sep}`)); };
const textOf = node => node.type === 'text' || node.type === 'inlineCode' ? node.value : (node.children ?? []).map(textOf).join('');
// ponytail: bounded session caches; a disk cache already handles no-op worker restarts.
const moduleCache = new Map();
const renderCache = new Map();
function remember(cache, key, value) { if (cache.size >= 2048) cache.delete(cache.keys().next().value); cache.set(key, value); }

export function extractRegion(source, name) {
  if (!name) return source;
  const lines = source.split(/\r?\n/);
  let start = -1, depth = 0;
  for (let index = 0; index < lines.length; index++) {
    const opening = /^\s*#region(?:\s+(.*))?\s*$/.exec(lines[index]);
    if (opening) {
      if (start >= 0) depth++;
      else if (opening[1]?.trim() === name) { start = index + 1; depth = 1; }
    } else if (start >= 0 && /^\s*#endregion\b/.test(lines[index]) && --depth === 0) return lines.slice(start, index).join('\n');
  }
  throw new Error(`Code region '${name}' is missing or unterminated.`);
}

function highlight(code, language) {
  if (!language || language === 'text' || language === 'plain') return null;
  if (!Prism.languages[language]) {
    if (!/^[a-z0-9-]+$/i.test(language)) throw new Error(`Invalid code language '${language}'.`);
    try { loadLanguages([language]); } catch { /* An unknown language uses escaped plain code. */ }
  }
  return Prism.languages[language] ? Prism.highlight(code, Prism.languages[language], language) : null;
}

export async function compileSite(request) {
  const projectRoot = await realpath(request.projectRoot);
  const workRoot = await realpath(request.workRoot);
  if (inside(projectRoot, workRoot) && !request.allowWorkWithinProject) throw new Error('Worker scratch directory must be isolated from source.');
  const projectRequire = createRequire(path.join(projectRoot, 'package.json'));
  function resolvePackage(name) {
    try { return projectRequire.resolve(name); } catch (error) { if (error.code !== 'MODULE_NOT_FOUND') throw error; return workerRequire.resolve(name); }
  }
  const reactFile = await realpath(resolvePackage('react'));
  const serverFile = resolvePackage('react-dom/server');
  if (await realpath(createRequire(serverFile).resolve('react')) !== reactFile) throw new Error('Multiple React copies: react-dom resolves another React.');
  const {renderToPipeableStream} = await import(pathToFileURL(serverFile));
  const inputs = new Map();
  const metadata = new Map();
  const compiled = new Map();
  let compiledModules = 0, renderedPages = 0;
  const moduleDependencies = new Map();
  const allowedRoots = [projectRoot, directory];
  const pages = request.pages;
  const bySource = new Map(pages.map(page => [path.resolve(projectRoot, page.source), page]));

  async function readInput(file) {
    const resolved = await realpath(file);
    if (!allowedRoots.some(root => inside(root, resolved))) throw new Error(`Import escapes the declared roots: ${relative(projectRoot, file)}`);
    // Compare the lexical and real path: symlinked imports do not bypass C# source validation.
    const comparable = value => process.platform === 'win32' ? value.toLowerCase() : value;
    if (comparable(path.resolve(file)) !== comparable(resolved)) throw new Error(`Symbolic imports are not supported: ${relative(projectRoot, file)}`);
    const bytes = await readFile(resolved);
    inputs.set(resolved, hash(bytes));
    return bytes;
  }

  function authoring(file, info) {
    return function () {
      return async tree => {
        const jobs = [];
        visit(tree, node => {
          if (node.type === 'heading') info.headings.push({depth: node.depth, text: textOf(node), line: node.position?.start.line ?? 1});
          if (node.type === 'mdxjsEsm') {
            for (const statement of node.data?.estree?.body ?? []) {
              const names = [...(statement.specifiers ?? []).map(item => item.exported?.name),
                ...(statement.declaration?.declarations ?? []).map(item => item.id?.name), statement.declaration?.id?.name];
              if (names.some(name => ['frontMatter', 'toc', 'contentTitle'].includes(name)))
                throw new Error('frontMatter, toc and contentTitle are reserved MDX metadata exports.');
            }
          }
          if (node.type === 'link' || node.type === 'image') info.links.push({url: node.url, line: node.position?.start.line ?? 1});
          if (node.type === 'paragraph' || node.type === 'heading') info.text.push(textOf(node));
          if (node.type === 'containerDirective') {
            if (!['note', 'tip', 'info', 'warning', 'danger', 'caution'].includes(node.name)) throw new Error(`Unsupported directive '${node.name}'.`);
            node.data = {...node.data, hName: 'aside', hProperties: {className: ['mdx-admonition', `mdx-${node.name}`], role: 'note', 'aria-label': node.name}};
          }
          if (node.type === 'code') {
            const meta = node.meta ?? '';
            node.data = {...node.data, hProperties: {
              'data-title': /(?:^|\s)title="([^"]*)"/.exec(meta)?.[1] ?? '',
              'data-highlight': /\{([\d, -]+)\}/.exec(meta)?.[1] ?? '',
              'data-start': Number(/(?:^|\s)start=(\d+)/.exec(meta)?.[1] ?? 1),
              'data-line-numbers': /(?:^|\s)showLineNumbers(?:\s|$)/.test(meta)
            }};
          }
          if (node.type === 'mdxJsxFlowElement' || node.type === 'mdxJsxTextElement') {
            if (node.name === 'CodeBlock') {
              const attributes = new Map(node.attributes.filter(attribute => attribute.type === 'mdxJsxAttribute').map(attribute => [attribute.name, attribute]));
              const source = attributes.get('source')?.value;
              if (typeof source === 'string') jobs.push((async () => {
                const input = path.resolve(path.dirname(file), source);
                const code = extractRegion((await readInput(input)).toString('utf8'), attributes.get('region')?.value);
                (moduleDependencies.get(file) ?? moduleDependencies.set(file, new Set()).get(file)).add(input);
                node.attributes = node.attributes.filter(attribute => !['source', 'region'].includes(attribute.name));
                node.attributes.push({type: 'mdxJsxAttribute', name: 'code', value: code});
                const html = highlight(code, attributes.get('language')?.value);
                if (html) node.attributes.push({type: 'mdxJsxAttribute', name: 'highlightedHtml', value: html});
              })());
            }
          }
        });
        await Promise.all(jobs);
      };
    };
  }

  function codeBlocks(info) { return function () {
    return tree => visit(tree, 'element', node => {
      if (/^h[1-6]$/.test(node.tagName)) {
        const heading = info.headings.find(item => item.line === node.position?.start.line);
        if (heading) heading.id = node.properties.id;
      }
      if (node.tagName !== 'pre' || node.children[0]?.tagName !== 'code') return;
      const code = node.children[0];
      Object.assign(node.properties, code.properties);
      const language = code.properties.className?.find(name => name.startsWith('language-'))?.slice(9);
      const content = code.children.map(child => child.value ?? '').join('');
      const html = highlight(content, language);
      // The component receives compiler-produced, escaped Prism markup, not arbitrary source HTML.
      if (html) node.properties['data-highlighted-html'] = html;
    }); };
  }

  function plugin(platform) {
    return {name: 'lithosharp-mdx', setup(builder) {
      builder.onResolve({filter: /^@lithosharp\/runtime$/}, () => ({path: path.join(directory, 'runtime', 'components.mjs')}));
      builder.onResolve({filter: /^@docusaurus\/BrowserOnly$/}, () => ({path: 'BrowserOnly', namespace: 'theme'}));
      builder.onResolve({filter: /^@theme\//}, args => {
        const name = args.path.slice(7);
        if (!['Tabs', 'TabItem', 'Admonition', 'Details', 'CodeBlock', 'TOCInline', 'Card', 'MDXComponents', 'BrowserOnly'].includes(name))
          return {errors: [{text: `Unsupported Docusaurus alias '${args.path}'.`}]};
        return {path: name, namespace: 'theme'};
      });
      builder.onLoad({filter: /.*/, namespace: 'theme'}, args => ({contents:
        `export {${args.path === 'MDXComponents' ? 'components' : args.path} as default} from ${JSON.stringify(path.join(directory, 'runtime', 'components.mjs'))};`, loader: 'js', resolveDir: directory}));
      builder.onResolve({filter: /^@site\//}, args => ({path: path.resolve(projectRoot, args.path.slice(6))}));
      builder.onResolve({filter: /\?raw$/}, args => ({path: path.resolve(args.resolveDir, args.path.slice(0, -4)), namespace: 'raw'}));
      builder.onLoad({filter: /.*/, namespace: 'raw'}, async args => ({contents: (await readInput(args.path)).toString('utf8'), loader: 'text'}));
      builder.onResolve({filter: /^(?:react|react-dom)(?:\/|$)/}, async args => {
        if (args.importer && !args.importer.startsWith('virtual:')) {
          try {
            const candidate = await realpath(createRequire(args.importer).resolve('react'));
            if (candidate !== reactFile && !inside(directory, args.importer)) return {errors: [{text: `Multiple React copies imported by ${relative(projectRoot, args.importer)}.`}]};
          } catch (error) { if (error.code !== 'MODULE_NOT_FOUND') throw error; }
        }
        const resolved = args.path === 'react' ? reactFile : resolvePackage(args.path);
        return {path: platform === 'node' ? pathToFileURL(resolved).href : resolved, external: platform === 'node'};
      });
      builder.onResolve({filter: /^@mdx-js\/react$/}, () => ({path: workerRequire.resolve('@mdx-js/react')}));
      builder.onResolve({filter: /^server-only$/}, () => platform === 'browser'
        ? {errors: [{text: 'A server-only module reached the browser graph.'}]} : {path: 'server-only', namespace: 'empty'});
      builder.onLoad({filter: /.*/, namespace: 'empty'}, () => ({contents: '', loader: 'js'}));
      if (platform === 'node') builder.onLoad({filter: /\.css$/}, async args => { await readInput(args.path); return {contents: '', loader: 'js'}; });
      builder.onLoad({filter: /\.(?:mdx|md)$/}, async args => {
        await readInput(args.path);
        const file = await realpath(args.path);
        const entry = bySource.get(file);
        const source = request.sources[relative(projectRoot, file)];
        if (source === undefined) throw Object.assign(new Error(`MDX import needs C# validation: ${relative(projectRoot, file)}`), {requiredSource: file});
        if (!compiled.has(file)) {
          const key = hash(JSON.stringify([file, source, entry?.props.frontMatter, entry?.title]));
          const cached = request.cacheable && moduleCache.get(key);
          if (cached && (await Promise.all(cached.dependencies.map(async ([file, fingerprint]) => hash(await readInput(file)) === fingerprint))).every(Boolean)) {
            compiled.set(file, cached.code); metadata.set(file, cached.info);
            return {contents: cached.code, loader: 'js', resolveDir: path.dirname(file)};
          }
          const info = {headings: [], links: [], text: []};
          metadata.set(file, info);
          let result;
          try {
            result = await compile({value: source, path: file}, {
              providerImportSource: '@mdx-js/react',
              remarkPlugins: [remarkGfm, remarkDirective, remarkMath, authoring(file, info)],
              rehypePlugins: [rehypeSlug, rehypeKatex, codeBlocks(info)],
              development: false
            });
          } catch (error) {
            return {errors: [{text: error.reason ?? error.message, location: {file: relative(projectRoot, file),
              line: error.line ?? error.place?.start?.line ?? 1, column: Math.max(0, (error.column ?? error.place?.start?.column ?? 1) - 1)}, detail: error}]};
          }
          compiled.set(file, String(result) + `\nexport const frontMatter=${JSON.stringify(entry?.props.frontMatter ?? {})};\nexport const toc=${JSON.stringify(info.headings)};\nexport const contentTitle=${JSON.stringify(entry?.title ?? info.headings[0]?.text ?? '')};`);
          compiledModules++;
          if (request.cacheable) remember(moduleCache, key, {code: compiled.get(file), info,
            dependencies: [...(moduleDependencies.get(file) ?? [])].map(file => [file, inputs.get(file)])});
        }
        return {contents: compiled.get(file), loader: 'js', resolveDir: path.dirname(file)};
      });
      builder.onLoad({filter: /\.(?:[cm]?js|jsx|tsx?|json|css|png|jpe?g|gif|svg|webp|avif|woff2?|ttf)$/}, async args => {
        const bytes = await readInput(args.path);
        const extension = path.extname(args.path).slice(1);
        const loader = ['js', 'mjs', 'cjs'].includes(extension) ? 'js'
          : ['jsx', 'ts', 'tsx', 'json'].includes(extension) ? extension
          : extension === 'css' ? args.path.endsWith('.module.css') ? 'local-css' : 'css' : 'file';
        return {contents: bytes, loader, resolveDir: path.dirname(args.path)};
      });
    }};
  }

  const serverDir = path.join(workRoot, 'server');
  const browserDir = path.join(workRoot, 'browser');
  await mkdir(serverDir, {recursive: true});
  await mkdir(browserDir, {recursive: true});
  const publicPath = request.assetBaseUrl.replace(/\/$/, '');
  const common = {
    absWorkingDir: projectRoot, bundle: true, format: 'esm', metafile: true, write: false, jsx: 'automatic',
    logLevel: 'silent', nodePaths: [path.join(projectRoot, 'node_modules'), path.join(directory, 'node_modules')],
    assetNames: 'assets/[name]-[hash]', chunkNames: 'chunks/[name]-[hash]', entryNames: 'pages/[name]-[hash]',
    publicPath, loader: {'.png': 'file', '.jpg': 'file', '.jpeg': 'file', '.gif': 'file', '.svg': 'file', '.webp': 'file', '.avif': 'file', '.woff': 'file', '.woff2': 'file', '.ttf': 'file'},
    define: {'process.env.NODE_ENV': '"production"'}
  };
  const virtualServer = new Map();
  const virtualBrowser = new Map();
  for (const page of pages) {
    const props = JSON.stringify(page.props);
    const source = JSON.stringify(path.resolve(projectRoot, page.source));
    const setup = `import {createElement} from 'react';import Content from ${source};import {components,PageContext} from '@lithosharp/runtime';
      const value=${JSON.stringify({id: page.id, url: page.url, source: page.source, linkMap: request.linkMap, locale: page.locale, timestamp: request.timestamp, title: page.title, basePath: request.basePath})};
      export const element=createElement(PageContext.Provider,{value},createElement(Content,{...${props},components}));`;
    virtualServer.set(`virtual:${page.id}`, setup);
    virtualBrowser.set(`virtual:${page.id}`, setup + `\nimport {mountPage} from ${JSON.stringify(path.join(directory, 'runtime', 'browser.mjs'))};mountPage(${JSON.stringify(page.id)},element,${JSON.stringify(page.id + '-')});`);
  }
  const virtualPlugin = sources => ({name: 'entries', setup(builder) {
    builder.onResolve({filter: /^virtual:/}, args => ({path: args.path, namespace: 'virtual'}));
    builder.onLoad({filter: /.*/, namespace: 'virtual'}, args => ({contents: sources.get(args.path), loader: 'js', resolveDir: projectRoot}));
  }});
  const entries = Object.fromEntries(pages.map(page => [page.id, `virtual:${page.id}`]));
  if (!pages.length) return {pages: [], assets: [], inputs: [], compiledModules: 0, renderedPages: 0, bundledPages: 0};
  const server = await build({...common, entryPoints: entries, outdir: serverDir, platform: 'node', plugins: [virtualPlugin(virtualServer), plugin('node')]});
  for (const file of server.outputFiles) { await mkdir(path.dirname(file.path), {recursive: true}); await writeFile(file.path, file.contents); }
  const browser = await build({...common, entryPoints: entries, outdir: browserDir, platform: 'browser', splitting: true, minify: true,
    sourcemap: false, plugins: [virtualPlugin(virtualBrowser), plugin('browser')]});
  const browserOutputs = new Map(Object.entries(browser.metafile.outputs).map(([file, info]) => [path.resolve(projectRoot, file), info]));
  const assets = browser.outputFiles.map(file => {
    const info = browserOutputs.get(file.path);
    return {path: relative(browserDir, file.path), bytes: Buffer.from(file.contents).toString('base64'), hash: hash(file.contents),
      imports: (info?.imports ?? []).filter(item => !item.external).map(item => relative(browserDir, path.resolve(projectRoot, item.path))),
      inputs: Object.keys(info?.inputs ?? {}).filter(file => !file.startsWith('virtual:')).map(file => relative(projectRoot, path.resolve(projectRoot, file)))};
  });
  const results = [];
  for (const page of pages) {
    const serverEntry = Object.entries(server.metafile.outputs).find(([, output]) => output.entryPoint === `virtual:virtual:${page.id}` || output.entryPoint === `virtual:${page.id}`);
    if (!serverEntry) throw new Error(`No server entry was emitted for '${page.id}'.`);
    const serverPath = path.resolve(projectRoot, serverEntry[0]);
    const renderKey = hash(server.outputFiles.find(file => file.path === serverPath).contents);
    let html = request.cacheable ? renderCache.get(renderKey) : undefined;
    if (html === undefined) {
      const {element} = await import(pathToFileURL(serverPath).href);
      html = await new Promise((resolve, reject) => {
      const chunks = [];
      const stream = new PassThrough();
      stream.on('data', chunk => chunks.push(chunk));
      stream.on('error', reject);
      stream.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')));
      const rendered = renderToPipeableStream(element, {identifierPrefix: page.id + '-', onAllReady() { rendered.pipe(stream); }, onShellError: reject, onError: reject});
      });
      renderedPages++;
      if (request.cacheable) remember(renderCache, renderKey, html);
    }
    const browserEntry = Object.entries(browser.metafile.outputs).find(([, output]) => output.entryPoint === `virtual:virtual:${page.id}` || output.entryPoint === `virtual:${page.id}`);
    if (!browserEntry) throw new Error(`No browser entry was emitted for '${page.id}'.`);
    const info = metadata.get(path.resolve(projectRoot, page.source)) ?? {headings: [], links: [], text: []};
    results.push({id: page.id, html, entry: relative(browserDir, path.resolve(projectRoot, browserEntry[0])),
      css: browserEntry[1].cssBundle ? [relative(browserDir, path.resolve(projectRoot, browserEntry[1].cssBundle))] : [],
      headings: info.headings, links: info.links, text: info.text.join('\n')});
  }
  for (const input of new Set([...Object.keys(server.metafile.inputs), ...Object.keys(browser.metafile.inputs)])) {
    if (input.startsWith('virtual:') || input.startsWith('theme:') || input.startsWith('empty:')) continue;
    if (input.startsWith('raw:')) await readInput(input.slice(4));
    else await readInput(path.resolve(projectRoot, input));
  }
  return {pages: results, assets, inputs: [...inputs].map(([file, hash]) => ({file, hash})),
    compiledModules, renderedPages, bundledPages: pages.length};
}
