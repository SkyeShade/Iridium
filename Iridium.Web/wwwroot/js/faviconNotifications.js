const variants = new Map();
let originalHref;
let latestRevision = 0;

function faviconLink() {
    let link = document.querySelector('link[rel~="icon"]');
    if (!link) {
        link = document.createElement('link');
        link.rel = 'icon';
        document.head.appendChild(link);
    }
    return link;
}

function loadImage(source) {
    return new Promise((resolve, reject) => {
        const image = new Image();
        image.onload = () => resolve(image);
        image.onerror = reject;
        image.src = source;
    });
}

export async function setMentionCount(count, revision) {
    if (revision < latestRevision) return;
    latestRevision = revision;
    const link = faviconLink();
    originalHref ??= link.getAttribute('href') || 'favicon.png';
    if (!Number.isFinite(count) || count <= 0) {
        link.href = originalHref;
        return;
    }

    const label = count > 9 ? '9+' : String(Math.max(1, Math.trunc(count)));
    if (!variants.has(label)) {
        try {
            const source = await loadImage(originalHref);
            const canvas = document.createElement('canvas');
            canvas.width = canvas.height = 64;
            const context = canvas.getContext('2d');
            context.drawImage(source, 0, 0, 64, 64);
            context.fillStyle = '#e5484d';
            context.beginPath();
            context.arc(49, 49, 15, 0, Math.PI * 2);
            context.fill();
            context.lineWidth = 4;
            context.strokeStyle = '#17191f';
            context.stroke();
            context.fillStyle = '#fff';
            context.font = `700 ${label.length > 1 ? 15 : 20}px system-ui, sans-serif`;
            context.textAlign = 'center';
            context.textBaseline = 'middle';
            context.fillText(label, 49, 50);
            variants.set(label, canvas.toDataURL('image/png'));
        } catch {
            return; // Dynamic favicons are optional; leave the original intact.
        }
    }
    if (revision === latestRevision) link.href = variants.get(label);
}
