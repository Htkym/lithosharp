import React, {Children, createElement as h, useContext, useEffect, useId, useRef, useState} from 'react';
import {PageContext} from './context.mjs';
import {Island} from './islands.mjs';
export {PageContext, Island};
import './style.css';
import 'katex/dist/katex.min.css';

export const usePageContext = () => useContext(PageContext);
export function useTranslation() {
  const page = usePageContext();
  return (key, fallback) => page.messages?.[key] ?? fallback ?? key;
}
export function Translate({id, children}) { return useTranslation()(id, children); }
export function FormattedDate({value, timeZone = 'UTC', ...options}) {
  const page = usePageContext();
  return h('time', {dateTime: value}, new Intl.DateTimeFormat(page.locale || 'en', {timeZone, ...options}).format(new Date(value)));
}
const plainText = value => typeof value === 'string' || typeof value === 'number' ? String(value)
  : Children.toArray(value).map(child => child?.props ? plainText(child.props.children) : typeof child === 'string' ? child : '').join('');
const storageKey = (page, group) => `lithosharp:${page.basePath}:tabs:${group}`;

export function Link({href = '', children, ...props}) {
  const page = useContext(PageContext);
  if (/^\s*(?:javascript|vbscript|data):/i.test(href)) throw new Error('Unsafe link protocol.');
  if (page.linkMap && /\.(?:md|mdx)(?:[?#]|$)/.test(href)) {
    const source = new URL(href, `https://source.invalid/${page.source}`);
    const target = page.linkMap[decodeURIComponent(source.pathname.slice(1))];
    if (!target) throw new Error(`Unresolved document link '${href}'.`);
    href = target + source.search + source.hash;
  }
  return h('a', {...props, href}, children);
}

export function Admonition({type = 'note', title, children}) {
  return h('aside', {className: `mdx-admonition mdx-${type}`, role: 'note'},
    h('strong', null, title ?? type), children);
}

export function TabItem({children}) { return h(React.Fragment, null, children); }

export function Tabs({children, defaultValue, groupId, queryString, values, ...props}) {
  const page = useContext(PageContext);
  const id = useId();
  const items = Children.toArray(children).filter(child => child?.props);
  const choices = items.map((item, index) => ({value: String(item.props.value ?? values?.[index]?.value ?? index),
    label: item.props.label ?? values?.[index]?.label ?? String(item.props.value ?? index)}));
  const initial = choices.find(choice => choice.value === String(defaultValue))?.value ?? choices[0]?.value;
  const [selected, setSelected] = useState(initial);
  const group = groupId ?? id;
  const query = typeof queryString === 'string' ? queryString : queryString ? group : null;
  useEffect(() => {
    const select = value => { if (choices.some(choice => choice.value === value)) setSelected(value); };
    try { select((query && new URL(location.href).searchParams.get(query)) || localStorage.getItem(storageKey(page, group))); } catch { /* Storage is optional. */ }
    const receive = event => { if (groupId && event.detail.group === groupId) select(event.detail.value); };
    document.addEventListener('lithosharp:tab', receive);
    return () => document.removeEventListener('lithosharp:tab', receive);
  }, [groupId, group, query, page.basePath]);
  function choose(value) {
    setSelected(value);
    try { localStorage.setItem(storageKey(page, group), value); } catch { /* Storage is optional. */ }
    if (query) { const url = new URL(location.href); url.searchParams.set(query, value); history.replaceState(history.state, '', url); }
    if (groupId) document.dispatchEvent(new CustomEvent('lithosharp:tab', {detail: {group: groupId, value}}));
  }
  return h('div', {className: 'mdx-tabs'},
    h('noscript', null, h('style', null, '.mdx-tabs [role="tabpanel"][hidden]{display:block}.mdx-tabs [role="tablist"]{display:none}')),
    h('div', {role: 'tablist', 'aria-label': props['aria-label'] ?? groupId ?? 'Content tabs'}, choices.map((choice, index) =>
      h('button', {type: 'button', role: 'tab', id: `${id}-tab-${index}`, key: choice.value,
        'aria-controls': `${id}-panel-${index}`, 'aria-selected': selected === choice.value, tabIndex: selected === choice.value ? 0 : -1,
        onClick: () => choose(choice.value), onKeyDown: event => {
          const direction = event.key === 'ArrowRight' ? 1 : event.key === 'ArrowLeft' ? -1 : 0;
          const target = event.key === 'Home' ? 0 : event.key === 'End' ? choices.length - 1 : direction ? (index + direction + choices.length) % choices.length : -1;
          if (target >= 0) { event.preventDefault(); choose(choices[target].value); event.currentTarget.parentElement.children[target].focus(); }
        }}, choice.label))),
    items.map((item, index) => h('div', {role: 'tabpanel', id: `${id}-panel-${index}`, key: choices[index].value,
      'aria-labelledby': `${id}-tab-${index}`, hidden: selected !== choices[index].value, tabIndex: 0}, item.props.children)));
}

export function Details({summary = 'Details', children, ...props}) {
  return h('details', props, h('summary', null, summary), children);
}

export function CodeBlock({children, code, title, language, highlightedHtml, ...props}) {
  const t = useTranslation();
  const codeChild = Children.toArray(children).find(child => child?.props);
  const value = code ?? plainText(codeChild?.props.children ?? children);
  const classes = codeChild?.props.className ?? props.className ?? (language ? `language-${language}` : '');
  const lang = language ?? /language-([\w-]+)/.exec(classes)?.[1];
  const [status, setStatus] = useState('');
  const id = useId();
  if (lang === 'mermaid') return h(Mermaid, {chart: value, description: title ?? 'Diagram'});
  const html = highlightedHtml ?? props['data-highlighted-html'];
  const heading = title ?? props['data-title'];
  const start = Number(props['data-start'] ?? 1);
  const highlighted = String(props['data-highlight'] ?? '');
  const numbered = props['data-line-numbers'] === true || props.showLineNumbers === true;
  const selected = new Set(highlighted.split(',').flatMap(range => {
    const [from, to = from] = range.trim().split('-').map(Number);
    return Number.isInteger(from) && Number.isInteger(to) && from > 0 && to >= from && to - from < 10000
      ? Array.from({length: to - from + 1}, (_, index) => from + index) : [];
  }));
  const lines = value.replace(/\n$/, '').split('\n');
  const showLines = selected.size > 0 || lang === 'diff';
  return h('figure', {className: 'mdx-code'},
    heading ? h('figcaption', {id}, heading) : null,
    h('button', {type: 'button', className: 'mdx-copy', 'aria-label': t('copy', 'Copy code'), onClick: async () => {
      try { await navigator.clipboard.writeText(value); setStatus(t('copied', 'Copied')); } catch { setStatus(t('copyUnavailable', 'Copy unavailable')); }
    }}, t('copy', 'Copy')),
    h('span', {role: 'status', 'aria-live': 'polite', className: 'mdx-sr-only'}, status),
    h('pre', {'aria-labelledby': heading ? id : undefined, className: numbered ? 'mdx-numbered' : undefined,
      'data-highlight': highlighted, 'data-start': start},
      numbered ? h('span', {'aria-hidden': true, className: 'mdx-line-numbers'}, value.replace(/\n$/, '').split('\n').map((_, index) => h('span', {key: index}, start + index))) : null,
      h('code', {className: classes, ...(!showLines && html ? {dangerouslySetInnerHTML: {__html: html}} : {})},
        showLines ? lines.map((line, index) => h('span', {key: index, className: 'mdx-code-line' +
          (selected.has(index + 1) ? ' mdx-highlight' : '') + (lang === 'diff' && /^[+-]/.test(line) ? line[0] === '+' ? ' mdx-added' : ' mdx-removed' : '')}, line + '\n'))
          : html ? undefined : value)));
}

export function TOCInline({toc = [], minHeadingLevel = 2, maxHeadingLevel = 3}) {
  return h('nav', {'aria-label': 'On this page'}, h('ul', null, toc.filter(item => item.depth >= minHeadingLevel && item.depth <= maxHeadingLevel)
    .map((item, index) => h('li', {key: item.id ?? index}, h(Link, {href: '#' + item.id}, item.text ?? item.value)))));
}

export function Card({title, href, children}) { return h('article', {className: 'mdx-card'}, h('h3', null, h(Link, {href}, title)), children); }

export function BrowserOnly({children, fallback = null}) {
  const [mounted, setMounted] = useState(false);
  useEffect(() => { setMounted(true); }, []);
  return mounted ? (typeof children === 'function' ? children() : children) : fallback;
}

export function ClientOnly({load, fallback = null, ...props}) {
  const [component, setComponent] = useState(null);
  const [error, setError] = useState(null);
  useEffect(() => {
    let active = true;
    Promise.resolve().then(load).then(module => { if (active) setComponent(() => module.default ?? module); }, error => { if (active) setError(error); });
    return () => { active = false; };
  }, []);
  if (error) return h('div', {role: 'alert'}, 'Unable to load this component.', fallback);
  return component ? h(component, props) : fallback;
}

export function Mermaid({chart, children, description = 'Diagram'}) {
  const id = useId().replace(/[^a-z0-9]/gi, '');
  const [svg, setSvg] = useState(null);
  const [error, setError] = useState(null);
  const source = chart ?? plainText(children);
  useEffect(() => {
    let active = true;
    const render = async () => {
      try {
        const {default: mermaid} = await import('mermaid');
        mermaid.initialize({startOnLoad: false, securityLevel: 'strict', theme: document.documentElement.dataset.theme === 'dark' ? 'dark' : 'default'});
        const {svg} = await mermaid.render('diagram' + id, source);
        if (active) setSvg(svg);
      } catch (error) { if (active) setError(String(error)); }
    };
    render();
    document.addEventListener('lithosharp:theme', render);
    return () => { active = false; document.removeEventListener('lithosharp:theme', render); };
  }, [source, id]);
  return h('figure', {className: 'mdx-mermaid'}, h('figcaption', null, description),
    svg ? h('div', {role: 'img', 'aria-label': description, dangerouslySetInnerHTML: {__html: svg}}) : h('pre', null, h('code', null, source)),
    error ? h('p', {role: 'status'}, 'Diagram rendering unavailable; source is shown.') : null);
}

export const components = {a: Link, pre: CodeBlock, Tabs, TabItem, Admonition, Details, CodeBlock, TOCInline, Card, BrowserOnly, ClientOnly, Mermaid, Translate, FormattedDate, Island};
export default components;
