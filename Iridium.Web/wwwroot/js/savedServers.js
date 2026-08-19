export function load(key) {
    const value = localStorage.getItem(key);
    if (!value) return [];

    try {
        return JSON.parse(value);
    } catch {
        return [];
    }
}

export function save(key, servers) {
    localStorage.setItem(key, JSON.stringify(servers));
}

export function loadValue(key) {
    const value = localStorage.getItem(key);
    if (!value) return null;
    try {
        return JSON.parse(value);
    } catch {
        return null;
    }
}

export function loadSessionValue(key) {
    const value = sessionStorage.getItem(key);
    if (!value) return null;
    try {
        return JSON.parse(value);
    } catch {
        return null;
    }
}

export function saveSessionValue(key, value) {
    if (value === null || value === undefined) sessionStorage.removeItem(key);
    else sessionStorage.setItem(key, JSON.stringify(value));
}

export function loadGuidValue(key) {
    const raw = localStorage.getItem(key);
    if (raw === null) return null;
    if (raw.trim() === "" || raw === "undefined" || raw === "null") {
        localStorage.removeItem(key);
        return null;
    }

    let value;
    try {
        value = JSON.parse(raw);
    } catch {
        value = raw;
    }

    if (typeof value === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value))
        return value;

    localStorage.removeItem(key);
    return null;
}

export function saveGuidValue(key, value) {
    if (typeof value !== "string" || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value)) {
        localStorage.removeItem(key);
        return;
    }
    localStorage.setItem(key, JSON.stringify(value));
}

export function loadToken(nodeAddress) {
    return localStorage.getItem(`iridium.nodeToken:${nodeAddress}`);
}

export function saveToken(nodeAddress, token) {
    localStorage.setItem(`iridium.nodeToken:${nodeAddress}`, token);
}

export function removeToken(nodeAddress) {
    localStorage.removeItem(`iridium.nodeToken:${nodeAddress}`);
}
