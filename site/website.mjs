// Static API entry pages do not load the MDX runtime. Keep its sidebar contract
// available on direct visits, then yield ownership when navigation starts MDX.
if (!document.querySelector('script[src*="/_docs/client-"],script[src$="/assets/site.js"]')) {
  const compact = matchMedia('(max-width:52rem)');
  const owned = () => !globalThis[Symbol.for('lithosharp.client')];
  const menu = document.querySelector('[data-docs-menu-toggle]');
  const sidebar = document.querySelector('[data-docs-sidebar]');
  const body = document.body;
  let desktopOpen = true;
  const setOpen = open => {
    if (!owned() || document.body !== body || !menu || !sidebar) return;
    if (!open && sidebar.contains(document.activeElement)) menu.focus();
    menu.setAttribute('aria-expanded', String(open));
    sidebar.classList.toggle('docs-sidebar-open', open);
    body.classList.toggle('docs-sidebar-collapsed', !open);
    sidebar.inert = !open;
    if (!compact.matches) desktopOpen = open;
  };
  if (menu && sidebar) {
    body.classList.add('docs-enhanced');
    menu.addEventListener('click', () => setOpen(menu.getAttribute('aria-expanded') !== 'true'));
    sidebar.addEventListener('click', event => { if (compact.matches && event.target.closest('a')) setOpen(false); });
    document.addEventListener('keydown', event => {
      if (owned() && event.key === 'Escape' && menu.getAttribute('aria-expanded') === 'true') { setOpen(false); menu.focus(); }
    });
    compact.addEventListener('change', () => setOpen(!compact.matches && desktopOpen));
    const header = document.querySelector('.docs-header');
    if (header) {
      const resize = new ResizeObserver(() => {
        if (!owned() || document.body !== body) { resize.disconnect(); return; }
        body.style.setProperty('--docs-header-height', `${header.getBoundingClientRect().height}px`);
      });
      resize.observe(header);
    }
    setOpen(!compact.matches);
  }
}
// The documentation runtime owns its pages; static API pages share its saved preference.
if (!document.querySelector('script[src*="/_docs/client-"]')) {
  const root = document.documentElement;
  const basePath = document.querySelector('script[src$="website-theme.js"]')?.dataset.basePath;
  const systemTheme = matchMedia('(prefers-color-scheme: dark)');
  let mode = root.dataset.themeMode || 'system';
  const apply = () => {
    if (globalThis[Symbol.for('lithosharp.client')]) return;
    root.dataset.themeMode = mode;
    root.dataset.theme = root.dataset.siteTheme = mode === 'system' ? systemTheme.matches ? 'dark' : 'light' : mode;
    root.style.colorScheme = root.dataset.theme;
    document.querySelectorAll('[data-ls-theme]').forEach(button => {
      button.hidden = false;
      button.textContent = `Theme: ${mode}`;
      button.setAttribute('aria-label', button.textContent);
    });
  };
  document.addEventListener('click', event => {
    if (globalThis[Symbol.for('lithosharp.client')] || !event.target.closest('[data-ls-theme]')) return;
    mode = mode === 'system' ? 'light' : mode === 'light' ? 'dark' : 'system';
    try { localStorage.setItem(`lithosharp:${basePath}:theme`, mode); } catch {}
    apply();
  });
  systemTheme.addEventListener('change', apply);
  apply();
}
const initialized = new WeakSet();
function revealApiCategory() {
  let id; try { id = decodeURIComponent(location.hash.slice(1)); } catch { return; }
  const target = document.getElementById(id);
  if (target?.matches('details.api-category-card')) target.open = true;
}
addEventListener('hashchange', revealApiCategory);
function enhance() {
  revealApiCategory();
  const toc = document.querySelector('.website-toc');
  if (toc && !initialized.has(toc)) { initialized.add(toc); toc.open = matchMedia('(min-width:80rem)').matches; }
  for (const button of document.querySelectorAll('[data-ls-theme]')) {
    if (initialized.has(button)) continue;
    initialized.add(button);
    const update = () => {
      const label = button.getAttribute('aria-label') || button.textContent;
      const english = /^Theme: (system|light|dark)$/i.exec(label);
      const localized = document.documentElement.lang.startsWith('ja') && english
        ? 'テーマ: ' + ({system:'システム設定', light:'ライト', dark:'ダーク'}[english[1].toLowerCase()]) : label;
      if (button.getAttribute('aria-label') !== localized) button.setAttribute('aria-label', localized);
      button.title = localized;
      button.dataset.themeMode = button.title.includes('システム') || /system/i.test(button.title) ? 'system' : document.documentElement.dataset.theme;
    };
    new MutationObserver(update).observe(button, {attributes:true, attributeFilter:['aria-label']});
    update();
  }
}
document.addEventListener('click', event => {
  if (!event.target.closest('.website-language')) document.querySelectorAll('.website-language[open]').forEach(details => { details.open = false; });
});
document.addEventListener('keydown', event => {
  if (event.key !== 'Escape') return;
  const details = document.querySelector('.website-language[open]');
  if (details) { event.preventDefault(); event.stopImmediatePropagation(); details.open = false; details.querySelector('summary').focus(); }
}, true);
document.addEventListener('lithosharp:page', enhance);
enhance();
matchMedia('(min-width:80rem)').addEventListener('change', event => { const toc = document.querySelector('.website-toc'); if (toc) toc.open = event.matches; });
