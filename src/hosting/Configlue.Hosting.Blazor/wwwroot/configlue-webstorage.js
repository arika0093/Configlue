// Atomic conditional writes for Configlue browser storage.
//
// Reference this file from the interactive host page:
//   <script src="_content/Configlue.Hosting.Blazor/configlue-webstorage.js"></script>
//
// A conditional write is a single browser-side operation. The read, compare, and
// commit all execute inside one navigator.locks.request callback, so the Web Lock
// is held for exactly as long as the mutation can still commit and is released
// when the callback returns. There is deliberately no timeout that can release the
// lock while the caller is still able to write.
(function () {
    if (globalThis.configlueWebStorage) {
        return;
    }

    function lockName(storageName, key) {
        return "Configlue:webstorage:" + storageName + ":" + key;
    }

    function storageArea(storageName) {
        if (storageName === "sessionStorage") {
            return globalThis.sessionStorage;
        }
        return globalThis.localStorage;
    }

    function supportsWebLocks() {
        return !!(
            globalThis.navigator &&
            navigator.locks &&
            typeof navigator.locks.request === "function"
        );
    }

    function toHex(buffer) {
        const bytes = new Uint8Array(buffer);
        let value = "";
        for (let index = 0; index < bytes.length; index++) {
            value += bytes[index].toString(16).padStart(2, "0");
        }
        return value;
    }

    async function rawRevision(raw) {
        const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(raw));
        return toHex(digest);
    }

    // Mirrors WebStorageResource.Decode: a value written outside Configlue has no
    // envelope, so its revision is the lower-case SHA-256 of the raw string.
    async function currentRevision(raw) {
        try {
            const envelope = JSON.parse(raw);
            if (envelope && typeof envelope === "object" && !Array.isArray(envelope)) {
                let content;
                let revision;
                // JsonSerializerDefaults.Web reads property names without regard to case.
                for (const [name, value] of Object.entries(envelope)) {
                    if (name.toLowerCase() === "content") content = value;
                    if (name.toLowerCase() === "revision") revision = value;
                }
                if (typeof content === "string" &&
                    (revision === undefined || revision === null || typeof revision === "string")) {
                    const base64 = content.replace(/[ \t\r\n]/g, "");
                    if (/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(base64)) {
                        return revision ?? null;
                    }
                }
            }
        } catch {
            // Not a Configlue envelope.
        }
        return await rawRevision(raw);
    }

    async function mutate(storageName, key, value, expectedRevision, mustNotExist) {
        if (!supportsWebLocks()) {
            return "unsupported";
        }

        let status = "unsupported";
        try {
            const storage = storageArea(storageName);
            if (!storage) return "unavailable";
            await navigator.locks.request(
                lockName(storageName, key),
                { mode: "exclusive" },
                async function () {
                    const existing = storage.getItem(key);
                    const exists = existing !== null && existing.length > 0;
                    if (mustNotExist) {
                        if (exists) {
                            status = "conflict";
                            return;
                        }
                    } else {
                        const revision =
                            exists && existing.length > 0
                                ? await currentRevision(existing)
                                : null;
                        if (revision !== expectedRevision) {
                            status = "conflict";
                            return;
                        }
                    }

                    storage.setItem(key, value);
                    status = "committed";
                }
            );
        } catch {
            return "unavailable";
        }

        return status;
    }

    globalThis.configlueWebStorage = {
        mutate: mutate,
    };
})();
