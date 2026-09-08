import {readFile, readdir, realpath, mkdir, writeFile} from 'node:fs/promises';
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
// ponytail: at most 20,000 session entries; use an LRU byte budget if larger declared corpora require it.
const moduleCache = new Map();
const renderCache = new Map();
function remember(cache, key, value, limit) { if (cache.size >= limit) cache.delete(cache.keys().next().value); cache.set(key, value); }

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
  const started = performance.now();
  const projectRoot = await realpath(request.projectRoot);
  const workRoot = await realpath(request.workRoot);
  if (inside(projectRoot, workRoot) && !request.allowWorkWithinProject) throw new Error('Worker scratch directory must be isolated from source.');
  const projectRequire = createRequire(path.join(projectRoot, 'package.json'));
  function resolvePackage(name) {
    try { return projectRequire.resolve(name); } catch (error) { if (error.code !== 'MODULE_NOT_FOUND') throw error; return workerRequire.resolve(name); }
  }
  const reactFile = await realpath(resolvePackage('react'));
  const projectReact = await import(pathToFileURL(reactFile));
  if ((projectReact.default ?? projectReact).version !== '19.2.4') throw new Error('The project React version must match the locked worker React version 19.2.4.');
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
  const cacheLimit = Math.min(20000, Math.max(2048, pages.length * 2));
  const bySource = new Map(pages.map(page => [path.resolve(projectRoot, page.source), page]));

  // esbuild can request every source at once; bound open files across all input readers.
  const reads = Array.from({length: 32}, () => Promise.resolve());
  let nextRead = 0;
  function readInput(file) {
    const slot = nextRead++ % reads.length;
    const result = reads[slot].then(async () => {
      const resolved = await realpath(file);
      if (!allowedRoots.some(root => inside(root, resolved))) throw new Error(`Import escapes the declared roots: ${relative(projectRoot, file)}`);
      // Compare the lexical and real path: symlinked imports do not bypass C# source validation.
      const comparable = value => process.platform === 'win32' ? value.toLowerCase() : value;
      if (comparable(path.resolve(file)) !== comparable(resolved)) throw new Error(`Symbolic imports are not supported: ${relative(projectRoot, file)}`);
      const bytes = await readFile(resolved);
      inputs.set(resolved, hash(bytes));
      return bytes;
    });
    reads[slot] = result.catch(() => {});
    return result;
  }

  const extensions = {remark: [], rehype: []};
  for (const extension of request.plugins ?? []) {
    if (!Object.hasOwn(extensions, extension.stage)) throw new Error('Unsupported compiler plugin stage.');
    const file = extension.module.startsWith('.') ? path.resolve(projectRoot, extension.module) : projectRequire.resolve(extension.module);
    await readInput(file);
    const extensionBundle = await build({entryPoints: [file], bundle: true, platform: 'node', format: 'esm', write: false,
      banner: {js: "import {createRequire as __createRequire} from 'node:module';const require=__createRequire(import.meta.url);"},
      plugins: [{name: 'extension-inputs', setup(builder) { builder.onLoad({filter: /\.(?:[cm]?js|ts|json)$/}, async args => ({contents: await readInput(args.path), loader: path.extname(args.path) === '.json' ? 'json' : path.extname(args.path) === '.ts' ? 'ts' : 'js'})); }}]});
    const extensionPath = path.join(workRoot, 'extension-' + hash(extensionBundle.outputFiles[0].contents) + '.mjs');
    await writeFile(extensionPath, extensionBundle.outputFiles[0].contents);
    const module = await import(pathToFileURL(extensionPath));
    if (typeof module.default !== 'function') throw new Error(`Compiler plugin '${extension.module}' must export a default function.`);
    extensions[extension.stage].push([module.default, extension.options]);
  }
  const extensionFingerprint = [...inputs];
  let liveRuntime;

  function authoring(file, info) {
    return function () {
      return async tree => {
        const jobs = [];
        const imported = new Map();
        visit(tree, 'mdxjsEsm', node => {
          for (const statement of node.data?.estree?.body ?? []) if (statement.type === 'ImportDeclaration')
            for (const specifier of statement.specifiers) imported.set(specifier.local.name, {module: statement.source.value,
              exportName: specifier.type === 'ImportDefaultSpecifier' ? 'default' : specifier.imported?.name});
        });
        info.fallback = null;
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
          if (node.type === 'mdxFlowExpression' || node.type === 'mdxTextExpression') {
            const expression = node.data?.estree?.body?.[0]?.expression;
            if (expression?.type !== 'Literal' && !(expression?.type === 'MemberExpression' && expression.object?.name === 'frontMatter'))
              info.fallback ??= 'An arbitrary MDX expression requires page hydration.';
          }
          if (node.type === 'paragraph' || node.type === 'heading') info.text.push(textOf(node));
          if (node.type === 'containerDirective') {
            if (!['note', 'tip', 'info', 'warning', 'danger', 'caution'].includes(node.name)) throw new Error(`Unsupported directive '${node.name}'.`);
            node.data = {...node.data, hName: 'aside', hProperties: {className: ['mdx-admonition', `mdx-${node.name}`], role: 'note', 'aria-label': node.name}};
          }
          if (node.type === 'code') {
            info.fallback ??= 'Code copy controls require page hydration; use an explicit Island for selective controls.';
            const meta = node.meta ?? '';
            node.data = {...node.data, hProperties: {
              'data-title': /(?:^|\s)title="([^"]*)"/.exec(meta)?.[1] ?? '',
              'data-highlight': /\{([\d, -]+)\}/.exec(meta)?.[1] ?? '',
              'data-start': Number(/(?:^|\s)start=(\d+)/.exec(meta)?.[1] ?? 1),
              'data-line-numbers': /(?:^|\s)showLineNumbers(?:\s|$)/.test(meta)
            }};
          }
          if (node.type === 'mdxJsxFlowElement' || node.type === 'mdxJsxTextElement') {
            if (node.name === 'Island') {
              const component = node.attributes.find(attribute => attribute.name === 'component');
              const name = component?.value?.data?.estree?.body?.[0]?.expression?.name ?? component?.value?.value;
              const binding = imported.get(name);
              if (!binding?.exportName) throw new Error('Island component must reference a statically imported default or named component.');
              const module = binding.module.startsWith('.') ? './' + relative(projectRoot, path.resolve(path.dirname(file), binding.module)) : binding.module;
              node.attributes.push({type: 'mdxJsxAttribute', name: 'module', value: module}, {type: 'mdxJsxAttribute', name: 'exportName', value: binding.exportName});
            } else if (node.name && /^[A-Z]/.test(node.name) && !['Admonition', 'Details', 'Card', 'TOCInline', 'Translate', 'FormattedDate', ...(request.staticComponents ?? [])].includes(node.name)) {
              info.fallback ??= `Component '${node.name}' is not declared static or enclosed in an explicit Island.`;
            }
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
      builder.onResolve({filter: /^@lithosharp\/live-code$/}, () => ({path: path.join(directory, 'runtime', 'live-code.mjs')}));
      builder.onResolve({filter: /^lithosharp:live-runtime$/}, () => ({path: 'runtime', namespace: 'live-runtime'}));
      builder.onLoad({filter: /.*/, namespace: 'live-runtime'}, async () => {
        liveRuntime ??= build({stdin: {contents: `import React from ${JSON.stringify(reactFile)};import {createRoot} from ${JSON.stringify(resolvePackage('react-dom/client'))};globalThis.React=React;globalThis.render=value=>createRoot(document.getElementById('root')).render(value);`, resolveDir: projectRoot},
          bundle: true, write: false, platform: 'browser', format: 'iife', minify: true, define: {'process.env.NODE_ENV': '"production"'}, metafile: true});
        const result = await liveRuntime;
        for (const file of Object.keys(result.metafile.inputs).filter(file => file !== '<stdin>')) await readInput(path.resolve(file));
        return {contents: `export default ${JSON.stringify(result.outputFiles[0].text)}`, loader: 'js'};
      });
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
      builder.onLoad({filter: /\.(?:mdx|md)$/}, async args => {
        await readInput(args.path);
        const file = await realpath(args.path);
        const entry = bySource.get(file);
        const source = request.sources[relative(projectRoot, file)];
        if (source === undefined) throw Object.assign(new Error(`MDX import needs C# validation: ${relative(projectRoot, file)}`), {requiredSource: file});
        if (!compiled.has(file)) {
          const key = hash(JSON.stringify([file, source, entry?.props.frontMatter, entry?.title, request.plugins, extensionFingerprint, request.staticComponents]));
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
              remarkPlugins: [remarkGfm, remarkDirective, remarkMath, ...extensions.remark, authoring(file, info)],
              rehypePlugins: [rehypeSlug, rehypeKatex, ...extensions.rehype, codeBlocks(info)],
              development: false
            });
          } catch (error) {
            return {errors: [{text: error.reason ?? error.message, location: {file: relative(projectRoot, file),
              line: error.line ?? error.place?.start?.line ?? 1, column: Math.max(0, (error.column ?? error.place?.start?.column ?? 1) - 1)}, detail: error}]};
          }
          compiled.set(file, String(result) + `\nexport const frontMatter=${JSON.stringify(entry?.props.frontMatter ?? {})};\nexport const toc=${JSON.stringify(info.headings)};\nexport const contentTitle=${JSON.stringify(entry?.title ?? info.headings[0]?.text ?? '')};`);
          compiledModules++;
          if (request.cacheable) remember(moduleCache, key, {code: compiled.get(file), info,
            dependencies: [...(moduleDependencies.get(file) ?? [])].map(file => [file, inputs.get(file)])}, cacheLimit);
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
    absWorkingDir: projectRoot, bundle: true, format: 'esm', metafile: true, write: false, jsx: 'automatic', minifyWhitespace: true, minifySyntax: true, minifyIdentifiers: false,
    logLevel: 'silent', nodePaths: [path.join(projectRoot, 'node_modules'), path.join(directory, 'node_modules')],
    assetNames: 'assets/[name]-[hash]', chunkNames: 'chunks/[name]-[hash]', entryNames: 'pages/[name]-[hash]',
    publicPath, loader: {'.png': 'file', '.jpg': 'file', '.jpeg': 'file', '.gif': 'file', '.svg': 'file', '.webp': 'file', '.avif': 'file', '.woff': 'file', '.woff2': 'file', '.ttf': 'file'},
    define: {'process.env.NODE_ENV': '"production"'}
  };
  const virtualServer = new Map();
  const virtualBrowser = new Map();
  const contexts = new Map();
  const linksModule = `export const linkMap=${JSON.stringify(request.linkMap)};export const crossReferences=${JSON.stringify(request.crossReferences ?? {})};`;
  virtualServer.set('virtual:site-links', linksModule);
  virtualBrowser.set('virtual:site-links', linksModule);
  for (const page of pages) {
    const props = JSON.stringify(page.props);
    const source = JSON.stringify(path.resolve(projectRoot, page.source));
    const value = {id: page.id, url: page.url, source: page.source, locale: page.locale, timestamp: request.timestamp, title: page.title, basePath: request.basePath, messages: page.props.messages ?? {}};
    contexts.set(page.id, value);
    const setup = `import {createElement} from 'react';import Content from ${source};import {components as defaults,PageContext,HydrationProbe} from '@lithosharp/runtime';
      ${request.componentsModule ? `import overrides from ${JSON.stringify(path.resolve(projectRoot, request.componentsModule))};` : 'const overrides={};'}const components={...defaults,...overrides};
      import {linkMap,crossReferences} from 'virtual:site-links';export const value={...${JSON.stringify(value)},linkMap,crossReferences};
      export const element=createElement(HydrationProbe,{id:${JSON.stringify(page.id)}},createElement(PageContext.Provider,{value},createElement(Content,{...${props},components})));`;
    virtualServer.set(`virtual:${page.id}`, setup + `\nvalue.usedLinks={};import {renderToString} from 'react-dom/server';export const islands=[];export function selectIslands(){value.islands=islands;value.renderIsland=(component,props,id)=>renderToString(createElement(HydrationProbe,{id},createElement(PageContext.Provider,{value:{...value,renderIsland:undefined,islands:undefined}},createElement(component,props))),{identifierPrefix:id+'-'});}`);
    virtualBrowser.set(`virtual:${page.id}`, setup + `\nimport {mountPage} from ${JSON.stringify(path.join(directory, 'runtime', 'browser.mjs'))};export function mount(){mountPage(${JSON.stringify(page.id)},element,${JSON.stringify(page.id + '-')});}mount();`);
  }
  const virtualPlugin = sources => ({name: 'entries', setup(builder) {
    builder.onResolve({filter: /^virtual:/}, args => ({path: args.path, namespace: 'virtual'}));
    builder.onLoad({filter: /.*/, namespace: 'virtual'}, args => ({contents: sources.get(args.path), loader: 'js', resolveDir: projectRoot}));
  }});
  const entries = Object.fromEntries(pages.map(page => [page.id, `virtual:${page.id}`]));
  if (!pages.length) return {pages: [], assets: [], inputs: [], compiledModules: 0, renderedPages: 0, bundledPages: 0};
  const serverStarted = performance.now();
  const server = await build({...common, entryPoints: entries, outdir: serverDir, platform: 'node', splitting: true, publicPath: '', plugins: [virtualPlugin(virtualServer), plugin('node')]});
  const serverBundleMilliseconds = performance.now() - serverStarted;
  const serverOutputs = new Map(Object.entries(server.metafile.outputs).map(([file, info]) => [path.resolve(projectRoot, file), info]));
  const serverEntries = new Map([...serverOutputs].filter(([, info]) => info.entryPoint).map(([file, info]) => [info.entryPoint.replace(/^virtual:virtual:/, 'virtual:'), file]));
  const serverAssets = server.outputFiles.filter(file => !/\.(?:js|css)$/.test(file.path));
  // Node chunks use local imports. Only file-loader asset literals need their final public URLs before React renders.
  for (const file of server.outputFiles.filter(file => file.path.endsWith('.js'))) {
    let source = Buffer.from(file.contents).toString('utf8');
    for (const asset of serverAssets) {
      let local = relative(path.dirname(file.path), asset.path); if (!local.startsWith('.')) local = './' + local;
      source = source.replaceAll(JSON.stringify(local), JSON.stringify(request.assetBaseUrl.replace(/\/$/, '') + '/' + relative(serverDir, asset.path)));
    }
    file.contents = Buffer.from(source);
  }
  for (const file of server.outputFiles) { await mkdir(path.dirname(file.path), {recursive: true}); await writeFile(file.path, file.contents); }
  const serverContents = new Map(server.outputFiles.map(file => [file.path, file.contents]));
  const results = [];
  const renderStarted = performance.now();
  for (const page of pages) {
    const serverPath = serverEntries.get(`virtual:${page.id}`);
    if (!serverPath) throw new Error(`No server entry was emitted for '${page.id}'.`);
    const info = metadata.get(path.resolve(projectRoot, page.source)) ?? {headings: [], links: [], text: [], fallback: null};
    const referencedInputs = new Set();
    const visitedOutputs = new Set();
    function visitOutput(file) {
      if (visitedOutputs.has(file)) return; visitedOutputs.add(file);
      const output = serverOutputs.get(file); if (!output) return;
      for (const input of Object.keys(output.inputs)) referencedInputs.add(input);
      for (const item of output.imports.filter(item => !item.external && item.kind !== 'dynamic-import')) visitOutput(path.resolve(projectRoot, item.path));
    }
    visitOutput(serverPath);
    const fallback = [...referencedInputs].map(file => metadata.get(path.resolve(projectRoot, file))?.fallback).find(Boolean)
      ?? info.fallback ?? (request.componentsModule ? 'A component override module requires page hydration.' : null);
    const selective = request.hydration === 'selective' && !fallback;
    const renderKey = hash(Buffer.concat([serverContents.get(serverPath), Buffer.from(String(selective))]));
    let renderedPage = request.cacheable ? renderCache.get(renderKey) : undefined;
    if (renderedPage === undefined) {
      const module = await import(pathToFileURL(serverPath).href);
      if (selective) module.selectIslands();
      const html = await new Promise((resolve, reject) => {
      const chunks = [];
      const stream = new PassThrough();
      let rendered, failed = false;
      const fail = error => { if (failed) return; failed = true; reject(error); stream.destroy(); rendered?.abort(); };
      stream.on('data', chunk => chunks.push(chunk));
      stream.on('error', fail);
      stream.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')));
      rendered = renderToPipeableStream(module.element, {identifierPrefix: page.id + '-', onAllReady() { if (!failed) rendered.pipe(stream); }, onShellError: fail, onError: fail});
      });
      renderedPage = {html, islands: module.islands, usedLinks: module.value.usedLinks};
      renderedPages++;
      if (request.cacheable) remember(renderCache, renderKey, renderedPage, cacheLimit);
    }
    if (selective) {
      if (!renderedPage.islands.length) virtualBrowser.delete(`virtual:${page.id}`);
      else virtualBrowser.set(`virtual:${page.id}`, `import {registerIsland} from ${JSON.stringify(path.join(directory, 'runtime', 'island-browser.mjs'))};import {linkMap,crossReferences} from 'virtual:site-links';const context={...${JSON.stringify(contexts.get(page.id))},linkMap,crossReferences};export function mount(){\n` + renderedPage.islands.map(island => {
        const specifier = island.module.startsWith('.') ? path.resolve(projectRoot, island.module) : island.module;
        return `registerIsland({...${JSON.stringify(island)},load:()=>import(${JSON.stringify(specifier)})},context);`;
      }).join('\n') + '}mount();');
    }
    results.push({id: page.id, ...renderedPage, entry: null, css: [], hydration: selective ? renderedPage.islands.length ? 'selective' : 'static' : 'page', fallback: request.hydration === 'selective' ? fallback : null,
      headings: info.headings, links: info.links, text: info.text.join('\n')});
  }
  const renderMilliseconds = performance.now() - renderStarted;
  const excludedSources = new Set(pages.filter(page => page.discoverable === false).map(page => page.source));
  const publicLinks = Object.fromEntries(Object.entries(request.linkMap ?? {}).filter(([source]) => !excludedSources.has(source)));
  for (const page of results) Object.assign(publicLinks, page.usedLinks);
  virtualBrowser.set('virtual:site-links', `export const linkMap=${JSON.stringify(publicLinks)};export const crossReferences=${JSON.stringify(request.crossReferences ?? {})};`);
  const browserEntries = Object.fromEntries(pages.filter(page => virtualBrowser.has(`virtual:${page.id}`)).map(page => [page.id, `virtual:${page.id}`]));
  const styleRoots = [...inputs.keys()].filter(file => file.endsWith('.css'))
    .map(file => [inside(projectRoot, file) ? relative(projectRoot, file) : '@worker/' + relative(directory, file), file])
    .sort(([left], [right]) => left < right ? -1 : left > right ? 1 : 0);
  if (results.some(page => page.hydration !== 'page'))
    for (const [name, file] of styleRoots) browserEntries['style-' + hash(name).slice(0, 12)] = file;
  const browserStarted = performance.now();
  const browser = Object.keys(browserEntries).length ? await build({...common, entryPoints: browserEntries, outdir: browserDir, platform: 'browser', splitting: true,
    sourcemap: false, plugins: [virtualPlugin(virtualBrowser), plugin('browser')]}) : {outputFiles: [], metafile: {inputs: {}, outputs: {}}};
  const browserBundleMilliseconds = performance.now() - browserStarted;
  const browserOutputs = new Map(Object.entries(browser.metafile.outputs).map(([file, info]) => [path.resolve(projectRoot, file), info]));
  const pageEntries = new Map([...browserOutputs].filter(([, info]) => info.entryPoint).map(entry => [entry[1].entryPoint.replace(/^virtual:virtual:/, 'virtual:'), entry]));
  const styleOrder = new Map(styleRoots.map(([, file], index) => [file, index]));
  const styleEntries = [...browserOutputs].filter(([file, info]) => file.endsWith('.css') && info.entryPoint && styleOrder.has(path.resolve(projectRoot, info.entryPoint)))
    .sort(([, left], [, right]) => styleOrder.get(path.resolve(projectRoot, left.entryPoint)) - styleOrder.get(path.resolve(projectRoot, right.entryPoint)))
    .map(([file]) => relative(browserDir, file));
  const assets = browser.outputFiles.map(file => {
    const info = browserOutputs.get(file.path);
    return {path: relative(browserDir, file.path), bytes: Buffer.from(file.contents).toString('base64'), hash: hash(file.contents),
      imports: (info?.imports ?? []).filter(item => !item.external).map(item => relative(browserDir, path.resolve(projectRoot, item.path))),
      inputs: Object.keys(info?.inputs ?? {}).filter(file => !file.startsWith('virtual:')).map(file => relative(projectRoot, path.resolve(projectRoot, file)))};
  });
  for (const file of server.outputFiles.filter(file => !/\.(?:js|css)$/.test(file.path))) {
    const name = relative(serverDir, file.path);
    if (!assets.some(asset => asset.path === name)) assets.push({path: name, bytes: Buffer.from(file.contents).toString('base64'), hash: hash(file.contents), imports: [], inputs: []});
  }
  for (const page of results) {
    const entry = pageEntries.get(`virtual:${page.id}`);
    if (entry) {
      page.entry = relative(browserDir, entry[0]);
      if (entry[1].cssBundle) page.css.push(relative(browserDir, path.resolve(projectRoot, entry[1].cssBundle)));
    }
    if (page.hydration !== 'page') page.css.push(...styleEntries);
    page.css = [...new Set(page.css)];
  }
  for (const input of new Set([...Object.keys(server.metafile.inputs), ...Object.keys(browser.metafile.inputs)])) {
    if (input.startsWith('virtual:') || input.startsWith('theme:') || input.startsWith('empty:') || input.startsWith('live-runtime:')) continue;
    if (input.startsWith('raw:')) await readInput(input.slice(4));
    else await readInput(path.resolve(projectRoot, input));
  }
  const packageRoots = new Set();
  for (const file of inputs.keys()) {
    const marker = path.sep + 'node_modules' + path.sep;
    const index = file.lastIndexOf(marker); if (index < 0) continue;
    const suffix = file.slice(index + marker.length).split(path.sep);
    packageRoots.add(file.slice(0, index + marker.length) + suffix.slice(0, suffix[0].startsWith('@') ? 2 : 1).join(path.sep));
  }
  const notices = new Map();
  for (const root of packageRoots) {
    const manifest = JSON.parse((await readInput(path.join(root, 'package.json'))).toString('utf8'));
    let license = '';
    const licenseName = (await readdir(root)).sort().find(name => /^licen[cs]e(?:\.md|\.txt|-mit)?$/i.test(name));
    if (licenseName) license = (await readInput(path.join(root, licenseName))).toString('utf8');
    notices.set(manifest.name + '@' + manifest.version, `${manifest.name}@${manifest.version}\nLicense: ${typeof manifest.license === 'string' ? manifest.license : JSON.stringify(manifest.license ?? 'See package distribution')}\n${license}\n`);
  }
  const noticeBytes = Buffer.from([...notices].sort(([left], [right]) => left.localeCompare(right, 'en')).map(([, text]) => text).join('\n---\n\n'));
  assets.push({path: 'third-party-notices.txt', bytes: noticeBytes.toString('base64'), hash: hash(noticeBytes), imports: [], inputs: []});
  return {pages: results, assets, inputs: [...inputs].map(([file, hash]) => ({file, hash})),
    compiledModules, renderedPages, bundledPages: results.filter(page => page.entry !== null).length,
    timings: {serverBundleMilliseconds, renderMilliseconds, browserBundleMilliseconds, totalMilliseconds: performance.now() - started}, memory: process.memoryUsage()};
}
