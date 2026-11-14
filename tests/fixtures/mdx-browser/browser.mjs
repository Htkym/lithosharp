import { chromium, firefox, webkit } from 'playwright';

export const browserName = process.env.LS_BROWSER ?? 'chromium';
export const browserType = { chromium, firefox, webkit }[browserName];
if (!browserType) throw new Error('LS_BROWSER must be chromium, firefox or webkit. WebKit is not an actual Safari check.');
