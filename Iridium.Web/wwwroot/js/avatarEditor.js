export function capturePointer(element, pointerId) {
    element.setPointerCapture(pointerId);
    return element.getBoundingClientRect().width;
}

export function releasePointer(element, pointerId) {
    if (element.hasPointerCapture(pointerId)) element.releasePointerCapture(pointerId);
}

function loadImage(sourceUrl) {
    return new Promise((resolve, reject) => {
        const image = new Image();
        image.onload = () => resolve(image);
        image.onerror = () => reject(new Error("The selected image could not be previewed."));
        image.src = sourceUrl;
    });
}

export async function imageDimensions(sourceUrl) {
    const image = await loadImage(sourceUrl);
    return { width: image.naturalWidth, height: image.naturalHeight };
}

export async function processStaticAvatar(sourceUrl, originalFileName) {
    const image = await loadImage(sourceUrl);
    const canvas = document.createElement("canvas");
    canvas.width = image.naturalWidth;
    canvas.height = image.naturalHeight;
    const context = canvas.getContext("2d");
    if (!context) throw new Error("Avatar image processing is unavailable in this browser.");
    context.drawImage(image, 0, 0);
    const blob = await new Promise((resolve, reject) => canvas.toBlob(
        value => value ? resolve(value) : reject(new Error("The avatar could not be encoded.")),
        "image/webp", 0.92));
    if (blob.type !== "image/webp")
        throw new Error(`The browser returned an unexpected avatar format: ${blob.type || "unknown"}.`);
    const dataUrl = await new Promise((resolve, reject) => {
        const reader = new FileReader();
        reader.onload = () => resolve(reader.result);
        reader.onerror = () => reject(new Error("The processed avatar could not be read."));
        reader.readAsDataURL(blob);
    });
    const baseName = (originalFileName || "avatar").replace(/\.[^.]+$/, "") || "avatar";
    return {
        dataUrl,
        contentType: blob.type,
        fileName: `${baseName}.webp`,
        size: blob.size,
        width: image.naturalWidth,
        height: image.naturalHeight
    };
}
