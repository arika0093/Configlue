// Narrow Configlue-specific Monaco bridge for APIs BlazorMonaco does not wrap
// cleanly: browser JSON language-service schema setup, hover content, inlay
// source labels, and server-side validation markers.
//
// Editor lifecycle, values, text edits, and decorations go through BlazorMonaco.
// This bridge never receives secret plaintext; all payloads are redacted viewer
// metadata (ConfiglueSecrets.RedactedText) produced server-side.
(function () {
  const store = new Map();
  function entry(key) {
    let current = store.get(key);
    if (!current) {
      current = { hovers: new Map(), inlays: [], markers: [] };
      store.set(key, current);
    }
    return current;
  }
  function toMonacoRange(payload) {
    return {
      startLineNumber: payload.startLineNumber,
      startColumn: payload.startColumn,
      endLineNumber: payload.endLineNumber,
      endColumn: payload.endColumn,
    };
  }
  function severityToMonaco(severity) {
    // monaco.MarkerSeverity: Hint=1, Info=2, Warning=4, Error=8.
    switch (String(severity)) {
      case "Error":
        return 8;
      case "Warning":
        return 4;
      case "Info":
        return 2;
      default:
        return 1;
    }
  }
  window.configlueDevToolsMonaco = {
    // Configures the browser-side JSON language service once per model/schema.
    // No per-keystroke round-trips and no remote schema fetching.
    setJsonSchema(uri, schemaJson) {
      try {
        const schema = JSON.parse(schemaJson);
        monaco.languages.json.jsonDefaults.setDiagnosticsOptions({
          validate: true,
          allowComments: false,
          schemas: [{ uri, fileMatch: [uri], schema }],
        });
      } catch (error) {
        console.warn("[configlue] setJsonSchema failed", error);
      }
    },
    // Hover/explain payloads keyed by member path. Monaco hover lookup by
    // document position is resolved here from cached viewer metadata.
    setHoverData(documentKey, hovers) {
      const current = entry(documentKey);
      current.hovers.clear();
      for (const hover of hovers || []) {
        current.hovers.set(hover.memberPath, hover.markdown);
      }
    },
    // Compact source labels rendered as inlay hints (virtual UI, never text).
    setInlayLabels(documentKey, inlays) {
      entry(documentKey).inlays = inlays || [];
    },
    // Server-side Configlue validation beyond JSON Schema, as extra markers.
    setRuntimeMarkers(documentKey, markers) {
      const current = entry(documentKey);
      current.markers = (markers || []).map((marker) => ({
        severity: severityToMonaco(marker.severity),
        message: marker.message,
        ...toMonacoRange(marker),
      }));
      try {
        const models = monaco.editor.getModels();
        for (const model of models) {
          if (model.uri.toString() === documentKey) {
            monaco.editor.setModelMarkers(model, "configlue", current.markers);
          }
        }
      } catch (error) {
        console.warn("[configlue] setRuntimeMarkers failed", error);
      }
    },
  };
})();
