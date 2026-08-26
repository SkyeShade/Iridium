export function supportsFullscreen(elementId) {
    const element = document.getElementById(elementId);
    return !!(element?.requestFullscreen && document.fullscreenEnabled !== false);
}
