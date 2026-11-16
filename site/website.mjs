const initialized = new WeakSet();
function enhance() {
  const toc = document.querySelector(".website-toc");
  if (toc && !initialized.has(toc)) { initialized.add(toc); toc.open = matchMedia("(min-width:80rem)").matches; }
  for (const details of document.querySelectorAll('.website-search,.website-language')) {
    if (initialized.has(details)) continue;
    initialized.add(details);
    details.addEventListener('toggle', () => {
      if (!details.open) return;
      for (const other of document.querySelectorAll('.website-search,.website-language')) if (other !== details) other.open = false;
      if (details.matches('.website-search')) details.querySelector('input')?.focus();
    });
  }
}
document.addEventListener('click', event => {
  const clear = event.target.closest('[data-website-search-clear]');
  if (clear) { const input = clear.closest('details').querySelector('input'); input.value = ''; input.dispatchEvent(new Event('input', {bubbles:true})); input.focus(); return; }
  const close = event.target.closest('[data-website-search-close]');
  if (close) {
    const details = close.closest('details'); details.open = false; details.querySelector('summary').focus();
  } else if (!event.target.closest('.website-search,.website-language')) {
    document.querySelectorAll('.website-search[open],.website-language[open]').forEach(details => { details.open = false; });
  }
});
document.addEventListener('keydown', event => {
  if ((event.ctrlKey || event.metaKey) && !event.altKey && event.key.toLowerCase() === 'k') { const search = document.querySelector('.website-search'); if (search) { event.preventDefault(); search.open = true; search.querySelector('input')?.focus(); } return; }
  if (event.key !== 'Escape') return;
  const details = document.querySelector('.website-search[open],.website-language[open]');
  if (details) { details.open = false; details.querySelector('summary').focus(); }
});
document.addEventListener('lithosharp:page', enhance);
enhance();


matchMedia("(min-width:80rem)").addEventListener("change", event => { const toc = document.querySelector(".website-toc"); if (toc) toc.open = event.matches; });
