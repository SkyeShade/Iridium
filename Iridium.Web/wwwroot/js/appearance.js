(() => {
    const storageKey = "iridium.appearance";
    const root = document.documentElement;
    const derivedProperties = [
        "--accent-soft", "--accent-deep", "--bg-deep", "--bg-sidebar", "--input-bg",
        "--surface-raised", "--surface-hover", "--surface-active", "--border", "--border-subtle",
        "--scrollbar-thumb", "--scrollbar-thumb-hover"
    ];
    const defaultPreferences = readDefaultPreferences();
    const defaultDerivedProperties = readProperties(derivedProperties);

    function isHexColor(value) {
        return typeof value === "string" && /^#[0-9a-f]{6}$/i.test(value);
    }

    function normalize(value) {
        return value.toLowerCase();
    }

    function parseHex(value) {
        return [
            Number.parseInt(value.slice(1, 3), 16),
            Number.parseInt(value.slice(3, 5), 16),
            Number.parseInt(value.slice(5, 7), 16)
        ];
    }

    function mix(first, second, secondWeight) {
        const left = parseHex(first);
        const right = parseHex(second);
        const channel = index => Math.round(left[index] * (1 - secondWeight) + right[index] * secondWeight)
            .toString(16).padStart(2, "0");
        return `#${channel(0)}${channel(1)}${channel(2)}`;
    }

    function validPreferences(value) {
        return value && isHexColor(value.accentColor) &&
            isHexColor(value.baseBackgroundColor) && isHexColor(value.surfaceColor);
    }

    function sanitized(value) {
        return {
            accentColor: normalize(value.accentColor),
            baseBackgroundColor: normalize(value.baseBackgroundColor),
            surfaceColor: normalize(value.surfaceColor),
            showMessageAvatarPresence: value.showMessageAvatarPresence === true
        };
    }

    function apply(value) {
        const preferences = sanitized(value);
        root.style.setProperty("--iridium-accent", preferences.accentColor);
        root.style.setProperty("--iridium-bg-base", preferences.baseBackgroundColor);
        root.style.setProperty("--iridium-bg-surface", preferences.surfaceColor);

        if (usesDefaultPalette(preferences)) {
            for (const property of derivedProperties)
                root.style.setProperty(property, defaultDerivedProperties[property]);
            return preferences;
        }

        root.style.setProperty("--accent-soft", mix(preferences.accentColor, "#ffffff", 0.34));
        root.style.setProperty("--accent-deep", mix(preferences.accentColor, "#000000", 0.27));
        root.style.setProperty("--bg-deep", mix(preferences.baseBackgroundColor, "#000000", 0.46));
        root.style.setProperty("--bg-sidebar", mix(preferences.baseBackgroundColor, preferences.surfaceColor, 0.34));
        root.style.setProperty("--input-bg", mix(preferences.baseBackgroundColor, "#000000", 0.42));
        root.style.setProperty("--surface-raised", mix(preferences.surfaceColor, "#ffffff", 0.055));
        root.style.setProperty("--surface-hover", mix(preferences.surfaceColor, "#ffffff", 0.10));
        root.style.setProperty("--surface-active", mix(preferences.surfaceColor, "#ffffff", 0.16));
        root.style.setProperty("--border", mix(preferences.surfaceColor, "#ffffff", 0.15));
        root.style.setProperty("--border-subtle", mix(preferences.surfaceColor, preferences.baseBackgroundColor, 0.55));
        root.style.setProperty("--scrollbar-thumb", mix(preferences.surfaceColor, "#ffffff", 0.12));
        root.style.setProperty("--scrollbar-thumb-hover", mix(preferences.surfaceColor, "#ffffff", 0.22));
        return preferences;
    }

    function readDefaultPreferences() {
        const styles = getComputedStyle(root);
        return {
            accentColor: styles.getPropertyValue("--iridium-accent").trim().toLowerCase(),
            baseBackgroundColor: styles.getPropertyValue("--iridium-bg-base").trim().toLowerCase(),
            surfaceColor: styles.getPropertyValue("--iridium-bg-surface").trim().toLowerCase(),
            showMessageAvatarPresence: false
        };
    }

    function readProperties(properties) {
        const styles = getComputedStyle(root);
        return Object.fromEntries(properties.map(property => [property, styles.getPropertyValue(property).trim()]));
    }

    function defaults() {
        return { ...defaultPreferences };
    }

    function usesDefaultPalette(preferences) {
        return preferences.accentColor === defaultPreferences.accentColor &&
            preferences.baseBackgroundColor === defaultPreferences.baseBackgroundColor &&
            preferences.surfaceColor === defaultPreferences.surfaceColor;
    }

    function stored() {
        try {
            const value = JSON.parse(localStorage.getItem(storageKey));
            return validPreferences(value) ? sanitized(value) : null;
        } catch {
            return null;
        }
    }

    let current = stored();
    if (current) apply(current);

    window.iridiumAppearance = {
        load() {
            return current || defaults();
        },
        save(value) {
            const preferences = validPreferences(value) ? apply(value) : defaults();
            localStorage.setItem(storageKey, JSON.stringify(preferences));
            current = preferences;
            return preferences;
        },
        reset() {
            const preferences = apply(defaults());
            localStorage.setItem(storageKey, JSON.stringify(preferences));
            current = preferences;
            return preferences;
        }
    };
})();
