if ("serviceWorker" in navigator && window.isSecureContext) {
    window.addEventListener("load", () => {
        navigator.serviceWorker.register("/service-worker.v1.js", { scope: "/", updateViaCache: "none" })
            .catch(() => { /* The online application remains usable when installation is unavailable. */ });
    }, { once: true });
}
