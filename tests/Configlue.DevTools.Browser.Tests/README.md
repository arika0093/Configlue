# DevTools Monaco browser smoke test

Runs the production `configlue-devtools-monaco.js` bridge against a real
Monaco instance (monaco-editor 0.54.0, matching BlazorMonaco) in headless
Chromium. This proves the integration that bUnit cannot: providers actually
registered, schema bound to the real model URI, hover/inlay rendered,
markers targeted, secrets redacted.

```sh
npm ci
npx playwright install chromium
npm test
```

## Coverage mapping to issue #263 item 7

1. page starts + Monaco visible — `page starts…` test.
2. canonical JSON loaded — editor model value assertions.
3. schema validation active — real JSON diagnostic marker after a type error.
4. provenance hover visible — real `.monaco-hover` content plus the shared
   provider lookup used by the registered hover provider.
5. inlay/label visible — inlay label text rendered inside `.monaco-editor`.
6. secret redacted + absent — model value, DOM, hover, and marker payloads.
7. runtime marker surfaced and cleared — `configlue` owner markers.
8. edit + Save changes live state — covered in-process by
   `MonacoIntegrationTests.EditSaveThroughHostRegistry_ChangesTheLiveState`
   and `EditorSessionTests` commit tests (a node page has no live state).
9. secret placeholder never persistable — covered by
   `EditorSessionTests.Secret_PlaceholderSyncIsNoOpWithoutPersistence`,
   `Secret_PlaintextInDraftRejected`, and `Secret_ExplicitChangeFlow`;
   this suite asserts the placeholder stays inert redacted text here.
