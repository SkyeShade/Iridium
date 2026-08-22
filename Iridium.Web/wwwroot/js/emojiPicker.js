export function scrollToCommunity(sectionId) {
    document.getElementById(sectionId)?.scrollIntoView({ behavior: "smooth", block: "start" });
}

export function detailPosition(clientX, clientY) {
    const width = 176;
    const height = 208;
    const margin = 10;
    const x = clientX + width + margin <= window.innerWidth ? clientX + margin : clientX - width - margin;
    return {
        x: Math.max(margin, Math.min(window.innerWidth - width - margin, x)),
        y: Math.max(margin, Math.min(window.innerHeight - height - margin, clientY - height / 2))
    };
}
