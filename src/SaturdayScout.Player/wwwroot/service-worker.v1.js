const CACHE_NAME = "saturday-scout-public-static-v2";
const PUBLIC_ASSETS = [
    "/manifest.v2.webmanifest",
    "/icons/scout-192.v1.svg",
    "/icons/scout-512.v1.svg",
    "/icons/apple-touch-icon.png"
];

self.addEventListener("install", event => {
    event.waitUntil(caches.open(CACHE_NAME)
        .then(cache => cache.addAll(PUBLIC_ASSETS))
        .then(() => self.skipWaiting()));
});

self.addEventListener("activate", event => {
    event.waitUntil(caches.keys()
        .then(keys => Promise.all(keys
            .filter(key => key.startsWith("saturday-scout-public-static-") && key !== CACHE_NAME)
            .map(key => caches.delete(key))))
        .then(() => self.clients.claim()));
});

self.addEventListener("fetch", event => {
    const request = event.request;
    const url = new URL(request.url);
    if (request.method !== "GET" || url.origin !== self.location.origin || !PUBLIC_ASSETS.includes(url.pathname)) {
        return;
    }

    event.respondWith(caches.open(CACHE_NAME).then(async cache => {
        const cached = await cache.match(request);
        if (cached) return cached;
        const response = await fetch(request);
        if (response.ok && response.type === "basic") await cache.put(request, response.clone());
        return response;
    }));
});
