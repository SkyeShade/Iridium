// Caution! Be sure you understand the caveats before publishing an application with
// offline support. See https://aka.ms/blazor-offline-considerations

self.importScripts('./service-worker-assets.js');
self.addEventListener('install', event => event.waitUntil(onInstall(event)));
self.addEventListener('activate', event => event.waitUntil(onActivate(event)));
self.addEventListener('fetch', event => event.respondWith(onFetch(event)));

const cacheNamePrefix = 'offline-cache-';
const cacheName = `${cacheNamePrefix}${self.assetsManifest.version}`;
const offlineAssetsInclude = [ /\.dll$/, /\.pdb$/, /\.wasm/, /\.html/, /\.js$/, /\.json$/, /\.css$/, /\.woff$/, /\.png$/, /\.jpe?g$/, /\.gif$/, /\.ico$/, /\.blat$/, /\.dat$/, /\.webmanifest$/ ];
const offlineAssetsExclude = [ /^service-worker\.js$/, /^service-worker-assets\.js$/, /^index\.html$/ ];

// Replace with your base path if you are hosting on a subfolder. Ensure there is a trailing '/'.
const base = "/";
const baseUrl = new URL(base, self.origin);
async function onInstall(event) {
    console.info('Service worker: Install');

    // Fetch and cache all matching items from the assets manifest
    const assetsRequests = self.assetsManifest.assets
        .filter(asset => offlineAssetsInclude.some(pattern => pattern.test(asset.url)))
        .filter(asset => !offlineAssetsExclude.some(pattern => pattern.test(asset.url)))
        .map(asset => new Request(asset.url, { integrity: asset.hash, cache: 'no-cache' }));
    await caches.open(cacheName).then(cache => cache.addAll(assetsRequests));
    await self.skipWaiting();
}

async function onActivate(event) {
    console.info('Service worker: Activate');

    // Delete unused caches
    const cacheKeys = await caches.keys();
    await Promise.all(cacheKeys
        .filter(key => key.startsWith(cacheNamePrefix) && key !== cacheName)
        .map(key => caches.delete(key)));
    await self.clients.claim();
}

async function onFetch(event) {
    if (event.request.method !== 'GET') return fetch(event.request);
    const requestUrl = new URL(event.request.url);
    const isNavigation = event.request.mode === 'navigate';
    const mustRevalidate = isNavigation || requestUrl.pathname.endsWith('/index.html') ||
        requestUrl.pathname.endsWith('/service-worker.js') ||
        requestUrl.pathname.endsWith('/service-worker-assets.js') ||
        requestUrl.pathname.includes('/js/') || requestUrl.pathname.includes('/css/') ||
        requestUrl.pathname.endsWith('.styles.css') || requestUrl.pathname.endsWith('.webmanifest');
    if (mustRevalidate) {
        try { return await fetch(event.request, { cache: 'no-cache' }); }
        catch {
            const cached = await caches.match(event.request);
            if (cached) return cached;
            throw new Error('The requested Iridium asset is unavailable.');
        }
    }

    let cachedResponse = null;
    const cache = await caches.open(cacheName);
    cachedResponse = await cache.match(event.request);

    return cachedResponse || fetch(event.request);
}
