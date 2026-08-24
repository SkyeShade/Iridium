const visibilityObservers = new WeakMap();

export function observeOnce(element, dotNetReference) {
    if (!element) return;
    unobserve(element);
    if (typeof IntersectionObserver !== "function") {
        void dotNetReference.invokeMethodAsync("VideoBecameVisibleAsync");
        return;
    }
    const observer = new IntersectionObserver(entries => {
        if (!entries.some(entry => entry.isIntersecting)) return;
        observer.disconnect();
        visibilityObservers.delete(element);
        void dotNetReference.invokeMethodAsync("VideoBecameVisibleAsync");
    }, { rootMargin: "240px 0px" });
    visibilityObservers.set(element, observer);
    observer.observe(element);
}

export function unobserve(element) {
    const observer = element ? visibilityObservers.get(element) : null;
    observer?.disconnect();
    if (element) visibilityObservers.delete(element);
}
