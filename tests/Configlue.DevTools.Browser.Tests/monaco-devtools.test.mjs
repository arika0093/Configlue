import assert from "node:assert/strict";
import { createServer } from "node:http";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { test, before, after } from "node:test";
import { chromium } from "playwright";

const root = fileURLToPath(new URL(".", import.meta.url));
const monacoRoot = `${root}node_modules/monaco-editor/min/vs`;
const bridgeSource = readFileSync(
    `${root}../../src/devtools/Configlue.DevTools.Web/wwwroot/configlue-devtools-monaco.js`,
    "utf8"
);

// Projection-shaped fixture (two-space indent, CLR names). Ranges below are
// derived from this text in Node so hover positions stay exact.
const password = "smoke-secret-9zX4q";
const redacted = "********"; // ConfiglueSecrets.RedactedText parity.
const documentUri = "configlue://states/devtools-viewer/-";
const schemaUri = "configlue://schemas/devtools-viewer/1";
const canonicalJson = `{
  "Theme": "Dark",
  "RetryCount": 5,
  "Database": {
    "Host": "db.local",
    "Password": "${redacted}"
  }
}`;
const schemaJson = JSON.stringify({
    $schema: "https://json-schema.org/draft/2020-12/schema",
    title: "devtools-viewer",
    type: "object",
    properties: {
        Theme: { type: "string" },
        RetryCount: { type: "integer" },
        Database: {
            type: "object",
            properties: {
                Host: { type: "string" },
                Password: { type: "string" },
            },
        },
    },
    required: [],
});
const hovers = [
    {
        memberPath: "Theme",
        markdown:
            "### Theme\n\nEffective source: memory\n\nEditable: Yes\n\nContributions\n\n- memory: Present, Effective\n\nLocator\n\n—",
    },
    {
        memberPath: "Database.Host",
        markdown:
            "### Host\n\nEffective source: memory\n\nEditable: Yes\n\nContributions\n\n- memory: Present, Effective\n\nLocator\n\n—",
    },
    {
        memberPath: "Database.Password",
        markdown: "### Password\n\nSecret: Yes\n\nPresent: Yes\n\nEffective source: memory",
    },
];
const inlays = [
    { memberPath: "Theme", label: "memory" },
    { memberPath: "Database.Host", label: "memory" },
];
const runtimeMarkers = [
    {
        memberPath: "RetryCount",
        severity: "Error",
        message: "Source 'memory' reported an invalid payload for 'RetryCount'.",
    },
];

function offsetToPosition(text, offset) {
    const before = text.slice(0, offset);
    const lineNumber = before.split("\n").length;
    const lineStart = before.lastIndexOf("\n") + 1;
    return { lineNumber, column: offset - lineStart + 1 };
}

function valueRangeOf(text, search) {
    const start = text.indexOf(search);
    assert.notEqual(start, -1, `fixture must contain ${search}`);
    const startPos = offsetToPosition(text, start);
    const endPos = offsetToPosition(text, start + search.length);
    return { startLineNumber: startPos.lineNumber, startColumn: startPos.column, endLineNumber: endPos.lineNumber, endColumn: endPos.column };
}

const ranges = [
    { memberPath: "Theme", ...valueRangeOf(canonicalJson, '"Dark"') },
    { memberPath: "Database.Host", ...valueRangeOf(canonicalJson, '"db.local"') },
    { memberPath: "Database.Password", ...valueRangeOf(canonicalJson, `"${redacted}"`) },
    { memberPath: "RetryCount", ...valueRangeOf(canonicalJson, "5,") },
];
const inlayPayload = inlays.map(entry => {
    const range = ranges.find(candidate => candidate.memberPath === entry.memberPath);
    return { ...entry, ...range };
});
const markerPayload = runtimeMarkers.map(entry => {
    const range = ranges.find(candidate => candidate.memberPath === entry.memberPath);
    return { ...entry, ...range };
});

const pageHtml = `<!doctype html>
<html><head><meta charset="utf-8"><title>DevTools Monaco smoke</title>
<link rel="stylesheet" href="/vs/editor/editor.main.css">
</head><body>
<div id="editor" style="width:900px;height:600px;border:1px solid #333"></div>
<script src="/vs/loader.js"></script>
<script>
require.config({ paths: { vs: '/vs' } });
window.__monacoReady = new Promise((resolve, reject) => {
    require(['vs/editor/editor.main'], () => resolve(true), reject);
});
</script>
<script src="/bridge.js"></script>
</body></html>`;

