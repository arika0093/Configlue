// Atomic conditional writes for Configlue browser storage.
//
// Reference this file from the interactive host page:
//   <script src="_content/Configlue.Hosting.Blazor/configlue-webstorage.js"></script>
//
// Conditional writes acquire the browser-wide Web Locks exclusive lock for the
// storage key, then Configlue performs the read/compare/write while the lock is
// held, and releases it. The lock spans separate .NET interop calls, which is what
// makes the compare and the commit atomic across tabs, windows, and Blazor circuits.
(function () {
    if (globalThis.configlueWebStorage) {
        return;
    }

    const releases = new Map();

    function lockName(storageName, key) {
        return "Configlue:webstorage:" + storageName + ":" + key;
    }

    async function acquire(storageName, key, timeoutMilliseconds) {
        if (!globalThis.navigator || !navigator.locks || !navigator.locks.request) {
            return "unsupported";
        }

        const name = lockName(storageName, key);
        const timeout =
            Number.isFinite(timeoutMilliseconds) && timeoutMilliseconds > 0
                ? timeoutMilliseconds
                : 30000;

        return await new Promise(function (resolve) {
            let resolveRelease = null;
            const held = new Promise(function (resolveHeld) {
                resolveRelease = resolveHeld;
            });

            try {
                navigator.locks
                    .request(name, { mode: "exclusive" }, async function () {
                        // Never hold the browser lock forever if the .NET circuit disappears
                        // before it releases.
                        const timer = setTimeout(function () {
                            releases.delete(name);
                            resolveRelease();
                        }, timeout);
                        releases.set(name, function () {
                            clearTimeout(timer);
                            resolveRelease();
                        });
                        resolve("web-locks");
                        await held;
                    })
                    .catch(function () {
                        resolve("unsupported");
                    });
            } catch {
                resolve("unsupported");
            }
        });
    }

    function release(storageName, key) {
        const releaseLock = releases.get(lockName(storageName, key));
        if (releaseLock) {
            releaseLock();
        }
    }

    globalThis.configlueWebStorage = {
        acquire: acquire,
        release: release,
    };
})();
