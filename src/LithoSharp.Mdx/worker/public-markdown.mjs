import {createHash} from 'node:crypto';

export const publicMarkdownImplementationId = 'mdx-static-markdown/1';
const componentNames = new Set(['Admonition', 'Tabs', 'TabItem', 'Details']);
const ordinary = new Set(['root', 'paragraph', 'text', 'heading', 'emphasis', 'strong', 'delete',
  'inlineCode', 'code', 'list', 'listItem', 'blockquote', 'break', 'thematicBreak', 'link', 'image',
  'definition', 'linkReference', 'imageReference', 'table', 'tableRow', 'tableCell',
  'footnoteDefinition', 'footnoteReference', 'math', 'inlineMath']);
const notes = new Set(['note', 'tip', 'info', 'warning', 'danger', 'caution']);

// Consume the AST already being compiled for HTML. Preserve ordinary source bytes and
// replace only ranges owned by known wrappers; never execute expressions or serialize a new AST.
export function publicMarkdownFromTree(tree, source, options = {}) {
  const identity = {schemaVersion: 1, implementationId: publicMarkdownImplementationId,
    sourceHash: createHash('sha256').update(source).digest('hex')};
  const unsupported = reason => ({...identity, status: 'unsupported', markdown: null, reason});
  if (options.plugins?.length) return unsupported('compilerPlugins');
  if (options.componentsModule) return unsupported('componentOverrides');
  if (options.preprocessedSource !== undefined && options.preprocessedSource !== source)
    return unsupported('sourcePreprocessing');
  const imports = new Map();
  const edits = [];
  let failure;
  const fail = reason => { failure ??= reason; };
  const range = node => {
    const start = node.position?.start?.offset, end = node.position?.end?.offset;
    if (!Number.isSafeInteger(start) || !Number.isSafeInteger(end) || start < 0 || end < start || end > source.length) {
      fail('unknownSourceRange'); return null;
    }
    return {start, end};
  };
  const edit = (start, end, replacement) => edits.push({start, end, replacement});
  const blank = node => {
    const span = range(node);
    if (span) edit(span.start, span.end, source.slice(span.start, span.end).replace(/[^\r\n]/g, ' '));
  };
  const label = value => {
    if (typeof value !== 'string' || /[\x00-\x1f\x7f]/.test(value)) { fail('dynamicLabel'); return ''; }
    return value.replace(/[\\`*_{}\[\]()<>!|#~&]/g, '\\$&');
  };
  const labelText = node => {
    if (node.type === 'text' || node.type === 'inlineCode') return node.value;
    if (!['paragraph', 'emphasis', 'strong', 'delete'].includes(node.type)) { fail('dynamicLabel'); return ''; }
    return (node.children ?? []).map(labelText).join('');
  };
  const attributes = (node, allowed) => {
    const result = new Map();
    for (const attribute of node.attributes ?? []) {
      if (attribute.type !== 'mdxJsxAttribute' || !allowed.includes(attribute.name)
        || result.has(attribute.name) || (attribute.value !== null && typeof attribute.value !== 'string')) {
        fail('unsupportedAttribute'); return result;
      }
      result.set(attribute.name, attribute.value === null ? true : attribute.value);
    }
    return result;
  };
  const nameOf = node => imports.get(node.name) ?? node.name;
  function unwrap(node, heading) {
    const span = range(node);
    if (!span) return;
    const children = node.children ?? [];
    if (!children.length) { edit(span.start, span.end, heading); return; }
    const first = range(children[0]), last = range(children.at(-1));
    if (!first || !last || first.start < span.start || last.end > span.end || first.start > last.end) {
      fail('unknownSourceRange'); return;
    }
    edit(span.start, first.start, heading);
    edit(last.end, span.end, '\n\n');
  }
  for (const node of tree.children ?? []) {
    if (node.type !== 'mdxjsEsm') continue;
    const statements = node.data?.estree?.body;
    if (!statements?.length) { fail('moduleCode'); continue; }
    for (const statement of statements) {
      if (statement.type !== 'ImportDeclaration' || statement.attributes?.length || !statement.specifiers?.length) {
        fail('moduleCode'); continue;
      }
      const module = statement.source?.value;
      for (const specifier of statement.specifiers) {
        let component;
        if (specifier.type === 'ImportDefaultSpecifier' && typeof module === 'string' && module.startsWith('@theme/'))
          component = module.slice('@theme/'.length);
        else if (specifier.type === 'ImportSpecifier' && module === '@lithosharp/runtime')
          component = specifier.imported?.name;
        // The authoring pass treats these names specially, and JSX lowercase names are intrinsic.
        const local = specifier.local?.name;
        if (!componentNames.has(component) || !/^[A-Z]/.test(local ?? '') || imports.has(local)
          || ['Island', 'DocCardList', 'CodeBlock'].includes(local)
          || (componentNames.has(local) && local !== component)) {
          fail('unknownImport'); continue;
        }
        imports.set(specifier.local.name, component);
      }
    }
    blank(node);
  }
  function walk(node, parent, siblingIndex = 0) {
    if (failure) return;
    if (node.type === 'mdxjsEsm') { if (parent?.type !== 'root') fail('moduleCode'); return; }
    if (node.type === 'mdxFlowExpression' || node.type === 'mdxTextExpression') {
      // An empty ESTree program is an MDX comment/empty expression, not executable public text.
      if (node.data?.estree?.body?.length !== 0) { fail('expression'); return; }
      blank(node); return;
    }
    if (node.type === 'containerDirective') {
      if (!['root', 'mdxJsxFlowElement', 'containerDirective'].includes(parent?.type)) { fail('wrapperContext'); return; }
      if (!notes.has(node.name) || Object.keys(node.attributes ?? {}).length) { fail('unknownDirective'); return; }
      const span = range(node);
      if (!span) return;
      const firstLineEnd = source.indexOf('\n', span.start);
      let end = span.end;
      while (end > span.start && (source[end - 1] === '\n' || source[end - 1] === '\r')) end--;
      const closingStart = source.lastIndexOf('\n', end - 1) + 1;
      const closing = source.slice(closingStart, end).trim();
      if (firstLineEnd < 0 || firstLineEnd >= closingStart || closing.length < 3 || [...closing].some(char => char !== ':')) {
        fail('unknownDirectiveRange'); return;
      }
      const titleNode = node.children?.find(child => child.data?.directiveLabel);
      const heading = label(node.name) + (titleNode ? ': ' + label(labelText(titleNode)) : '');
      edit(span.start, firstLineEnd + 1, `**${heading}**\n\n`);
      edit(closingStart, span.end, '\n');
      for (const child of node.children ?? []) {
        if (child === titleNode) continue;
        const childRange = range(child);
        if (!childRange || childRange.start <= firstLineEnd || childRange.end > closingStart) { fail('unknownDirectiveRange'); return; }
        walk(child, node);
      }
      return;
    }
    if (node.type === 'mdxJsxFlowElement' || node.type === 'mdxJsxTextElement') {
      if (!['root', 'mdxJsxFlowElement', 'containerDirective'].includes(parent?.type)) { fail('wrapperContext'); return; }
      const name = nameOf(node);
      if (node.type !== 'mdxJsxFlowElement' || !componentNames.has(name)) { fail('unknownComponent'); return; }
      let heading = '';
      if (name === 'Admonition') {
        const props = attributes(node, ['type', 'title']);
        heading = `**${label(props.get('type') ?? 'note')}${props.has('title') ? ': ' + label(props.get('title')) : ''}**\n\n`;
      } else if (name === 'Details') {
        const props = attributes(node, ['summary', 'open']);
        heading = `**${label(props.get('summary') ?? 'Details')}**\n\n`;
      } else if (name === 'Tabs') {
        attributes(node, ['defaultValue', 'groupId', 'queryString']);
        if (!node.children?.length || node.children.some(child =>
          child.type !== 'mdxJsxFlowElement' || nameOf(child) !== 'TabItem')) { fail('unknownTabContent'); return; }
      } else {
        if (nameOf(parent ?? {}) !== 'Tabs') { fail('orphanTab'); return; }
        const props = attributes(node, ['value', 'label', 'default']);
        heading = `**${label(props.get('label') ?? props.get('value') ?? String(siblingIndex))}**\n\n`;
      }
      unwrap(node, heading);
    } else if (!ordinary.has(node.type)) { fail('unknownSyntax'); return; }
    for (let index = 0; index < (node.children?.length ?? 0); index++) walk(node.children[index], node, index);
  }
  walk(tree, null);
  if (failure) return unsupported(failure);
  edits.sort((a, b) => a.start - b.start || a.end - b.end);
  let cursor = 0, markdown = '';
  for (const item of edits) {
    if (item.start < cursor) return unsupported('overlappingEdits');
    markdown += source.slice(cursor, item.start) + item.replacement;
    cursor = item.end;
  }
  return {...identity, status: 'complete', markdown: markdown + source.slice(cursor), reason: null};
}
