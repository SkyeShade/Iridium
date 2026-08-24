export async function requireMatchingMediaBuild(expectedBuildId) {
    const response = await fetch(`../media-build.json?build=${encodeURIComponent(expectedBuildId)}`, {
        cache: "no-store",
        credentials: "same-origin"
    });
    if (!response.ok) throw new Error("MediaVersionMismatchError: The client build manifest is unavailable.");
    const manifest = await response.json();
    if (manifest?.buildId === expectedBuildId) return;
    const updates = globalThis.iridiumClientUpdate ?? await import(
        `./clientUpdate.js?build=${encodeURIComponent(expectedBuildId)}`);
    const recovered = await updates.recoverMediaMismatch(expectedBuildId);
    if (recovered === false)
        throw new Error("MediaVersionMismatchPersistent: This tab is still using older client files.");
    await new Promise(() => {});
}

export async function loadVoicePlayback(expectedBuildId) {
    return import(`./voicePlayback.js?build=${encodeURIComponent(expectedBuildId)}`);
}