const contentTypes = { ".js": "text/javascript", ".css": "text/css", ".json": "application/json", ".ttf": "font/ttf", ".woff": "font/woff" };

let browser;
let server;
let origin;
before(async () => {
    server = createServer((request, response) => {
        const url = new URL(request.url, "http://127.0.0.1");
        if (url.pathname === "/") {
            response.writeHead(200, { "Content-Type": "text/html" });
            response.end(pageHtml);
            return;
        }
        if (url.pathname === "/bridge.js") {
            response.writeHead(200, { "Content-Type": "text/javascript" });
            response.end(bridgeSource);
            return;
        }
        if (url.pathname.startsWith("/vs/")) {
            const suffix = url.pathname.slice("/vs/".length);
            if (suffix.includes("..")) {
                response.writeHead(400);
                response.end();
                return;
            }
            try {
                const body = readFileSync(`${monacoRoot}/${suffix}`);
                const dot = suffix.lastIndexOf(".");
                const type = contentTypes[suffix.slice(dot)] ?? "application/octet-stream";
                response.writeHead(200, { "Content-Type": type });
                response.end(body);
            } catch {
                response.writeHead(404);
                response.end();
            }
            return;
        }
        response.writeHead(404);
        response.end();
    });
    await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
    origin = `http://127.0.0.1:${server.address().port}`;
    browser = await chromium.launch({ headless: true });
});
after(async () => {
    await browser?.close();
    await new Promise(resolve => server.close(resolve));
});

async function newEditorPage(context) {
    const page = await context.newPage();
    await page.goto(origin);
    await page.evaluate(() => window.__monacoReady);
    await page.evaluate(() => {
        globalThis.editor = monaco.editor.create(document.getElementById("editor"), {
            language: "json",
            theme: "vs-dark",
            automaticLayout: true,
            minimap: { enabled: false },
        });
    });
    return page;
}

function markers(page, owner) {
    return page.evaluate(({ uri, owner }) => {
        const resource = monaco.Uri.parse(uri);
        return monaco.editor.getModelMarkers({ resource }).filter(marker => !owner || marker.owner === owner);
    }, { uri: documentUri, owner });
}

// Bounded polling helper (replaces waitForFunction for async predicates).
async function waitForPage(page, predicate, timeoutMs = 15000) {
    const deadline = Date.now() + timeoutMs;
    for (;;) {
        if (await page.evaluate(predicate)) {
            return;
        }
        if (Date.now() >= deadline) {
            throw new Error("Timed out waiting for page condition.");
        }
        await new Promise(resolve => setTimeout(resolve, 250));
    }
}

test("page starts and Monaco is visible", async () => {
    const context = await browser.newContext();
    try {
        const page = await newEditorPage(context);
        await page.waitForSelector(".monaco-editor", { timeout: 15000 });
        assert.equal(await page.evaluate(() => typeof monaco.editor.create), "function");
        assert.equal(await page.evaluate(() => typeof configlueDevToolsMonaco.setJsonSchema), "function");
    } finally { await context.close(); }
});

test("canonical JSON loads on the explicit document URI", async () => {
    const context = await browser.newContext();
    try {
        const page = await newEditorPage(context);
        await page.evaluate(({ uri, json }) => {
            const model = monaco.editor.createModel(json, "json", monaco.Uri.parse(uri));
            globalThis.editor.setModel(model);
        }, { uri: documentUri, json: canonicalJson });
        const value = await page.evaluate(() => globalThis.editor.getValue());
        assert.match(value, /"Theme": "Dark"/);
        assert.ok(value.includes(`"Password": "${redacted}"`));
        assert.equal(value.includes(password), false);
    } finally { await context.close(); }
});

