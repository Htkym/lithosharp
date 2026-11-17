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
    setOpen(!compact.matches);
  }
}
// Reuse the product controller on static and MDX entry points.
globalThis[Symbol.for('lithosharp.theme')]?.start(
  document.querySelector('script[src$="website-theme.js"]')?.dataset.basePath || '/');
const initialized = new WeakSet();

function enhance() {
  const toc = document.querySelector('.website-toc');
  if (toc && !initialized.has(toc)) { initialized.add(toc); toc.open = matchMedia('(min-width:80rem)').matches; }

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
