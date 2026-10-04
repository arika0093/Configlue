// Configlue browser storage helpers as an isolated ES module.
//
// Load from .NET through JS isolation instead of a host-page script tag:
//
//   var module = await jsRuntime.InvokeAsync<IJSObjectReference>(
//       "import", "./_content/Configlue.Hosting.Blazor/configlue-webstorage.js");
//
// No global symbols are required. The module keeps one browser `storage`
// listener per JavaScript realm and fans notifications out to every managed
// subscriber registered through subscribeStorageChanges().
//
// A conditional write is a single browser-side operation. The read, compare,
// and commit all execute inside one navigator.locks.request callback, so the
// Web Lock is held for exactly as long as the mutation can still commit and
// is released when the callback returns. There is deliberately no timeout
// that can release the lock while the caller is still able to write.

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

export async function mutate(storageName, key, value, expectedRevision, mustNotExist) {
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

function storageNameOf(area) {
    try {
        if (area === globalThis.localStorage) {
            return "localStorage";
        }
        if (area === globalThis.sessionStorage) {
            return "sessionStorage";
        }
    } catch {
        // Storage access can throw (for example a denied SecurityError);
        // fall through and report an unknown area.
    }
    return null;
}

let storageListener = null;
// Keyed by the managed subscription id so repeated subscribes from one owner stay
// idempotent and teardown always releases exactly what the owner registered.
const storageSubscribers = new Map();

function onBrowserStorageEvent(event) {
    // A null key reports clear(); managed subscribers wake conservatively.
    const areaName = storageNameOf(event.storageArea);
    const key = event.key ?? null;
    for (const subscriber of Array.from(storageSubscribers.values())) {
        try {
            subscriber.invokeMethodAsync("OnStorageChanged", areaName, key);
        } catch {
            // One broken subscriber must not prevent fan-out to the rest.
        }
    }
}

// Registers a managed change receiver for `storage` events. The browser
// `storage` event fires in browsing contexts other than the writer, so
// same-context writes are additionally signaled on the managed side.
// Resolves with the current subscriber count, primarily for diagnostics.
export function subscribeStorageChanges(subscriptionId, subscriber) {
    if (subscriptionId && subscriber) {
        storageSubscribers.set(subscriptionId, subscriber);
    }
    if (!storageListener) {
        storageListener = onBrowserStorageEvent;
        globalThis.addEventListener("storage", storageListener);
    }
    return storageSubscribers.size;
}

export function unsubscribeStorageChanges(subscriptionId) {
    if (subscriptionId === null || subscriptionId === undefined) {
        storageSubscribers.clear();
    } else {
        storageSubscribers.delete(subscriptionId);
    }
    if (storageSubscribers.size === 0 && storageListener) {
        globalThis.removeEventListener("storage", storageListener);
        storageListener = null;
    }
    return storageSubscribers.size;
}