test("generated schema binds to the document URI and validates", async () => {
    const context = await browser.newContext();
    try {
        const page = await newEditorPage(context);
        await page.evaluate(({ uri, json }) => {
            globalThis.editor.setModel(monaco.editor.createModel(json, "json", monaco.Uri.parse(uri)));
        }, { uri: documentUri, json: canonicalJson });
        await page.evaluate(({ schemaUri, schemaJson, uri }) => {
            configlueDevToolsMonaco.setJsonSchema(schemaUri, schemaJson, uri);
        }, { schemaUri, schemaJson, uri: documentUri });

        const state = await page.evaluate(() => configlueDevToolsMonaco.__configlueTestState());
        assert.equal(state.providersRegistered, true);

        const fileMatch = await page.evaluate(() => {
            const options = monaco.languages.json.jsonDefaults.diagnosticsOptions;
            return (options.schemas ?? []).map(entry => ({ uri: entry.uri, fileMatch: entry.fileMatch }));
        });
        assert.deepEqual(fileMatch, [{ uri: schemaUri, fileMatch: [documentUri] }]);

        // A real type violation must surface as a browser-side JSON marker.
        await page.evaluate(broken => globalThis.editor.setValue(broken), canonicalJson.replace("5,", '"oops",'));
        await waitForPage(page, () => {
            const found = monaco.editor.getModelMarkers();
            return found.some(marker => marker.owner === "json");
        });
        const jsonMarkers = await markers(page, "json");
        assert.ok(jsonMarkers.length > 0);

        // Restore the canonical document for later tests.
        await page.evaluate(json => globalThis.editor.setValue(json), canonicalJson);
    } finally { await context.close(); }
});

test("provenance hover resolves and renders", async () => {
    const context = await browser.newContext();
    try {
        const page = await newEditorPage(context);
        await page.evaluate(({ uri, json, hovers, ranges }) => {
            globalThis.editor.setModel(monaco.editor.createModel(json, "json", monaco.Uri.parse(uri)));
            configlueDevToolsMonaco.setHoverData(uri, hovers, ranges);
        }, { uri: documentUri, json: canonicalJson, hovers, ranges });

        // Direct provider-lookup assertion through the shared resolver.
        const resolved = await page.evaluate(({ uri }) =>
            configlueDevToolsMonaco.__resolveHover(uri, 2, 14), { uri: documentUri });
        assert.ok(resolved);
        assert.equal(resolved.memberPath, "Theme");
        assert.match(resolved.markdown, /Effective source: memory/);

        // Real hover UI at the Theme value: move the mouse over the value,
        // then show the hover through the real hover action at the cursor.
        // (Headless mouse hover alone is not a reliable trigger.)
        const point = await page.evaluate(() => {
            const position = { lineNumber: 2, column: 14 };
            globalThis.editor.setPosition(position);
            globalThis.editor.revealPositionInCenter(position);
            const visible = globalThis.editor.getScrolledVisiblePosition(position);
            if (!visible) {
                return null;
            }
            const rect = globalThis.editor.getContainerDomNode().getBoundingClientRect();
            return { x: rect.left + visible.left, y: rect.top + visible.top + visible.height / 2 };
        });
        assert.ok(point);
        await page.mouse.move(point.x, point.y, { steps: 5 });
        await page.evaluate(() => {
            globalThis.editor.focus();
            globalThis.editor.trigger("smoke", "editor.action.showHover", {});
        });
        await page.waitForSelector(".monaco-hover:not(.hidden)", { timeout: 15000 });
        const hoverText = await page.evaluate(() => document.querySelector(".monaco-editor").innerText);
        assert.match(hoverText, /memory/);
    } finally { await context.close(); }
});

test("effective-source inlay labels render without touching text", async () => {
    const context = await browser.newContext();
    try {
        const page = await newEditorPage(context);
        await page.evaluate(({ uri, json, inlays }) => {
            globalThis.editor.setModel(monaco.editor.createModel(json, "json", monaco.Uri.parse(uri)));
            configlueDevToolsMonaco.setInlayLabels(uri, inlays);
        }, { uri: documentUri, json: canonicalJson, inlays: inlayPayload });
        // Move the mouse away so no hover widget can satisfy this assertion.
        await page.mouse.move(0, 0);
        await page.waitForFunction(() => {
            const host = document.querySelector(".monaco-hover");
            return !host || host.style.display === "none" || host.innerText.length === 0;
        }, { timeout: 8000 }).catch(() => {});
        await page.waitForFunction(() => document.querySelector("#editor").innerText.includes("memory"), { timeout: 15000 });
        // Inlays are virtual UI: the model text itself never carries them.
        const value = await page.evaluate(() => globalThis.editor.getValue());
        assert.equal(value.includes("memory"), false);
    } finally { await context.close(); }
});

