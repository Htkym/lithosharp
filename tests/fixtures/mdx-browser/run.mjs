import { chromium } from 'playwright';
import { readFile } from 'node:fs/promises';
const browser = await chromium.launch();
try {
  for (const file of ['check.js', 'optional.js', 'analytics.js', 'failures.js']) {
    const context = await browser.newContext();
    try {
      const page = await context.newPage();
      page.setDefaultTimeout(15000);
      const check = new Function('return (' + await readFile(new URL(file, import.meta.url), 'utf8') + ')')();
      console.log(JSON.stringify({ file, result: await check(page) }));
    } finally { await context.close(); }
  }
} finally { await browser.close(); }
