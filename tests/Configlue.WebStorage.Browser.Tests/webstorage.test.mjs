import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { createServer } from "node:http";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { test, before, after } from "node:test";
import { chromium } from "playwright";

const scriptPath = fileURLToPath(new URL("../../src/hosting/Configlue.Hosting.Blazor/wwwroot/configlue-webstorage.js", import.meta.url));
const moduleSource = readFileSync(scriptPath, "utf8");
const moduleUrlPath = "/configlue-webstorage.js";
const raw = "external settings";
const revision = createHash("sha256").update(raw).digest("hex");
const encode = name => JSON.stringify({ revision: name, content: Buffer.from(name).toString("base64") });
let browser;
let server;
let origin;
before(async () => {
    server = createServer((request, response) => {
        if (request.url === moduleUrlPath) {
            response.writeHead(200, { "Content-Type": "text/javascript" });
            response.end(moduleSource);
            return;
        }
        response.writeHead(200, { "Content-Type": "text/html" });
        response.end("<!doctype html><title>WebStorage contract</title>");
    });
    await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
    origin = `http://127.0.0.1:${server.address().port}`;
    browser = await chromium.launch({ headless: true });
});
after(async () => {
    await browser?.close();
    await new Promise(resolve => server.close(resolve));
});
// The helper is an isolated ES module: no host-page script tag, no globals.
async function page(context) {
    const result = await context.newPage();
    await result.goto(origin);
    await result.evaluate(async url => {
        globalThis.configlueModule = await import(url);
    }, `${origin}${moduleUrlPath}`);
    return result;
}
async function pauseHash(holder) {
    await holder.evaluate(() => {
        const digest = crypto.subtle.digest.bind(crypto.subtle);
        let entered;
        let release;
        globalThis.hashEntered = new Promise(resolve => entered = resolve);
        const paused = new Promise(resolve => release = resolve);
        globalThis.releaseHash = release;
        crypto.subtle.digest = async (...args) => {
            entered();
            await paused;
            return digest(...args);
        };
    });
}
async function start(target, value, expected = revision, missing = false) {
    await target.evaluate(({ value, expected, missing }) => {
        globalThis.mutation = globalThis.configlueModule.mutate("localStorage", "settings", value, expected, missing);
        globalThis.settled = false;
        mutation.then(() => globalThis.settled = true);
    }, { value, expected, missing });
}
async function waitForContender(target) {
    await target.waitForFunction(async () => (await navigator.locks.query()).pending.some(lock => lock.name === "Configlue:webstorage:localStorage:settings"));
}

test("native lock survives 31 seconds of controlled time while mutation can still commit", async () => {
    const context = await browser.newContext();
    try {
        const holder = await page(context);
        const contender = await page(context);
        await holder.clock.install();
        await holder.evaluate(value => localStorage.setItem("settings", value), raw);
        await pauseHash(holder);
        await start(holder, encode("a"));
        await holder.evaluate(() => hashEntered);
        await start(contender, encode("b"));
        await waitForContender(contender);
        await holder.clock.runFor(31_000);
        assert.equal(await contender.evaluate(() => settled), false);
        await holder.evaluate(() => releaseHash());
        assert.equal(await holder.evaluate(() => mutation), "committed");
        assert.equal(await contender.evaluate(() => mutation), "conflict");
        assert.equal(await contender.evaluate(() => JSON.parse(localStorage.getItem("settings")).revision), "a");
        assert.equal((await contender.evaluate(() => navigator.locks.query())).held.length, 0);
    } finally { await context.close(); }
});

test("closing an abandoned holder releases its native lock without allowing its commit", async () => {
    const context = await browser.newContext();
    try {
        const holder = await page(context);
        const contender = await page(context);
        await holder.evaluate(value => localStorage.setItem("settings", value), raw);
        await pauseHash(holder);
        await start(holder, encode("abandoned"));
        await holder.evaluate(() => hashEntered);
        await start(contender, encode("remaining"));
        await waitForContender(contender);
        await holder.close();
        assert.equal(await contender.evaluate(() => mutation), "committed");
        assert.equal(await contender.evaluate(() => JSON.parse(localStorage.getItem("settings")).revision), "remaining");
        assert.equal((await contender.evaluate(() => navigator.locks.query())).held.length, 0);
    } finally { await context.close(); }
});

test("losing the caller's interest leaves a self-contained browser mutation atomic", async () => {
    const context = await browser.newContext();
    try {
        const holder = await page(context);
        const contender = await page(context);
        await holder.evaluate(value => localStorage.setItem("settings", value), raw);
        await pauseHash(holder);
        await start(holder, encode("unobserved"));
        await holder.evaluate(() => hashEntered);
        await start(contender, encode("contender"));
        await waitForContender(contender);
        // The interop caller never observes holder.mutation; the browser owns its lifetime.
        await holder.evaluate(() => releaseHash());
        assert.equal(await contender.evaluate(() => mutation), "conflict");
        assert.equal(await contender.evaluate(() => JSON.parse(localStorage.getItem("settings")).revision), "unobserved");
        assert.equal((await contender.evaluate(() => navigator.locks.query())).held.length, 0);
    } finally { await context.close(); }
});

test("MustNotExist has exactly one winner across two native execution contexts", async () => {
    const context = await browser.newContext();
    try {
        const a = await page(context);
        const b = await page(context);
        await Promise.all([start(a, encode("a"), null, true), start(b, encode("b"), null, true)]);
        assert.deepEqual((await Promise.all([a.evaluate(() => mutation), b.evaluate(() => mutation)])).sort(), ["committed", "conflict"]);
    } finally { await context.close(); }
});

