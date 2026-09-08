self.addEventListener('install', event => event.waitUntil((async () => {
  const cache = await caches.open(config.cache);
  try { await cache.addAll(config.urls); }
  catch (error) { await caches.delete(config.cache); throw error; }
})()));
self.addEventListener('message', event => { if (event.data?.type === 'lithosharp:activate') self.skipWaiting(); });
self.addEventListener('activate', event => event.waitUntil((async () => {
  const previous = (await caches.keys()).filter(key => key.startsWith(config.prefix) && key !== config.cache);
  // Keep one previous generation for in-flight clients while controllerchange reloads their HTML.
  for (const key of previous.slice(0, -1)) await caches.delete(key);
  await self.clients.claim();
})()));
self.addEventListener('fetch', event => {
  if (event.request.method !== 'GET' || new URL(event.request.url).origin !== self.location.origin) return;
  event.respondWith((async () => {
    const current = await caches.open(config.cache);
    const cached = await current.match(event.request, {ignoreSearch: event.request.mode === 'navigate'});
    if (cached) return cached;
    try { return await fetch(event.request); }
    catch (error) { const previous = await caches.match(event.request); if (previous) return previous; throw error; }
  })());
});
