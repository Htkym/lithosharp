import assert from 'node:assert/strict';
import test from 'node:test';
import {
  isExternalUrlAllowed,
  joinUrl,
  PreviewTracker,
  resolvePreviewUrl,
  wrapperHtml,
  type InspectedRoute,
} from '../../src/preview.js';
import { PreviewManager, type PreviewHost, type PreviewPanel, type PreviewServer } from '../../src/previewManager.js';

const routes: InspectedRoute[] = [
  { path: 'index.html', publicPath: '/' },
  { path: 'docs/guide/index.html', publicPath: '/docs/guide/' },
  { path: 'docs/日本語/index.html', publicPath: '/docs/日本語/' },
  { path: 'typed/two/index.html', publicPath: '/typed/two/index.html' },
];

test('markdown resolves without guessing extensions', () => {
  const target = resolvePreviewUrl('docs/guide.md', routes, 'http://127.0.0.1:8080');
  assert.deepEqual(target, { kind: 'ready', url: 'http://127.0.0.1:8080/docs/guide/' });
});

test('root and sub-path join without double slashes', () => {
  assert.equal(joinUrl('http://h:1/', '/'), 'http://h:1/');
  assert.equal(joinUrl('http://h:1', 'typed/two/index.html'), 'http://h:1/typed/two/index.html');
  const target = resolvePreviewUrl('two.md', routes, 'http://127.0.0.1:8080/');
  assert.deepEqual(target, { kind: 'ready', url: 'http://127.0.0.1:8080/typed/two/index.html' });
});

test('Japanese routes survive', () => {
  const target = resolvePreviewUrl('日本語.md', routes, 'http://127.0.0.1:8080');
  assert.equal(target.kind, 'ready');
});

test('unknown and draft share an honest explanation', () => {
  assert.deepEqual(resolvePreviewUrl('missing.md', routes, 'http://127.0.0.1:1'), {
    kind: 'unavailable',
    reason: 'unknown-route',
  });
  assert.deepEqual(resolvePreviewUrl('draft.md', routes, 'http://127.0.0.1:1'), {
    kind: 'unavailable',
    reason: 'unknown-route',
  });
  assert.deepEqual(resolvePreviewUrl('guide.md', routes, null), { kind: 'unavailable', reason: 'no-server' });
});

test('multiple routes offer variants', () => {
  const target = resolvePreviewUrl('guide.md', [...routes, { path: 'v2/guide/index.html', publicPath: '/v2/guide/' }], 'http://h:1');
  assert.equal(target.kind, 'choice');
});

test('external URLs validate to http(s) only', () => {
  assert.equal(isExternalUrlAllowed('https://example.test/docs/'), true);
  assert.equal(isExternalUrlAllowed('http://127.0.0.1:8080/typed/one/'), true);
  for (const bad of ['command:lithosharp.build', 'javascript:alert(1)', 'data:text/html,<p>x</p>', 'ftp://h/x', 'not a url']) {
    assert.equal(isExternalUrlAllowed(bad), false);
  }
});

test('wrapper locks origins and scripts', () => {
  const html = wrapperHtml({ pageUrl: 'http://127.0.0.1:8080/docs/', origin: 'http://127.0.0.1:8080', nonce: 'n', status: 's' });
  assert.match(html, /default-src 'none'/);
  assert.match(html, /frame-src http:\/\/127\.0\.0\.1:8080/);
  assert.match(html, /script-src 'nonce-n'/);
  assert.match(html, /sandbox="allow-scripts allow-same-origin"/);
  assert.match(html, /event\.origin!==origin/);
  assert.match(html, /parsed\.protocol!=='http:'&&parsed\.protocol!=='https:'/);
  assert.ok(!html.includes(' * '));
  // The shell converts nothing: no Markdown rendering inside the wrapper.
  assert.ok(!html.includes('<em>') && !html.includes('mdx-admonition'));
});

test('tracker labels failures as last success', () => {
  const tracker = new PreviewTracker();
  tracker.currentUrl('http://h:1/a/');
  tracker.onRebuildStarted();
  assert.equal(tracker.label(), 'rebuilding…');
  tracker.onRebuildFailed(4);
  assert.match(tracker.label(), /last successful result/);
  assert.equal(tracker.currentUrl(null), 'http://h:1/a/');
  tracker.onRebuildSucceeded(5);
  assert.equal(tracker.label(), 'generation 5');
  tracker.onUnsaved(true);
  assert.match(tracker.label(), /unsaved/);
});