test("secrets stay redacted and absent everywhere", async () => {
    const context = await browser.newContext();
    try {
        const page = await newEditorPage(context);
        await page.evaluate(({ uri, json, hovers, ranges, inlays, markers }) => {
            globalThis.editor.setModel(monaco.editor.createModel(json, "json", monaco.Uri.parse(uri)));
            configlueDevToolsMonaco.setHoverData(uri, hovers, ranges);
            configlueDevToolsMonaco.setInlayLabels(uri, inlays);
            configlueDevToolsMonaco.setRuntimeMarkers(uri, markers);
        }, { uri: documentUri, json: canonicalJson, hovers, ranges, inlays: inlayPayload, markers: markerPayload });

        const value = await page.evaluate(() => globalThis.editor.getValue());
        assert.equal(value.includes(password), false);
        assert.ok(value.includes(redacted));
        const dom = await page.evaluate(() => document.body.innerHTML);
        assert.equal(dom.includes(password), false);
        const secretHover = await page.evaluate(({ uri }) =>
            configlueDevToolsMonaco.__resolveHover(uri, 6, 20), { uri: documentUri });
        assert.ok(secretHover);
        assert.match(secretHover.markdown, /Secret: Yes/);
        assert.equal(secretHover.markdown.includes(password), false);
        const all = await markers(page);
        for (const marker of all) {
            assert.equal(marker.message.includes(password), false);
        }
    } finally { await context.close(); }
});

test("runtime markers surface on the document model and clear", async () => {
    const context = await browser.newContext();
    try {
        const page = await newEditorPage(context);
        await page.evaluate(({ uri, json }) => {
            globalThis.editor.setModel(monaco.editor.createModel(json, "json", monaco.Uri.parse(uri)));
        }, { uri: documentUri, json: canonicalJson });
        await page.evaluate(({ uri, markers }) => {
            configlueDevToolsMonaco.setRuntimeMarkers(uri, markers);
        }, { uri: documentUri, markers: markerPayload });

        const surfaced = await markers(page, "configlue");
        assert.equal(surfaced.length, 1);
        assert.equal(surfaced[0].severity, 8);
        assert.match(surfaced[0].message, /invalid payload/);

        await page.evaluate(uri => configlueDevToolsMonaco.setRuntimeMarkers(uri, []), documentUri);
        assert.deepEqual(await markers(page, "configlue"), []);

        const state = await page.evaluate(() => configlueDevToolsMonaco.__configlueTestState());
        assert.ok(state.documents.includes(documentUri));
        await page.evaluate(uri => configlueDevToolsMonaco.clearDocument(uri), documentUri);
        const cleared = await page.evaluate(() => configlueDevToolsMonaco.__configlueTestState());
        assert.equal(cleared.documents.includes(documentUri), false);
    } finally { await context.close(); }
});

test("editing the live model works while the placeholder stays inert", async () => {
    const context = await browser.newContext();
    try {
        const page = await newEditorPage(context);
        await page.evaluate(({ uri, json }) => {
            globalThis.editor.setModel(monaco.editor.createModel(json, "json", monaco.Uri.parse(uri)));
        }, { uri: documentUri, json: canonicalJson });

        // Exercise the real input path first; fall back to a programmatic
        // edit only if headless focus swallows the keystroke.
        await page.evaluate(() => {
            globalThis.editor.setPosition({ lineNumber: 2, column: 18 });
            globalThis.editor.trigger("smoke", "type", { text: "!" });
        });
        let value = await page.evaluate(() => globalThis.editor.getValue());
        if (!value.includes('Dark"!')) {
            await page.evaluate(() => {
                const model = globalThis.editor.getModel();
                model.applyEdits([{
                    range: new monaco.Range(2, 18, 2, 18),
                    text: "!",
                    forceMoveMarkers: true,
                }]);
            });
            value = await page.evaluate(() => globalThis.editor.getValue());
        }
        assert.ok(value.includes('Dark"!'));

        // The redacted placeholder is inert editor text: typing over it is a
        // plain text operation. Persisting it as a value is refused
        // server-side (EditorSessionTests Secret_*), never here.
        await page.evaluate(text => globalThis.editor.setValue(text), canonicalJson);
        const restored = await page.evaluate(() => globalThis.editor.getValue());
        assert.ok(restored.includes(`"${redacted}"`));
        assert.equal(restored.includes(password), false);
        // Edit + Save changing the live Configlue state runs in-process in
        // MonacoIntegrationTests.EditSaveThroughHostRegistry_ChangesTheLiveState.
    } finally { await context.close(); }
});
