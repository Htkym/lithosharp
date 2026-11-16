(() => {
  const installed = Symbol.for('lithosharp.code-copy');
  if (globalThis[installed]) { globalThis[installed](); return; }
  const labels = () => {
    let messages = {};
    try { messages = JSON.parse(document.querySelector('[data-ls-messages]')?.textContent || '{}'); } catch { }
    const japanese = document.documentElement.lang.toLowerCase().startsWith('ja');
    return {
      copy: messages.copy ?? (japanese ? 'コピー' : 'Copy'),
      copied: messages.copied ?? (japanese ? 'コピーしました' : 'Copied'),
      failed: messages.copyUnavailable ?? (japanese ? 'コピーできませんでした。コードを選択してコピーしてください。' : 'Copy unavailable. Select the code and copy it manually.')
    };
  };
  async function copy(value) {
    try { if (navigator.clipboard?.writeText) { await navigator.clipboard.writeText(value); return; } } catch { }
    // Keep the fallback temporary, text-only and within the user's copy action.
    const active = document.activeElement, input = document.createElement('textarea');
    input.value = value; input.readOnly = true; input.tabIndex = -1;
    input.style.cssText = 'position:fixed;inset:0;opacity:0;pointer-events:none';
    document.body.append(input);
    try {
      input.focus({preventScroll:true}); input.select();
      if (!document.execCommand('copy')) throw new Error('Copy unavailable');
    } finally { input.remove(); if (active?.isConnected) active.focus({preventScroll:true}); }
  }
  function enhance() {
    const text = labels();
    for (const code of document.querySelectorAll('main pre > code')) {
      const pre = code.parentElement;
      if (pre.dataset.lsCopyReady || pre.closest('.mdx-code, [data-ls-no-copy]')) continue;
      pre.dataset.lsCopyReady = 'true';
      const tools = document.createElement('div'), button = document.createElement('button'), status = document.createElement('span');
      tools.className = 'ls-code-tools'; button.className = 'ls-code-copy'; button.type = 'button'; button.textContent = text.copy;
      status.className = 'ls-code-status'; status.setAttribute('role', 'status'); status.setAttribute('aria-live', 'polite');
      tools.append(button, status); pre.before(tools);
      let pending = false;
      button.addEventListener('click', async () => {
        if (pending) return;
        pending = true; button.setAttribute('aria-disabled', 'true'); status.textContent = '';
        try { await copy(code.textContent ?? ''); status.textContent = text.copied; }
        catch { status.textContent = text.failed; }
        finally { pending = false; button.removeAttribute('aria-disabled'); }
      });
    }
  }
  globalThis[installed] = enhance;
  document.addEventListener('lithosharp:page', enhance);
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', enhance, {once:true});
  else enhance();
})();
