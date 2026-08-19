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

export function loadToken(nodeAddress) {
    return localStorage.getItem(`iridium.nodeToken:${nodeAddress}`);
}

export function saveToken(nodeAddress, token) {
    localStorage.setItem(`iridium.nodeToken:${nodeAddress}`, token);
}

export function removeToken(nodeAddress) {
    localStorage.removeItem(`iridium.nodeToken:${nodeAddress}`);
}
