import {readFile, readdir, realpath, mkdir, writeFile, stat} from 'node:fs/promises';
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

// Prism re-evaluates language files (re-registering their global hooks) when a
// later load modifies an already-loaded grammar. Page compilation runs
// concurrently, so the load order varies between machines and the same source
// could highlight differently (e.g. doubled language-xxxx classes on nested
// fences). Textually identical hook functions are never legitimately
// registered twice, so ignore exact duplicates to keep highlighting stable.
const seenPrismHooks = new Map();
const prismHooksAdd = Prism.hooks.add.bind(Prism.hooks);
Prism.hooks.add = (name, callback) => {
  let seen = seenPrismHooks.get(name);
  if (!seen) {
    seen = new Set();
    seenPrismHooks.set(name, seen);
  }
  const source = Function.prototype.toString.call(callback);
  if (seen.has(source)) {
    return;
  }
  seen.add(source);
  prismHooksAdd(name, callback);
};

const directory = path.dirname(fileURLToPath(import.meta.url));
const workerRequire = createRequire(import.meta.url);
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const relative = (root, file) => path.relative(root, file).split(path.sep).join('/');
const inside = (root, file) => { const value = path.relative(root, file); return value === '' || (!path.isAbsolute(value) && value !== '..' && !value.startsWith(`..${path.sep}`)); };
const textOf = node => node.type === 'text' || node.type === 'inlineCode' ? node.value : (node.children ?? []).map(textOf).join('');
// Bound each module/render cache by entry count and 16 MiB estimated retained payload.
const moduleCache = new Map();
const renderCache = new Map();
// Retain only the most recent browser build: 64 MiB payload budget, at most 20,000 dependencies.
// Reuse requires a caller-supplied resolution fingerprint as well as unchanged inputs.
let browserCache;
const cacheAccounting = new WeakMap();
function remember(cache, key, value, limit) {
  let accounting = cacheAccounting.get(cache);
  if (!accounting) { accounting = {bytes: 0, sizes: new Map()}; cacheAccounting.set(cache, accounting); }
  const bytes = (key.length + JSON.stringify(value).length) * 2 + 128;
  const budget = 16 * 1024 * 1024;
  if (bytes > budget) return;
  if (cache.has(key)) { accounting.bytes -= accounting.sizes.get(key); accounting.sizes.delete(key); cache.delete(key); }
  while (cache.size && (cache.size >= limit || accounting.bytes + bytes > budget)) {
    const oldest = cache.keys().next().value;
    accounting.bytes -= accounting.sizes.get(oldest); accounting.sizes.delete(oldest); cache.delete(oldest);
  }
  cache.set(key, value); accounting.sizes.set(key, bytes); accounting.bytes += bytes;
}
// These lists mirror DocusaurusProfile in src/LithoSharp/Documentation; the .NET profile tests parse them.
const supportedThemeComponents = ['Tabs', 'TabItem', 'Admonition', 'Details', 'CodeBlock', 'TOCInline', 'Card', 'DocCardList', 'MDXComponents', 'BrowserOnly', 'IdealImage', 'ThemedImage', 'Heading'];
const staticMdxComponents = ['Admonition', 'Details', 'Card', 'TOCInline', 'Translate', 'FormattedDate'];

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

export function highlight(code, language) {
  if (!language || language === 'text' || language === 'plain') return null;
  // Docusaurus mdx-code-block fences are unwrapped before compilation, but samples
  // displayed inside outer fences keep the language for Prism. Map the legacy name
  // to MDX so the highlighter does not log "Language does not exist" noise.
  const normalized = language === 'mdx-code-block' ? 'mdx' : language;
  if (!Prism.languages[normalized]) {
    if (!/^[a-z0-9-]+$/i.test(normalized)) throw new Error(`Invalid code language '${language}'.`);
    try { loadLanguages([normalized]); } catch { /* An unknown language uses escaped plain code. */ }
  }
  return Prism.languages[normalized] ? Prism.highlight(code, Prism.languages[normalized], normalized) : null;
}