test("external envelope revisions agree with .NET decoding", async () => {
    const context = await browser.newContext();
    try {
        const target = await page(context);
        for (const value of [
            '{"content":"invalid base64","revision":"legacy"}',
            '{"content":"YQ==","revision":123}',
            '{"content":"YQ","revision":"legacy"}',
            '{"Content":"YQ==","Revision":"legacy"}',
            '{"content":" YQ==\\n","revision":"legacy"}'
        ]) {
            const validEnvelope = value.includes('"Content"') || value.includes('\\n');
            const expected = validEnvelope ? "legacy" : createHash("sha256").update(value).digest("hex");
            await target.evaluate(value => localStorage.setItem("settings", value), value);
            await start(target, encode("updated"), expected);
            assert.equal(await target.evaluate(() => mutation), "committed", value);
        }
        await target.evaluate(() => localStorage.setItem("settings", ""));
        await start(target, encode("new"), null, true);
        assert.equal(await target.evaluate(() => mutation), "committed");
    } finally { await context.close(); }
});

test("storage errors are unavailable and release the lock", async () => {
    const context = await browser.newContext();
    try {
        const target = await page(context);
        await target.evaluate(() => {
            Storage.prototype.setItem = () => { throw new DOMException("Quota", "QuotaExceededError"); };
        });
        await start(target, encode("new"), null, true);
        assert.equal(await target.evaluate(() => mutation), "unavailable");
        assert.equal((await target.evaluate(() => navigator.locks.query())).held.length, 0);
        await target.evaluate(() => Object.defineProperty(globalThis, "localStorage", { get() { throw new DOMException("Denied", "SecurityError"); } }));
        await start(target, encode("new"), null, true);
        assert.equal(await target.evaluate(() => mutation), "unavailable");
    } finally { await context.close(); }
});

test("module exposes no globals", async () => {
    const context = await browser.newContext();
    try {
        const target = await page(context);
        assert.equal(await target.evaluate(() => globalThis.configlueWebStorage), undefined);
        assert.equal(await target.evaluate(() => typeof globalThis.configlueModule.mutate), "function");
        assert.equal(await target.evaluate(() => typeof globalThis.configlueModule.subscribeStorageChanges), "function");
        assert.equal(await target.evaluate(() => typeof globalThis.configlueModule.unsubscribeStorageChanges), "function");
    } finally { await context.close(); }
});

test("storage events fan out to subscribers with area and key", async () => {
    const context = await browser.newContext();
    try {
        const writer = await page(context);
        const watcher = await page(context);
        await watcher.evaluate(() => {
            globalThis.received = [];
            globalThis.stub = {
                invokeMethodAsync(_method, area, key) {
                    globalThis.received.push([area, key]);
                    return Promise.resolve();
                }
            };
            globalThis.configlueModule.subscribeStorageChanges("watcher", globalThis.stub);
        });
        await writer.evaluate(() => {
            globalThis.received = [];
            globalThis.stub = {
                invokeMethodAsync(_method, area, key) {
                    globalThis.received.push([area, key]);
                    return Promise.resolve();
                }
            };
            globalThis.configlueModule.subscribeStorageChanges("watcher", globalThis.stub);
        });
        await writer.evaluate(() => localStorage.setItem("settings", "cross-context"));
        await watcher.waitForFunction(() => globalThis.received.length > 0);
        assert.deepEqual(await watcher.evaluate(() => globalThis.received), [["localStorage", "settings"]]);

        // The browser event does not fire in the writing context itself, which is why
        // managed same-context writes signal local watchers explicitly.
        await new Promise(resolve => setTimeout(resolve, 250));
        assert.deepEqual(await writer.evaluate(() => globalThis.received), []);
    } finally { await context.close(); }
});

test("repeated subscribes with one id stay idempotent", async () => {
    const context = await browser.newContext();
    try {
        const writer = await page(context);
        const watcher = await page(context);
        await watcher.evaluate(() => {
            globalThis.received = [];
            globalThis.stub = {
                invokeMethodAsync(_method, area, key) {
                    globalThis.received.push([area, key]);
                    return Promise.resolve();
                }
            };
            globalThis.configlueModule.subscribeStorageChanges("watcher", globalThis.stub);
            globalThis.configlueModule.subscribeStorageChanges("watcher", globalThis.stub);
        });
        await writer.evaluate(() => localStorage.setItem("settings", "once"));
        await watcher.waitForFunction(() => globalThis.received.length > 0);
        await new Promise(resolve => setTimeout(resolve, 250));
        assert.deepEqual(await watcher.evaluate(() => globalThis.received), [["localStorage", "settings"]]);
    } finally { await context.close(); }
});

test("unsubscribed listeners stop receiving storage events", async () => {    const context = await browser.newContext();
    try {
        const writer = await page(context);
        const watcher = await page(context);
        await watcher.evaluate(() => {
            globalThis.received = [];
            globalThis.stub = {
                invokeMethodAsync(_method, area, key) {
                    globalThis.received.push([area, key]);
                    return Promise.resolve();
                }
            };
            globalThis.configlueModule.subscribeStorageChanges("watcher", globalThis.stub);
        });
        await writer.evaluate(() => localStorage.setItem("settings", "one"));
        await watcher.waitForFunction(() => globalThis.received.length > 0);
        await watcher.evaluate(() => globalThis.configlueModule.unsubscribeStorageChanges("watcher"));
        await writer.evaluate(() => localStorage.setItem("settings", "two"));
        await new Promise(resolve => setTimeout(resolve, 250));
        assert.deepEqual(await watcher.evaluate(() => globalThis.received), [["localStorage", "settings"]]);
    } finally { await context.close(); }
});
