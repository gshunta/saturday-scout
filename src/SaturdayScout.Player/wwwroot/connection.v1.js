(() => {
    let dotnetReference;
    const banner = () => document.getElementById("player-connectivity-banner");

    function setConnection(connected) {
        document.documentElement.dataset.playerConnected = connected ? "true" : "false";
        const notice = banner();
        if (notice) notice.hidden = connected;

        for (const control of document.querySelectorAll("[data-connection-sensitive]")) {
            if (!connected) {
                if (!Object.prototype.hasOwnProperty.call(control.dataset, "disabledBeforeDisconnect")) {
                    control.dataset.disabledBeforeDisconnect = control.disabled ? "true" : "false";
                }
                control.disabled = true;
            } else if (Object.prototype.hasOwnProperty.call(control.dataset, "disabledBeforeDisconnect")) {
                control.disabled = control.dataset.disabledBeforeDisconnect === "true";
                delete control.dataset.disabledBeforeDisconnect;
            }
        }

        if (dotnetReference) {
            dotnetReference.invokeMethodAsync("OnConnectionChanged", connected).catch(() => {});
        }
    }

    window.addEventListener("saturday-scout:connection", event => setConnection(event.detail.connected));
    window.addEventListener("offline", () => setConnection(false));
    window.addEventListener("online", () => {
        // Network availability is not proof that the Blazor circuit has reconnected.
        // The reconnect modal emits the positive state after the circuit resumes.
        if (document.documentElement.dataset.playerConnected === "false") {
            const notice = banner();
            if (notice) notice.textContent = "Network available. Reconnecting to Saturday Scout…";
        }
    });

    window.saturdayScoutConnection = {
        attach(reference) {
            dotnetReference = reference;
            setConnection(navigator.onLine);
        },
        detach() { dotnetReference = undefined; }
    };
})();
