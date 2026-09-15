import { chromium } from 'playwright';
import check from './subpath.js';
const origin = process.argv[2];
if (!origin) throw new Error('Pass the served origin, e.g. http://127.0.0.1:4318.');
const browser = await chromium.launch();
try {
  const page = await browser.newPage();
  page.setDefaultTimeout(15000);
  console.log(JSON.stringify(await check(page, origin)));
} finally { await browser.close(); }
