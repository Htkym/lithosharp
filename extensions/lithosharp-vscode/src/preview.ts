/** One inspected route: artifact path plus public path. */
export interface InspectedRoute {
  path: string;
  publicPath: string;
}

/** Resolution outcome. Routes are never guessed. */
export type PreviewTarget =
  | { kind: 'ready'; url: string }
  | { kind: 'choice'; candidates: { publicPath: string }[] }
  | { kind: 'unavailable'; reason: 'unknown-route' | 'no-server' };

/** Joins an origin and a public path without double slashes. */
export function joinUrl(origin: string, publicPath: string): string {
  const base = origin.replace(/\/+$/, '');
  const suffix = publicPath.startsWith('/') ? publicPath : `/${publicPath}`;
  return `${base}${suffix}`;
}

/** Only validated http/https URLs may leave the WebView. */
export function isExternalUrlAllowed(url: string): boolean {
  try {
    const parsed = new URL(url);
    return parsed.protocol === 'http:' || parsed.protocol === 'https:';
  } catch {
    return false;
  }
}

/**
 * Resolves the preview URL for a source document from inspected routes.
 * Matching is exact on normalized source suffixes; extensions are never
 * swapped and drafts share the unknown-route explanation instead of a guess.
 */
export function resolvePreviewUrl(
  sourcePath: string,
  routes: InspectedRoute[],
  servedOrigin: string | null,
): PreviewTarget {
  if (!servedOrigin) {
    return { kind: 'unavailable', reason: 'no-server' };
  }
  const normalized = sourcePath.replace(/\\/g, '/');
  const matches = routes.filter((route) => {
    const artifact = route.path;
    const stem = artifact.replace(/\/index\.html$/, '').replace(/\.html$/, '');
    const base = stem.split('/').pop() ?? '';
    const sourceBase = normalized.split('/').pop() ?? '';
    const sourceStem = sourceBase.replace(/\.mdx?$/, '');
    return base !== '' && (base === sourceStem || artifact === normalized);
  });
  if (matches.length === 0) {
    return { kind: 'unavailable', reason: 'unknown-route' };
  }
  if (matches.length > 1) {
    const unique = [...new Set(matches.map((route) => route.publicPath))].sort();
    if (unique.length > 1) {
      return { kind: 'choice', candidates: unique.map((publicPath) => ({ publicPath })) };
    }
    return { kind: 'ready', url: joinUrl(servedOrigin, unique[0]!) };
  }
  return { kind: 'ready', url: joinUrl(servedOrigin, matches[0]!.publicPath) };
}

/** Preview freshness per panel. Failures keep the last successful URL explicitly labeled. */
export type PreviewStatus =
  | { kind: 'current'; generation: number }
  | { kind: 'rebuilding' }
  | { kind: 'failed'; generation: number; lastUrl: string }
  | { kind: 'unsaved' };

export class PreviewTracker {
  private status: PreviewStatus = { kind: 'current', generation: 0 };
  private lastUrl: string | null = null;

  /** Current display URL: rebuilt on success, retained on failure. */
  currentUrl(newUrl: string | null): string | null {
    if (newUrl) {
      this.lastUrl = newUrl;
    }
    return this.lastUrl;
  }

  onRebuildStarted(): PreviewStatus {
    if (this.status.kind === 'current' || this.status.kind === 'failed') {
      this.status = { kind: 'rebuilding' };
    }
    return this.status;
  }

  onRebuildSucceeded(generation: number): PreviewStatus {
    this.status = { kind: 'current', generation };
    return this.status;
  }

  onRebuildFailed(generation: number): PreviewStatus {
    if (this.lastUrl) {
      this.status = { kind: 'failed', generation, lastUrl: this.lastUrl };
    }
    return this.status;
  }

  onUnsaved(dirty: boolean): PreviewStatus {
    if (dirty) {
      this.status = { kind: 'unsaved' };
    } else if (this.status.kind === 'unsaved') {
      this.status = { kind: 'current', generation: 0 };
    }
    return this.status;
  }

  get(): PreviewStatus {
    return this.status;
  }

  /** Human label. Failure explicitly names the last successful result. */
  label(): string {
    switch (this.status.kind) {
      case 'current':
        return `generation ${this.status.generation}`;
      case 'rebuilding':
        return 'rebuilding…';
      case 'failed':
        return `build failed — showing the last successful result (generation ${this.status.generation})`;
      case 'unsaved':
        return 'unsaved changes — preview shows the last saved build';
    }
  }
}

/**
 * Builds the thin wrapper page. The real site renders inside the iframe; this
 * shell converts nothing. The single message listener only accepts reloads
 * from the exact owning-server origin with http(s) URLs: forged or foreign
 * messages cannot redirect the frame.
 */
export function wrapperHtml(options: { pageUrl: string; origin: string; nonce: string; status: string }): string {
  const escaped = options.pageUrl.replace(/&/g, '&amp;').replace(/"/g, '&quot;');
  const originJson = JSON.stringify(options.origin);
  return `<!DOCTYPE html>
<html><head><meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; frame-src ${options.origin}; connect-src ${options.origin}; script-src 'nonce-${options.nonce}'; style-src 'unsafe-inline'; img-src ${options.origin} data:;">
<style>html,body{margin:0;height:100%}#bar{font:12px sans-serif;padding:4px 8px;background:#eee;color:#222}#frame{width:100%;height:calc(100% - 24px);border:0}</style>
</head><body><div id="bar">${options.status}</div>
<iframe id="frame" src="${escaped}" sandbox="allow-scripts allow-same-origin"></iframe>
<script nonce="${options.nonce}">(function(){var frame=document.getElementById('frame');var origin=${originJson};window.addEventListener('message',function(event){if(event.origin!==origin)return;var d=event.data;if(!d||d.lithosharp!=='reload'||typeof d.url!=='string')return;var parsed;try{parsed=new URL(d.url);}catch(e){return;}if((parsed.protocol!=='http:'&&parsed.protocol!=='https:')||parsed.origin!==origin)return;frame.src=d.url;});})();</script>
</body></html>`;
}
