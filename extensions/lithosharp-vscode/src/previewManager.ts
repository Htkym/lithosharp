import { randomBytes } from 'node:crypto';
import { isExternalUrlAllowed, joinUrl, PreviewTracker, resolvePreviewUrl, wrapperHtml, type InspectedRoute } from './preview.js';

/** Owned server reference. Stopping an owned server is allowed; others are left running. */
export interface PreviewServer {
  key: string;
  url: string | null;
  generation: number;
  owned: boolean;
  stop(): void;
}

/** Minimal WebView panel surface. */
export interface PreviewPanel {
  setHtml(html: string): void;
  postMessage(message: unknown): void;
  onDidDispose(callback: () => void): void;
  reveal(): void;
}

export interface PreviewHost {
  createPanel(title: string): PreviewPanel;
  openExternal(url: string): Promise<boolean>;
  showQuickPick<T extends { label: string; description?: string }>(items: T[]): Promise<T | undefined>;
  showMessage(message: string): void;
}

/**
 * Owns preview panels separately from server lifetimes. A server started only
 * for preview stops with its last panel; a user-started server never stops on
 * panel close. Port changes re-point panels instead of lingering on old origins.
 */
export class PreviewManager {
  private readonly panels = new Map<string, { panel: PreviewPanel; tracker: PreviewTracker; serverKey: string; owned: boolean; url: string }>();

  constructor(
    private readonly host: PreviewHost,
    private readonly stopServer: (serverKey: string) => void = () => {},
  ) {}

  /** Opens (or reveals) a preview for a source document. */
  async open(
    sourcePath: string,
    routes: InspectedRoute[],
    server: PreviewServer | null,
    dirty: boolean,
  ): Promise<void> {
    const target = resolvePreviewUrl(sourcePath, routes, server?.url ?? null);
    if (target.kind === 'unavailable') {
      this.host.showMessage(
        target.reason === 'no-server'
          ? 'LithoSharp: start the server first (LithoSharp: Start Server).'
          : 'LithoSharp: no published route for this document (unknown, draft, or unbuilt). The preview never guesses a URL.',
      );
      return;
    }
    let url: string;
    if (target.kind === 'choice') {
      const picked = await this.host.showQuickPick(
        target.candidates.map((candidate) => ({ label: candidate.publicPath, description: 'published route' })),
      );
      if (!picked || !server?.url) {
        return;
      }
      url = joinUrl(server.url, picked.label);
    } else {
      url = target.url;
    }
    const key = `${server!.key}${'\0'}${sourcePath}`;
    const existing = this.panels.get(key);
    if (existing) {
      existing.panel.reveal();
      return;
    }
    const tracker = new PreviewTracker();
    tracker.currentUrl(url);
    if (dirty) {
      tracker.onUnsaved(true);
    }
    const panel = this.host.createPanel(`Preview ${sourcePath.split('/').pop() ?? sourcePath}`);
    const origin = new URL(url).origin;
    panel.setHtml(wrapperHtml({ pageUrl: url, origin, nonce: randomBytes(16).toString('base64'), status: tracker.label() }));
    panel.onDidDispose(() => this.closed(key));
    this.panels.set(key, { panel, tracker, serverKey: server!.key, owned: server!.owned, url });
  }

  /** Follows server generations: reload on success, retain labeled output on failure. */
  onServerEvent(server: PreviewServer, event: { event: string; generation?: number | undefined; url?: string | undefined }): void {
    for (const entry of this.panels.values()) {
      if (entry.serverKey !== server.key) {
        continue;
      }
      if (event.event === 'rebuild-started') {
        entry.tracker.onRebuildStarted();
        this.refresh(entry);
      } else if (event.event === 'rebuild-succeeded') {
        entry.tracker.onRebuildSucceeded(event.generation ?? server.generation);
        const current = entry.tracker.currentUrl(entry.url);
        entry.panel.postMessage({ lithosharp: 'reload', url: current });
        this.refresh(entry);
      } else if (event.event === 'rebuild-failed') {
        entry.tracker.onRebuildFailed(event.generation ?? server.generation);
        this.refresh(entry);
      } else if (event.event === 'startup' && event.url && event.url !== entry.url) {
        // Port or server change: re-point instead of lingering on a stale origin.
        entry.url = event.url;
        entry.tracker.currentUrl(event.url);
        entry.tracker.onRebuildSucceeded(event.generation ?? 0);
        entry.panel.postMessage({ lithosharp: 'reload', url: event.url });
        this.refresh(entry);
      }
    }
  }

  /** Reloads the panel frame for its current generation. */
  refreshPanel(sourcePath: string, serverKey: string): void {
    const key = `${serverKey}${'\0'}${sourcePath}`;
    const entry = this.panels.get(key);
    if (!entry) {
      return;
    }
    entry.panel.postMessage({ lithosharp: 'reload', url: entry.url });
    this.refresh(entry);
  }

  /** Opens the panel URL in an external browser after validation. */
  async openExternal(sourcePath: string, serverKey: string): Promise<void> {
    const entry = this.panels.get(`${serverKey}${'\0'}${sourcePath}`);
    if (!entry) {
      this.host.showMessage('LithoSharp: open a preview first.');
      return;
    }
    if (!isExternalUrlAllowed(entry.url)) {
      this.host.showMessage('LithoSharp: refusing to open a non-http(s) preview URL.');
      return;
    }
    await this.host.openExternal(entry.url);
  }

  panelCount(): number {
    return this.panels.size;
  }

  closeAll(): void {
    // Disposal notifications drive actual cleanup through closed().
    this.panels.clear();
  }

  private refresh(entry: { panel: PreviewPanel; tracker: PreviewTracker; url: string }): void {
    const origin = new URL(entry.url).origin;
    entry.panel.setHtml(
      wrapperHtml({ pageUrl: entry.url, origin, nonce: randomBytes(16).toString('base64'), status: entry.tracker.label() }),
    );
  }

  private closed(key: string): void {
    const entry = this.panels.get(key);
    if (!entry) {
      return;
    }
    this.panels.delete(key);
    if (entry.owned) {
      const remaining = [...this.panels.values()].some((other) => other.serverKey === entry.serverKey);
      if (!remaining) {
        this.stopServer(entry.serverKey);
      }
    }
  }
}