function hostDouble(): PreviewHost & {
  panels: { html: string[]; messages: unknown[]; dispose: () => void; disposers: (() => void)[] }[];
  messages: string[];
  external: string[];
} {
  const self = {
    panels: [] as { html: string[]; messages: unknown[]; dispose: () => void; disposers: (() => void)[] }[],
    messages: [] as string[],
    external: [] as string[],
    createPanel(_title: string): PreviewPanel {
      const panel = {
        html: [] as string[],
        messages: [] as unknown[],
        disposers: [] as (() => void)[],
        setHtml: (html: string) => {
          panel.html.push(html);
        },
        postMessage: (message: unknown) => {
          panel.messages.push(message);
        },
        onDidDispose: (callback: () => void) => {
          panel.disposers.push(callback);
        },
        reveal: () => {},
        dispose: () => {
          for (const dispose of panel.disposers) {
            dispose();
          }
        },
      };
      self.panels.push(panel);
      return panel;
    },
    openExternal: async (url: string): Promise<boolean> => {
      self.external.push(url);
      return true;
    },
    showQuickPick: async <T,>(items: T[]): Promise<T | undefined> => items[0],
    showMessage: (message: string) => {
      self.messages.push(message);
    },
  };
  return self;
}

function server(overrides: Partial<PreviewServer> = {}): PreviewServer {
  return {
    key: 'proj',
    url: 'http://127.0.0.1:8080',
    generation: 1,
    owned: true,
    stop: () => {},
    ...overrides,
  };
}

test('open shows real output and follows generations', async () => {
  const host = hostDouble();
  const manager = new PreviewManager(host, () => {});
  await manager.open('docs/guide.md', routes, server(), false);
  assert.equal(host.panels.length, 1);
  assert.match(host.panels[0]!.html[0]!, /http:\/\/127\.0\.0\.1:8080\/docs\/guide\//);
  manager.onServerEvent(server(), { event: 'rebuild-started' });
  manager.onServerEvent(server(), { event: 'rebuild-succeeded', generation: 2 });
  assert.deepEqual(host.panels[0]!.messages.at(-1), { lithosharp: 'reload', url: 'http://127.0.0.1:8080/docs/guide/' });
  assert.match(host.panels[0]!.html.at(-1)!, /generation 2/);
});

test('owned server stops with its last panel', async () => {
  let stops = 0;
  const host = hostDouble();
  const manager = new PreviewManager(host, () => {
    stops += 1;
  });
  await manager.open('docs/guide.md', routes, server({ owned: true }), false);
  await manager.open('index.md', routes, server({ owned: true }), false);
  host.panels[0]!.dispose();
  assert.equal(stops, 0);
  assert.equal(manager.panelCount(), 1);
  host.panels[1]!.dispose();
  assert.equal(stops, 1);
  assert.equal(manager.panelCount(), 0);
});

test('user-started servers survive panel close', async () => {
  let stops = 0;
  const host = hostDouble();
  const manager = new PreviewManager(host, () => {
    stops += 1;
  });
  await manager.open('docs/guide.md', routes, server({ owned: false }), false);
  host.panels[0]!.dispose();
  assert.equal(stops, 0);
});

test('port change repoints instead of lingering', async () => {
  const host = hostDouble();
  const manager = new PreviewManager(host, () => {});
  await manager.open('docs/guide.md', routes, server(), false);
  manager.onServerEvent(server({ url: 'http://127.0.0.1:9090', generation: 2 }), {
    event: 'startup',
    url: 'http://127.0.0.1:9090',
    generation: 2,
  });
  assert.deepEqual(host.panels[0]!.messages.at(-1), { lithosharp: 'reload', url: 'http://127.0.0.1:9090' });
  assert.match(host.panels[0]!.html.at(-1)!, /http:\/\/127\.0\.0\.1:9090/);
});

test('malicious urls never leave the panel', async () => {
  const host = hostDouble();
  const manager = new PreviewManager(host, () => {});
  await manager.open('missing.md', routes, server(), false);
  assert.equal(host.panels.length, 0);
  assert.match(host.messages[0]!, /never guesses/);
  await manager.openExternal('docs/guide.md', 'proj');
  assert.match(host.messages.at(-1)!, /open a preview first/);
});

test('panel reopen starts fresh', async () => {
  const host = hostDouble();
  const manager = new PreviewManager(host, () => {});
  await manager.open('docs/guide.md', routes, server(), false);
  host.panels[0]!.dispose();
  assert.equal(manager.panelCount(), 0);
  await manager.open('docs/guide.md', routes, server(), false);
  assert.equal(manager.panelCount(), 1);
  assert.equal(host.panels.length, 2);
});
