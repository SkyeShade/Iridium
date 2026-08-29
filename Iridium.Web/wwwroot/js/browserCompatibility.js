(function () {
    async function copyText(value) {
        const text = String(value ?? "");
        if (typeof navigator.clipboard?.writeText === "function") {
            try {
                await navigator.clipboard.writeText(text);
                return;
            } catch {
                // Firefox requires a secure context and transient activation. Fall through to
                // the selection-based copy path when policy blocks the async Clipboard API.
            }
        }

        const activeElement = document.activeElement;
        const field = document.createElement("textarea");
        field.value = text;
        field.readOnly = true;
        field.setAttribute("aria-hidden", "true");
        field.style.position = "fixed";
        field.style.left = "-10000px";
        field.style.top = "0";
        document.body.appendChild(field);
        field.select();
        let copied = false;
        try { copied = typeof document.execCommand === "function" && document.execCommand("copy"); }
        finally {
            field.remove();
            if (activeElement instanceof HTMLElement && activeElement.isConnected) {
                try { activeElement.focus({ preventScroll: true }); }
                catch { activeElement.focus(); }
            }
        }
        if (!copied) throw new DOMException("Clipboard writing is unavailable.", "NotAllowedError");
    }

    globalThis.iridiumBrowserCompatibility = { copyText };
})();