// Docusaurus mdx-code-block fences wrap executable MDX (imports, component
// definitions, JSX open/close tags spanning markdown) rather than display code.
// The fence itself renders nothing; its inner content is hoisted as real MDX so
// later prose can use the imports and components it defines. Only top-level
// fences unwrap: mdx-code-block text inside outer fenced samples stays display.
export function unwrapMdxCodeBlocks(source) {
  const lines = source.split('\n');
  const output = [];
  let inFence = false;
  let fenceLength = 0;
  let fenceChar = '';
  let inMdxBlock = false;
  let mdxLength = 0;
  let mdxChar = '';
  for (const line of lines) {
    const trimmed = line.trimStart();
    const marker = /^(```+|~~~+)/.exec(trimmed)?.[1];
    if (marker) {
      const char = marker[0];
      const length = marker.length;
      if (inMdxBlock) {
        if (char === mdxChar && length >= mdxLength) {
          inMdxBlock = false;
          output.push('');
          continue;
        }
        output.push(line);
        continue;
      }
      if (!inFence) {
        const rest = trimmed.slice(length);
        if (/^mdx-code-block(?:\s|$)/.test(rest)) {
          inMdxBlock = true;
          mdxLength = length;
          mdxChar = char;
          output.push('');
          continue;
        }
        inFence = true;
        fenceLength = length;
        fenceChar = char;
        output.push(line);
        continue;
      }
      if (char === fenceChar && length >= fenceLength) inFence = false;
      output.push(line);
      continue;
    }
    if (!inFence && !inMdxBlock) {
      output.push(line);
      continue;
    }
    if (inMdxBlock) {
      output.push(line);
      continue;
    }
    output.push(line);
  }
  return output.join('\n');
}

// Analysis-only MDX inspection: shared preprocessing (mdx-code-block unwrap),
// the same MDX parser, and the permitted syntax transforms (directives, math,
// slugs, code metadata) as the build. It never loads user plugins or component
// modules, never resolves or executes imports, never bundles, and never runs
// SSR. CodeBlock `source` includes are left in place (no file reads at all),
// so analysis performs no file, process, or network work beyond parsing.
function analysisCollect(info) {
  return function () {
    return tree => {
      const imports = [];
      const imported = new Map();
      visit(tree, 'mdxjsEsm', node => {
        for (const statement of node.data?.estree?.body ?? []) {
          if (statement.type === 'ImportDeclaration') {
            imports.push({source: statement.source?.value ?? '',
              names: (statement.specifiers ?? []).map(specifier => specifier.local?.name).filter(Boolean),
              line: node.position?.start.line ?? 1});
            for (const specifier of statement.specifiers ?? []) imported.set(specifier.local?.name,
              {module: statement.source?.value ?? '', exportName: specifier.type === 'ImportDefaultSpecifier' ? 'default' : specifier.imported?.name});
          } else if (statement.type === 'ExportNamedDeclaration' || statement.type === 'ExportDefaultDeclaration') {
            const names = [...(statement.specifiers ?? []).map(item => item.exported?.name),
              ...(statement.declaration?.declarations ?? []).map(item => item.id?.name), statement.declaration?.id?.name];
            if (names.some(name => ['frontMatter', 'toc', 'contentTitle'].includes(name)))
              throw new Error('frontMatter, toc and contentTitle are reserved MDX metadata exports.');
          }
        }
      });
      info.imports = imports;
      visit(tree, node => {
        if (node.type === 'heading') info.headings.push({depth: node.depth, text: textOf(node), line: node.position?.start.line ?? 1});
        if (node.type === 'link' || node.type === 'image') info.links.push({url: node.url, line: node.position?.start.line ?? 1, image: node.type === 'image'});
        if (node.type === 'paragraph' || node.type === 'heading') info.text.push(textOf(node));
        if (node.type === 'containerDirective') {
          if (['note', 'tip', 'info', 'warning', 'danger', 'caution'].includes(node.name)) node.data = {...node.data, hName: 'aside', hProperties: {className: ['mdx-admonition', `mdx-${node.name}`], role: 'note', 'aria-label': node.name}};
          else node.data = {...node.data, hName: 'div', hProperties: {className: [node.name]}};
        }
        if (node.type === 'mdxJsxFlowElement' || node.type === 'mdxJsxTextElement') {
          if (node.name === 'Island') {
            const component = node.attributes.find(attribute => attribute.name === 'component');
            const name = component?.value?.data?.estree?.body?.[0]?.expression?.name ?? component?.value?.value;
            const binding = imported.get(name);
            if (!binding?.exportName) throw new Error('Island component must reference a statically imported default or named component.');
            info.islands.push({module: binding.module, exportName: binding.exportName});
          }
        }
      });
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

export async function analyzeMdx(request) {
  const info = {headings: [], links: [], text: [], islands: [], imports: []};
  const source = unwrapMdxCodeBlocks(request.text ?? '');
  try {
    await compile({value: source, path: request.sourcePath ?? 'document.mdx'}, {
      providerImportSource: '@mdx-js/react',
      remarkPlugins: [remarkGfm, remarkDirective, remarkMath, analysisCollect(info)],
      rehypePlugins: [rehypeSlug, rehypeKatex, codeBlocks(info)],
      development: false
    });
  } catch (error) {
    // Fatal syntax: no partial symbols (never resurface older results as latest).
    const start = error.place?.start;
    const message = error.reason ?? error.message ?? String(error);
    // MDX 3.1.1 encodes a source range in the stable syntax-error suffix even
    // when its VFileMessage fields are absent. Prefer that source position over
    // an esbuild/generated-code location supplied by a wrapper.
    const encodedPosition = /\((\d+):(\d+)-\d+:\d+\)\s*$/.exec(message);
    const line = encodedPosition ? Number(encodedPosition[1])
      : Number.isInteger(error.line) ? error.line : Number.isInteger(start?.line) ? start.line : null;
    const sourceColumn = encodedPosition ? Number(encodedPosition[2])
      : Number.isInteger(error.column) ? error.column : Number.isInteger(start?.column) ? start.column : null;
    return {headings: [], links: [], text: '', islands: [], imports: [],
      diagnostics: [{message,
        line,
        column: sourceColumn === null ? null : Math.max(0, sourceColumn - 1)}]};
  }
  return {headings: info.headings, links: info.links, text: info.text.join('\n'), islands: info.islands,
    imports: info.imports, diagnostics: []};
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
  const requiredInputs = new Set();
  const metadata = new Map();
  const compiled = new Map();
  let compiledModules = 0, renderedPages = 0;
  const moduleDependencies = new Map();
  const allowedRoots = [projectRoot, directory];
  const pages = request.pages;
  const pageIds = new Set();
  for (const page of pages) {
    if (typeof page.id !== 'string' || !page.id || /[\\/]/.test(page.id) || ['.', '..'].includes(page.id)) throw new Error('Invalid page id.');
    if (pageIds.has(page.id)) throw new Error('Duplicate page id.');
    pageIds.add(page.id);
  }
  const cardDirectories = new Map();
  for (const page of pages) {
    const folder = path.dirname(path.resolve(projectRoot, page.source));
    if (!cardDirectories.has(folder)) cardDirectories.set(folder, []);
    cardDirectories.get(folder).push([page.source, page.id, page.title, page.url, page.description]);
  }
  const cardKeys = new Map([...cardDirectories].map(([folder, cards]) => [folder, hash(JSON.stringify(cards))]));
  const sourceLoader = file => {
    const extension = path.extname(file).slice(1);
    if (['js', 'mjs', 'cjs'].includes(extension)) return 'js';
    if (['ts', 'mts', 'cts'].includes(extension)) return 'ts';
    if (['jsx', 'tsx', 'json'].includes(extension)) return extension;
    return null;
  };
  const cacheLimit = Math.min(20000, Math.max(2048, pages.length * 2));
  const bySource = new Map(pages.map(page => [path.resolve(projectRoot, page.source), page]));

  // esbuild can request every source at once; bound open files across all input readers.
  const reads = Array.from({length: 32}, () => Promise.resolve());
  let nextRead = 0;
  // Cache probes still check request-wide byte consistency, but only dependencies
  // of the current compilation are retained in its input/notice manifest.
  function readInput(file, required = true) {
    const slot = nextRead++ % reads.length;
    const result = reads[slot].then(async () => {
      const resolved = await realpath(file);
      if (!allowedRoots.some(root => inside(root, resolved))) throw new Error(`Import escapes the declared roots: ${relative(projectRoot, file)}`);
      // Compare the lexical and real path: symlinked imports do not bypass C# source validation.
      const comparable = value => process.platform === 'win32' ? value.toLowerCase() : value;
      if (comparable(path.resolve(file)) !== comparable(resolved)) throw new Error(`Symbolic imports are not supported: ${relative(projectRoot, file)}`);
      const bytes = await readFile(resolved);
      const fingerprint = hash(bytes);
      if (inputs.has(resolved) && inputs.get(resolved) !== fingerprint)
        throw new Error(`An MDX input changed during compilation: ${relative(projectRoot, file)}.`);
      inputs.set(resolved, fingerprint);
      if (required) requiredInputs.add(resolved);
      return bytes;
    });
    reads[slot] = result.catch(() => {});
    return result;
  }

  // esbuild-style extension and directory-index probing for fingerprinting.
  // Mirrors bundler resolution so extensionless and directory imports hash the
  // same bytes esbuild compiled instead of failing on the unresolved path.
  const probeExtensions = ['.tsx', '.ts', '.jsx', '.js', '.mjs', '.cjs', '.json', '.css'];
  async function resolveInputFile(file) {
    const candidates = [file];
    if (!path.extname(file)) {
      for (const extension of probeExtensions) candidates.push(file + extension);
      for (const extension of [''].concat(probeExtensions)) candidates.push(path.join(file, 'index' + extension));
    }
    for (const candidate of candidates) {
      try {
        if ((await stat(candidate)).isFile()) return candidate;
      } catch { /* Try the next candidate. */ }
    }
    throw new Error(`Cannot read file "${relative(projectRoot, file)}".`);
  }

  const extensions = {remark: [], rehype: []};
  for (const extension of request.plugins ?? []) {
    if (!Object.hasOwn(extensions, extension.stage)) throw new Error('Unsupported compiler plugin stage.');
    const file = extension.module.startsWith('.') ? path.resolve(projectRoot, extension.module) : projectRequire.resolve(extension.module);
    await readInput(file);
    const extensionBundle = await build({entryPoints: [file], bundle: true, platform: 'node', format: 'esm', write: false,
      banner: {js: "import {createRequire as __createRequire} from 'node:module';const require=__createRequire(import.meta.url);"},
      plugins: [{name: 'extension-inputs', setup(builder) { builder.onLoad({filter: /.*/, namespace: 'file'}, async args => {
        const loader = sourceLoader(args.path);
        if (!loader) throw new Error('Unsupported compiler plugin dependency: ' + relative(projectRoot, args.path));
        return {contents: await readInput(args.path), loader, resolveDir: path.dirname(args.path)};
      }); }}]});
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
          if (node.type === 'link' || node.type === 'image') info.links.push({url: node.url, line: node.position?.start.line ?? 1, image: node.type === 'image'});
          if (node.type === 'mdxFlowExpression' || node.type === 'mdxTextExpression') {
            const expression = node.data?.estree?.body?.[0]?.expression;
            if (expression?.type !== 'Literal' && !(expression?.type === 'MemberExpression' && expression.object?.name === 'frontMatter'))
              info.fallback ??= 'An arbitrary MDX expression requires page hydration.';
          }
          if (node.type === 'paragraph' || node.type === 'heading') info.text.push(textOf(node));
          if (node.type === 'containerDirective') {
            // Unknown directives keep their content in a plain div, matching the
            // Markdown pipeline instead of failing the build.
            if (['note', 'tip', 'info', 'warning', 'danger', 'caution'].includes(node.name)) node.data = {...node.data, hName: 'aside', hProperties: {className: ['mdx-admonition', `mdx-${node.name}`], role: 'note', 'aria-label': node.name}};
            else node.data = {...node.data, hName: 'div', hProperties: {className: [node.name]}};
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
            } else if (node.name && /^[A-Z]/.test(node.name) && ![...staticMdxComponents, ...(request.staticComponents ?? [])].includes(node.name)) {
              info.fallback ??= `Component '${node.name}' is not declared static or enclosed in an explicit Island.`;
            }
            if (node.type === 'mdxJsxFlowElement' && node.name === 'DocCardList') {
              // Bare DocCardList renders directory-sibling cards at compile time, matching
              // autogenerated navigation order. Variants with props stay explicit failures.
              if (node.attributes.some(attribute => attribute.type === 'mdxJsxAttribute')) throw new Error('DocCardList with props is not supported.');
              const directory = path.dirname(path.resolve(projectRoot, file));
              const siblings = (request.pages ?? []).filter(page => path.resolve(projectRoot, page.source) !== path.resolve(projectRoot, file) && path.dirname(path.resolve(projectRoot, page.source)) === directory);
              node.name = 'div';
              node.attributes = [{type: 'mdxJsxAttribute', name: 'className', value: 'mdx-doc-card-list'}];
              node.children = siblings.map(page => ({type: 'mdxJsxFlowElement', name: 'Card',
                attributes: [
                  {type: 'mdxJsxAttribute', name: 'title', value: page.title ?? page.id},
                  {type: 'mdxJsxAttribute', name: 'href', value: page.url},
                ],
                children: page.description ? [{type: 'text', value: page.description}] : []}));
            }
            if (node.name === 'CodeBlock') {              const attributes = new Map(node.attributes.filter(attribute => attribute.type === 'mdxJsxAttribute').map(attribute => [attribute.name, attribute]));
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

  function plugin(platform, browserExports = new Map(), browserDefaults = new Set(), runtimeInputs = new Map()) {
    const resolvedBrowser = file => browserExports.has(file) ? {path: browserExports.get(file), external: true} : {path: file};
    return {name: 'lithosharp-mdx', setup(builder) {
      if (browserExports.size) builder.onResolve({filter: /.*/}, args => [...browserExports.values()].includes(args.path) ? {path: args.path, external: true} : undefined);
      if (browserExports.size) builder.onResolve({filter: /^(?:\.|\/|[A-Za-z]:[\\/])/}, args => {
        const file = path.resolve(args.resolveDir, args.path);
        return browserExports.has(file) ? resolvedBrowser(file) : undefined;
      });
      if (browserExports.size) builder.onLoad({filter: /.*/, namespace: 'file'}, async args => {
        const file = await realpath(args.path);
        if (browserExports.has(file)) {
          await readInput(args.path);
          const url = JSON.stringify(browserExports.get(file));
          return {contents: `export * from ${url};${browserDefaults.has(file) ? `export {default} from ${url};` : ''}`, loader: 'js'};
        }
        // Unsupported private runtime entry points would create a second React/provider instance.
        if (/(?:^|[\\/])node_modules[\\/](?:react|react-dom|@mdx-js[\\/]react)[\\/]/.test(file))
          throw new Error('Direct private React or MDX runtime imports are not supported.');
        if (runtimeInputs.has(file) && sourceLoader(file))
          throw Object.assign(new Error('User code shares a runtime implementation module.'), {unifiedBrowserGraph: true});
      });
      builder.onResolve({filter: /^@lithosharp\/live-code$/}, () => resolvedBrowser(path.join(directory, 'runtime', 'live-code.mjs')));
      builder.onResolve({filter: /^lithosharp:live-runtime$/}, () => ({path: 'runtime', namespace: 'live-runtime'}));
      builder.onLoad({filter: /.*/, namespace: 'live-runtime'}, async () => {
        liveRuntime ??= build({stdin: {contents: `import React from ${JSON.stringify(reactFile)};import {createRoot} from ${JSON.stringify(resolvePackage('react-dom/client'))};globalThis.React=React;globalThis.render=value=>createRoot(document.getElementById('root')).render(value);`, resolveDir: projectRoot},
          bundle: true, write: false, platform: 'browser', format: 'iife', minify: true, define: {'process.env.NODE_ENV': '"production"'}, metafile: true, plugins: [plugin('browser')]});
        const result = await liveRuntime;
        for (const file of Object.keys(result.metafile.inputs).filter(file => file !== '<stdin>')) await readInput(path.resolve(file));
        return {contents: `export default ${JSON.stringify(result.outputFiles[0].text)}`, loader: 'js'};
      });
      builder.onResolve({filter: /^@lithosharp\/runtime$/}, () => resolvedBrowser(path.join(directory, 'runtime', 'components.mjs')));
      builder.onResolve({filter: /^@docusaurus\/BrowserOnly$/}, () => ({path: 'BrowserOnly', namespace: 'theme'}));
      builder.onResolve({filter: /^@docusaurus\/Link$/}, () => ({path: 'Link', namespace: 'theme'}));
      builder.onResolve({filter: /^@docusaurus\/Translate$/}, () => ({path: 'Translate', namespace: 'theme'}));
      // SSR-safe dummies for vendored site components. Direct use in documents stays
      // a migration-level manual item; these only let vendored components render statically.
      builder.onResolve({filter: /^@docusaurus\/useBrokenLinks$/}, () => ({path: 'useBrokenLinks', namespace: 'hooks'}));
      builder.onResolve({filter: /^@docusaurus\/useIsBrowser$/}, () => ({path: 'useIsBrowser', namespace: 'hooks'}));
      builder.onResolve({filter: /^@docusaurus\/router$/}, () => ({path: 'router', namespace: 'hooks'}));
      builder.onResolve({filter: /^@docusaurus\/theme-common$/}, () => ({path: 'theme-common', namespace: 'hooks'}));
      builder.onResolve({filter: /^@docusaurus\/plugin-content-docs\/client$/}, () => ({path: 'docs-client', namespace: 'hooks'}));
      builder.onLoad({filter: /.*/, namespace: 'hooks'}, args => {
        if (args.path === 'useBrokenLinks') return {contents: `export default function useBrokenLinks() { return {collectAnchor() {}, collectLink() {}}; }`, loader: 'js'};
        if (args.path === 'useIsBrowser') return {contents: `export default function useIsBrowser() { return false; }`, loader: 'js'};
        if (args.path === 'router') return {contents: [
          `export function useHistory() { return {location: {pathname: '', search: '', hash: ''}, push() {}, replace() {}}; }`,
          `export function useLocation() { return {pathname: '', search: '', hash: ''}; }`,
          `export default {useHistory, useLocation};`,
        ].join('\n'), loader: 'js'};
        if (args.path === 'docs-client') return {contents: [
          `const version = {name: 'current', label: 'current', path: '/docs'};`,
          `export function useLatestVersion() { return version; }`,
          `export function useActiveVersion() { return version; }`,
          `export function useVersions() { return [version]; }`,
          `export function useActiveDocContext() { return {activeVersion: version, activeDoc: null}; }`,
          `export function useAllDocsData() { return {}; }`,
          `export function useDocsSidebar() { return null; }`,
          `export function useDoc() { return null; }`,
          `export default {useLatestVersion, useActiveVersion, useVersions, useActiveDocContext, useAllDocsData, useDocsSidebar, useDoc};`,
        ].join('\n'), loader: 'js'};
        if (args.path === 'theme-common') return {contents: [
          `export function useColorMode() { return {colorMode: 'light', setColorMode() {}}; }`,
          `export function createStorageSlot() { let current = null; return {get: () => current, set: (value) => { current = value; }, del: () => { current = null; }, clear: () => { current = null; }}; }`,
          `export default {useColorMode, createStorageSlot};`,
        ].join('\n'), loader: 'js'};
        throw new Error(`Unknown hooks module '${args.path}'.`);
      });
      builder.onResolve({filter: /^@theme\//}, args => {
        const name = args.path.slice(7);
        if (!supportedThemeComponents.includes(name))
          return {errors: [{text: `Unsupported Docusaurus alias '${args.path}'.`}]};
        return {path: name, namespace: 'theme'};
      });
      builder.onLoad({filter: /.*/, namespace: 'theme'}, args => ({contents:
        `export {${args.path === 'MDXComponents' ? 'components' : args.path} as default} from ${JSON.stringify(path.join(directory, 'runtime', 'components.mjs'))};`, loader: 'js', resolveDir: directory}));
      builder.onResolve({filter: /^@site\//}, async args => ({path: await resolveInputFile(path.resolve(projectRoot, args.path.slice(6)))}));
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
        return platform === 'node' ? {path: pathToFileURL(resolved).href, external: true} : resolvedBrowser(resolved);
      });
      builder.onResolve({filter: /^@mdx-js\/react$/}, () => resolvedBrowser(workerRequire.resolve('@mdx-js/react')));
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
          const key = hash(JSON.stringify([file, source, entry?.props.frontMatter, entry?.title, request.plugins, extensionFingerprint, request.staticComponents, cardKeys.get(path.dirname(file))]));
          const cached = request.cacheable && moduleCache.get(key);
          if (cached && (await Promise.all(cached.dependencies.map(async ([file, fingerprint]) => hash(await readInput(file)) === fingerprint))).every(Boolean)) {
            compiled.set(file, cached.code); metadata.set(file, cached.info);
            return {contents: cached.code, loader: 'js', resolveDir: path.dirname(file)};
          }
          const info = {headings: [], links: [], text: []};
          metadata.set(file, info);
          let result;
          try {
            result = await compile({value: unwrapMdxCodeBlocks(source), path: file}, {
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
      builder.onLoad({filter: /.*/, namespace: 'file'}, async args => {
        const bytes = await readInput(args.path);
        const extension = path.extname(args.path).slice(1);
        const loader = sourceLoader(args.path) ?? (extension === 'css' ? args.path.endsWith('.module.css') ? 'local-css' : 'css' : common.loader['.' + extension]);
        if (!loader) throw new Error('Unsupported MDX dependency: ' + relative(projectRoot, args.path));
        return {contents: bytes, loader, resolveDir: path.dirname(args.path)};
      });
    }};
  }

  const serverDir = path.join(workRoot, 'server-' + hash(request.assetBaseUrl).slice(0, 16));
  const browserDir = path.join(workRoot, 'browser');
  await mkdir(serverDir, {recursive: true});
  await mkdir(browserDir, {recursive: true});
  const publicPath = request.assetBaseUrl.replace(/\/$/, '');
  const common = {
    absWorkingDir: projectRoot, bundle: true, format: 'esm', metafile: true, write: false, jsx: 'automatic', minifyWhitespace: true, minifySyntax: true, minifyIdentifiers: false,
    logLevel: 'silent', nodePaths: [path.join(projectRoot, 'node_modules'), path.join(directory, 'node_modules')],
    assetNames: 'assets/[name]-[hash]', chunkNames: 'chunks/[name]-[hash]', entryNames: 'pages/[name]-[hash]',
    publicPath, loader: {'.png': 'file', '.jpg': 'file', '.jpeg': 'file', '.gif': 'file', '.svg': 'file', '.webp': 'file', '.avif': 'file', '.woff': 'file', '.woff2': 'file', '.ttf': 'file', '.docx': 'file', '.pdf': 'file'},
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
  if (!pages.length) return {pages: [], assets: [], inputs: [], compiledModules: 0, renderedPages: 0, bundledPages: 0, rebundledPages: []};
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
  for (const file of server.outputFiles) { if (!inside(serverDir, file.path)) throw new Error('Server output escapes scratch.'); await mkdir(path.dirname(file.path), {recursive: true}); await writeFile(file.path, file.contents); }
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
      for (const item of output.imports.filter(item => !item.external)) visitOutput(path.resolve(projectRoot, item.path));
    }
    visitOutput(serverPath);
    const fallback = [...referencedInputs].map(file => metadata.get(path.resolve(projectRoot, file))?.fallback).find(Boolean)
      ?? info.fallback ?? (request.componentsModule ? 'A component override module requires page hydration.' : null);
    const selective = request.hydration === 'selective' && !fallback;
    const renderHasher = createHash('sha256').update(JSON.stringify([selective, request.assetBaseUrl]));
    for (const file of [...visitedOutputs].sort()) renderHasher.update(relative(serverDir, file)).update(serverContents.get(file));
    const renderKey = renderHasher.digest('hex');
    let renderedPage = request.cacheable ? renderCache.get(renderKey) : undefined;
    if (renderedPage === undefined) {
      const module = await import(pathToFileURL(serverPath).href);
      if (selective) module.selectIslands();
      const html = await new Promise((resolve, reject) => {
      const chunks = [];
      const stream = new PassThrough();
      let rendered, failed = false;
      const fail = error => { if (failed) return; failed = true;
        const cause = error instanceof Error ? error : new Error(String(error));
        cause.file ??= relative(projectRoot, path.resolve(projectRoot, page.source));
        reject(cause); stream.destroy(); rendered?.abort(); };
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
  const styleHashes = [...inputs].filter(([file]) => file.endsWith('.css')).sort(([left], [right]) => left.localeCompare(right, 'en'));
  const canCacheBrowser = request.cacheable && typeof request.resolutionFingerprint === 'string';
  const previousBrowser = canCacheBrowser ? browserCache : undefined;
  const emptyBrowser = () => ({outputFiles: [], metafile: {inputs: {}, outputs: {}}});
  const inputFile = name => /^(?:virtual|theme|empty|live-runtime|hooks):/.test(name) ? null
    : name.startsWith('raw:') ? name.slice(4) : path.resolve(projectRoot, name);
  const fingerprints = async names => {
    const values = new Map();
    for (const name of names) {
      const file = inputFile(name);
      if (file !== null) values.set(await realpath(file), hash(await readInput(file)));
    }
    return values;
  };
  const relocateBrowser = record => {
    const relocate = file => path.join(browserDir, relative(record.directory, path.resolve(projectRoot, file)));
    return {outputFiles: record.browser.outputFiles.map(file => ({contents: file.contents, path: relocate(file.path)})),
      metafile: {inputs: record.browser.metafile.inputs, outputs: Object.fromEntries(
        Object.entries(record.browser.metafile.outputs).map(([file, info]) => [relative(projectRoot, relocate(file)), {...info,
          ...(info.cssBundle ? {cssBundle: relative(projectRoot, relocate(info.cssBundle))} : {}),
          imports: info.imports.map(item => item.external ? item : {...item, path: relative(projectRoot, relocate(item.path))})}]))}};
  };
  const mergeBrowsers = (...parts) => {
    const files = new Map(), metadata = {inputs: {}, outputs: {}};
    for (const part of parts) {
      Object.assign(metadata.inputs, part.metafile.inputs);
      Object.assign(metadata.outputs, part.metafile.outputs);
      for (const file of part.outputFiles) {
        if (files.has(file.path) && !Buffer.from(files.get(file.path).contents).equals(Buffer.from(file.contents)))
          throw new Error('Browser output paths collide with different bytes.');
        files.set(file.path, file);
      }
    }
    return {outputFiles: [...files.values()], metafile: metadata};
  };
  let browserBundleMilliseconds = 0;
  const measureBrowser = async settings => {
    const started = performance.now();
    try { return await build(settings); }
    finally { browserBundleMilliseconds += performance.now() - started; }
  };

  // Give every page graph the same React, MDX provider, PageContext and cleanup modules.
  // Infer no exports from generated chunks: these facades export source-module APIs.
  const sharedSources = new Map(), sharedFiles = new Map(), sharedDefaults = new Set(), sharedEntries = {};
  for (const [name, specifier] of ['react', 'react-dom', 'react-dom/client', 'react/jsx-runtime', 'react/jsx-dev-runtime', 'react/compiler-runtime'].entries()) {
    const file = await realpath(specifier === 'react' ? reactFile : resolvePackage(specifier));
    const keys = Object.keys(projectRequire(file)).filter(key => /^[A-Za-z_$][\w$]*$/.test(key) && key !== 'default' && key !== '__esModule');
    const entry = `virtual:shared:${name}`;
    sharedFiles.set(file, entry); sharedDefaults.add(file); sharedEntries['react-' + name] = entry;
    sharedSources.set(entry, `import value from ${JSON.stringify(file)};export default value;export const {${keys.join(',')}}=value;`);
  }
  for (const [name, file, hasDefault] of [
    ['mdx', workerRequire.resolve('@mdx-js/react'), false],
    ...['components', 'context', 'browser', 'island-browser', 'islands', ...(inputs.has(path.join(directory, 'runtime', 'live-code.mjs')) ? ['live-code'] : [])].map(name =>
      [name, path.join(directory, 'runtime', name + '.mjs'), ['components', 'live-code'].includes(name)])]) {
    const entry = 'virtual:shared:' + name;
    sharedFiles.set(file, entry); if (hasDefault) sharedDefaults.add(file); sharedEntries[name] = entry;
    sharedSources.set(entry, `export * from ${JSON.stringify(file)};${hasDefault ? `export {default} from ${JSON.stringify(file)};` : ''}`);
  }
  sharedSources.set('virtual:site-links', virtualBrowser.get('virtual:site-links'));
  sharedEntries.links = 'virtual:site-links';
  const runtimeKey = hash(JSON.stringify([2, projectRoot, common, request.resolutionFingerprint, [...sharedSources]]));
  let runtime, runtimeDependencies, runtimeHit = false;
  const hasBrowserPages = Object.values(browserEntries).some(entry => entry.startsWith('virtual:'));
  if (hasBrowserPages && previousBrowser?.runtime.key === runtimeKey && previousBrowser.runtime.browser.outputFiles.some(file => file.path.endsWith('.js'))) {
    try {
      runtimeHit = (await Promise.all([...previousBrowser.runtime.dependencies].map(async ([file, fingerprint]) =>
        hash(await readInput(file, false)) === fingerprint))).every(Boolean);
    } catch { /* Resolve missing or unsafe inputs afresh. */ }
  }
  if (runtimeHit) {
    runtime = relocateBrowser(previousBrowser.runtime);
    runtimeDependencies = previousBrowser.runtime.dependencies;
    for (const file of runtimeDependencies.keys()) requiredInputs.add(file);
  } else if (hasBrowserPages) {
    runtime = await measureBrowser({...common, entryPoints: sharedEntries, entryNames: 'runtime/[name]-[hash]',
      outdir: browserDir, platform: 'browser', splitting: true, sourcemap: false,
      plugins: [virtualPlugin(sharedSources), plugin('browser')]});
    runtimeDependencies = await fingerprints(Object.keys(runtime.metafile.inputs));
  } else { runtime = emptyBrowser(); runtimeDependencies = new Map(); }
  const sharedUrls = new Map(Object.entries(runtime.metafile.outputs).filter(([, info]) => info.entryPoint)
    .filter(([file]) => file.endsWith('.js'))
    .map(([file, info]) => [info.entryPoint.replace(/^virtual:virtual:/, 'virtual:'), publicPath + '/' + relative(browserDir, path.resolve(projectRoot, file))]));
  const browserExports = new Map([...sharedFiles].map(([file, entry]) => [file, sharedUrls.get(entry)]).filter(([, url]) => url));
  const pageConfigKey = hash(JSON.stringify([2, projectRoot, common, request.resolutionFingerprint, request.plugins,
    request.staticComponents, styleHashes, [...sharedUrls]]));
  const records = new Map();
  const changed = new Set();
  const processed = new Set();
  const cachedPages = previousBrowser?.pageConfigKey === pageConfigKey ? previousBrowser : undefined;
  let unifiedBrowser = Boolean(cachedPages?.unifiedBrowser);
  const currentHashes = new Map(runtimeDependencies);
  if (cachedPages) for (const [file] of cachedPages.dependencies) {
    try { currentHashes.set(file, hash(await readInput(file, false))); }
    catch { currentHashes.set(file, null); }
  }
  const signature = (name, dependencies) => hash(JSON.stringify([browserEntries[name], virtualBrowser.get(browserEntries[name]),
    [...dependencies].filter(file => /\.(?:mdx|md)$/.test(file)).sort().map(file => [file, hash(compiled.get(file) ?? '')])]));
  for (const name of Object.keys(browserEntries)) {
    const record = cachedPages?.records.get(name);
    if (!record || record.signature !== signature(name, record.dependencies)
      || [...record.dependencies].some(file => currentHashes.get(file) !== cachedPages.dependencies.get(file))) changed.add(name);
    else records.set(name, record);
  }
  // Plain styles have no JS module identity, but their global page closures must update.
  if ([...changed].some(name => browserEntries[name].endsWith('.css')))
    for (const [name, entry] of Object.entries(browserEntries)) if (entry.startsWith('virtual:')) changed.add(name);
  // CSS module names are allocated across the complete graph. Keep that naming scope together.
  if (changed.size && [...inputs.keys(), ...(cachedPages?.dependencies.keys() ?? [])].some(file => file.endsWith('.module.css')))
    for (const name of Object.keys(browserEntries)) changed.add(name);
  const stateful = file => !browserExports.has(file) && (file.endsWith('.module.css') || !/\.(?:css|png|jpe?g|gif|svg|webp|avif|woff2?|ttf|docx|pdf)$/.test(file));
  const owners = new Map();
  if (cachedPages) for (const [name, record] of cachedPages.records) {
    if (!(name in browserEntries)) continue;
    for (const file of record.dependencies) if (stateful(file)) {
      if (!owners.has(file)) owners.set(file, new Set());
      owners.get(file).add(name);
    }
  }
  const expand = () => {
    const queue = [...changed];
    for (const name of queue) for (const file of cachedPages?.records.get(name)?.dependencies ?? []) {
      if (!stateful(file)) continue;
      for (const owner of owners.get(file) ?? []) if (!changed.has(owner)) { changed.add(owner); queue.push(owner); }
    }
  };
  expand();
  if (unifiedBrowser && changed.size) for (const name of Object.keys(browserEntries)) changed.add(name);
  const describe = async built => {
    const entryOutputs = new Map(Object.entries(built.metafile.outputs).filter(([file, info]) => info.entryPoint && !file.endsWith('.css') || info.entryPoint && !info.cssBundle)
      .map(([file, info]) => [info.entryPoint.replace(/^virtual:virtual:/, 'virtual:'), [relative(browserDir, path.resolve(projectRoot, file)), info]]));
    const described = new Map();
    for (const name of changed) {
      const value = browserEntries[name];
      const root = Object.keys(built.metafile.inputs).find(input => input.startsWith('virtual:') ? input.replace(/^virtual:virtual:/, 'virtual:') === value : path.resolve(projectRoot, input) === value);
      const inputNames = new Set(), queue = [root];
      while (queue.length) {
        const input = queue.pop();
        if (!input || inputNames.has(input)) continue;
        inputNames.add(input);
        for (const item of built.metafile.inputs[input]?.imports ?? []) if (!item.external) queue.push(item.path);
      }
      const dependencies = new Set((await fingerprints(inputNames)).keys());
      for (const file of dependencies) currentHashes.set(file, inputs.get(file));
      const emitted = entryOutputs.get(value) ?? entryOutputs.get(relative(projectRoot, value));
      if (!emitted) throw new Error(`No browser entry was emitted for '${name}'.`);
      described.set(name, {signature: signature(name, dependencies), dependencies, inputNames,
        root: emitted[0], css: emitted[1].cssBundle ? relative(browserDir, path.resolve(projectRoot, emitted[1].cssBundle)) : null});
    }
    return described;
  };
  let main = cachedPages ? relocateBrowser({browser: cachedPages.main, directory: cachedPages.directory}) : emptyBrowser();
  let attempts = 0;
  if (changed.size) while (true) {
    for (const name of changed) if (browserEntries[name].startsWith('virtual:')) processed.add(name);
    let built;
    try { built = await measureBrowser({...common, entryPoints: Object.fromEntries(Object.entries(browserEntries).filter(([name]) => changed.has(name))),
      outdir: browserDir, platform: 'browser', splitting: true, sourcemap: false,
      plugins: [...(unifiedBrowser ? [] : [{name: 'shared-links', setup(builder) { builder.onResolve({filter: /^virtual:site-links$/}, () => ({path: sharedUrls.get('virtual:site-links'), external: true})); }}]),
        virtualPlugin(virtualBrowser), plugin('browser', unifiedBrowser ? new Map() : browserExports, sharedDefaults, runtimeDependencies)]});
    } catch (error) {
      if (unifiedBrowser || !error.errors?.some(item => item.detail?.unifiedBrowserGraph)) throw error;
      // Preserve supported modules shared with runtime internals in one esbuild scope.
      unifiedBrowser = true; records.clear(); main = emptyBrowser();
      for (const name of Object.keys(browserEntries)) changed.add(name);
      continue;
    }
    const described = await describe(built);
    const count = changed.size;
    for (const record of described.values()) for (const file of record.dependencies) if (stateful(file))
      for (const owner of owners.get(file) ?? []) if (!changed.has(owner)) changed.add(owner);
    expand();
    if (changed.size !== count) {
      // At most two exploratory builds; further crossovers safely rebuild the whole graph.
      if (++attempts >= 2) for (const name of Object.keys(browserEntries)) changed.add(name);
      continue;
    }
    for (const [name, record] of described) records.set(name, record);
    main = mergeBrowsers(main, built);
    break;
  }
  // Keep only closures reachable from current entries. Removed imports/pages leave no stale assets.
  const outputs = new Map(Object.entries(main.metafile.outputs).map(([file, info]) => [relative(browserDir, path.resolve(projectRoot, file)), info]));
  const retained = new Set(), queue = [...records.values()].flatMap(record => [record.root, record.css].filter(Boolean));
  while (queue.length) {
    const name = queue.pop();
    if (retained.has(name)) continue;
    retained.add(name);
    const info = outputs.get(name);
    if (!info) throw new Error('A browser output closure is incomplete.');
    if (info.cssBundle) queue.push(relative(browserDir, path.resolve(projectRoot, info.cssBundle)));
    for (const item of info.imports) if (!item.external) queue.push(relative(browserDir, path.resolve(projectRoot, item.path)));
  }
  const retainedInputs = new Set([...records.values()].flatMap(record => [...record.inputNames]));
  main = {outputFiles: main.outputFiles.filter(file => retained.has(relative(browserDir, file.path))), metafile: {
    inputs: Object.fromEntries(Object.entries(main.metafile.inputs).filter(([name]) => retainedInputs.has(name))),
    outputs: Object.fromEntries(Object.entries(main.metafile.outputs).filter(([file]) => retained.has(relative(browserDir, path.resolve(projectRoot, file)))))}};
  const runtimeOutputs = new Map(Object.entries(runtime.metafile.outputs).map(([file, info]) => [relative(browserDir, path.resolve(projectRoot, file)), info]));
  const runtimeRetained = new Set();
  const runtimeQueue = (unifiedBrowser ? [] : Object.values(main.metafile.outputs)).flatMap(info => info.imports.filter(item => item.external && item.path.startsWith(publicPath + '/')).map(item => item.path.slice(publicPath.length + 1)));
  while (runtimeQueue.length) {
    const name = runtimeQueue.pop(); if (runtimeRetained.has(name)) continue;
    const info = runtimeOutputs.get(name); if (!info) throw new Error('A shared runtime output closure is incomplete.');
    runtimeRetained.add(name);
    if (info.cssBundle) runtimeQueue.push(relative(browserDir, path.resolve(projectRoot, info.cssBundle)));
    for (const item of info.imports) if (!item.external) runtimeQueue.push(relative(browserDir, path.resolve(projectRoot, item.path)));
  }
  const publishedRuntime = {outputFiles: runtime.outputFiles.filter(file => runtimeRetained.has(relative(browserDir, file.path))), metafile: {inputs: runtime.metafile.inputs,
    outputs: Object.fromEntries(Object.entries(runtime.metafile.outputs).filter(([file]) => runtimeRetained.has(relative(browserDir, path.resolve(projectRoot, file)))))}};
  const browser = mergeBrowsers(publishedRuntime, main);
  const dependencies = new Map([...records.values()].flatMap(record => [...record.dependencies].map(file => [file, currentHashes.get(file)])));
  for (const file of dependencies.keys()) requiredInputs.add(file);
  const references = [...records.values()].reduce((sum, record) => sum + record.dependencies.size + record.inputNames.size, 0);
  const cacheBytes = [...runtime.outputFiles, ...main.outputFiles].reduce((sum, file) => sum + file.contents.byteLength + file.path.length * 2, 0)
    + (JSON.stringify(runtime.metafile).length + JSON.stringify(main.metafile).length + runtimeKey.length + pageConfigKey.length + browserDir.length * 2) * 2 + [...dependencies, ...runtimeDependencies].reduce((sum, [file, fingerprint]) => sum + (file.length + fingerprint.length) * 2, 0)
    + [...records].reduce((sum, [name, record]) => sum + (name.length + record.signature.length + record.root.length + (record.css?.length ?? 0)
      + [...record.dependencies, ...record.inputNames].reduce((length, value) => length + value.length, 0)) * 2, 0);
  // Bound data payload/reference counts, not V8 object overhead or the process heap.
  browserCache = canCacheBrowser && dependencies.size + runtimeDependencies.size <= 20000 && references <= 40000 && cacheBytes <= 64 * 1024 * 1024
    ? {runtime: {key: runtimeKey, browser: runtime, directory: browserDir, dependencies: runtimeDependencies},
      main, directory: browserDir, pageConfigKey, records, dependencies, unifiedBrowser} : undefined;
  const browserOutputs = new Map(Object.entries(browser.metafile.outputs).map(([file, info]) => [path.resolve(projectRoot, file), info]));
  const pageEntries = new Map([...browserOutputs].filter(([file, info]) => info.entryPoint && relative(browserDir, file).startsWith('pages/')).map(entry => [entry[1].entryPoint.replace(/^virtual:virtual:/, 'virtual:'), entry]));
  const assets = browser.outputFiles.map(file => {
    const info = browserOutputs.get(file.path);
    return {path: relative(browserDir, file.path), bytes: Buffer.from(file.contents).toString('base64'), hash: hash(file.contents),
      imports: [...(info?.imports ?? []), ...(info?.cssBundle ? [{path: info.cssBundle, external: false}] : [])].filter(item => !item.external || item.path.startsWith(publicPath + '/')).map(item => item.external ? item.path.slice(publicPath.length + 1) : relative(browserDir, path.resolve(projectRoot, item.path))),
      inputs: Object.keys(info?.inputs ?? {}).filter(file => !file.startsWith('virtual:')).map(file => relative(projectRoot, path.resolve(projectRoot, file)))};
  });
  const neededServerCss = new Set(results.filter(page => page.hydration !== 'page').map(page => serverOutputs.get(serverEntries.get(`virtual:${page.id}`))?.cssBundle).filter(Boolean).map(file => path.resolve(projectRoot, file)));
  for (const file of server.outputFiles.filter(file => !/\.(?:js|css)$/.test(file.path) || neededServerCss.has(file.path))) {
    const name = relative(serverDir, file.path);
    const existing = assets.find(asset => asset.path === name);
    if (existing && !Buffer.from(existing.bytes, 'base64').equals(Buffer.from(file.contents))) throw new Error('Server and browser asset paths collide with different bytes.');
    if (!existing) {
      const info = serverOutputs.get(file.path);
      assets.push({path: name, bytes: Buffer.from(file.contents).toString('base64'), hash: hash(file.contents),
        imports: (info?.imports ?? []).filter(item => !item.external).map(item => relative(serverDir, path.resolve(projectRoot, item.path))), inputs: []});
    }
  }
  for (const page of results) {
    const entry = pageEntries.get(`virtual:${page.id}`);
    if (entry) {
      page.entry = relative(browserDir, entry[0]);
      if (entry[1].cssBundle) page.css.push(relative(browserDir, path.resolve(projectRoot, entry[1].cssBundle)));
    }
    if (page.hydration !== 'page') {
      const css = serverOutputs.get(serverEntries.get(`virtual:${page.id}`))?.cssBundle;
      if (css) page.css.push(relative(serverDir, path.resolve(projectRoot, css)));
    }
    if (page.hydration === 'page') page.css.push(...publishedRuntime.outputFiles.filter(file => file.path.endsWith('.css')).map(file => relative(browserDir, file.path)));
    page.css = [...new Set(page.css)];
  }
  // Count actual esbuild work, including unchanged entries processed in a shared build.
  // A validated browser cache hit does no browser bundling work.
  const rebundledPages = results.filter(page => processed.has(page.id)).map(page => page.id);
  for (const input of new Set([...Object.keys(server.metafile.inputs), ...Object.keys(browser.metafile.inputs)])) {
    if (input.startsWith('virtual:') || input.startsWith('theme:') || input.startsWith('empty:') || input.startsWith('live-runtime:') || input.startsWith('hooks:')) continue;
    if (input.startsWith('raw:')) await readInput(input.slice(4));
    else await readInput(await resolveInputFile(path.resolve(projectRoot, input)));
  }
  for (const file of inputs.keys()) if (!requiredInputs.has(file)) inputs.delete(file);
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
    compiledModules, renderedPages, bundledPages: rebundledPages.length, rebundledPages,
    timings: {serverBundleMilliseconds, renderMilliseconds, browserBundleMilliseconds, totalMilliseconds: performance.now() - started}, memory: process.memoryUsage()};
}
