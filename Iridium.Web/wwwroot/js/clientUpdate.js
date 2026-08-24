export async function recoverMediaMismatch(buildId) {
        const key = `iridium.media-recovery.${buildId}`;
        if (sessionStorage.getItem(key) === "attempted") return false;
        sessionStorage.setItem(key, "attempted");
        try {
            const registration = await navigator.serviceWorker?.getRegistration();
            await registration?.update();
        } catch { }
        const target = new URL(location.href);
        target.searchParams.set("iridium-update", buildId);
        location.replace(target.href);
        await new Promise(() => {});
}

globalThis.iridiumClientUpdate = { recoverMediaMismatch };
