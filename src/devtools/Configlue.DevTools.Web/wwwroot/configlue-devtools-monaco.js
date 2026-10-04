// Narrow Configlue-specific Monaco bridge for APIs BlazorMonaco does not wrap
// cleanly: browser JSON language-service schema setup, hover content, inlay
// source labels, and server-side validation markers.
//
// Editor lifecycle, values, text edits, and decorations go through BlazorMonaco.
// This bridge never receives secret plaintext; all payloads are redacted viewer
// metadata (ConfiglueSecrets.RedactedText) produced server-side.
//
// Providers are registered once per page lifetime (guarded): a hover provider
// and an inlay-hints provider for `json`. Both resolve the current document by
// the editor model's URI, which the host binds to an explicit document URI of
// the form `configlue://states/{modelId}/{stateName}` via ensureDocumentModel.
// Per-document payloads are keyed by that URI and can be disposed cleanly when
// the DevTools selection changes or the editor is recreated.
(function () {
  const store = new Map();
  const disposables = [];
  let providersRegistered = false;

  function entry(key) {
    let current = store.get(key);
    if (!current) {
      current = { hovers: new Map(), ranges: new Map(), inlays: [], markers: [] };
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

  function rangeContains(range, lineNumber, column) {
    if (lineNumber < range.startLineNumber || lineNumber > range.endLineNumber) {
      return false;
    }
    if (lineNumber === range.startLineNumber && column < range.startColumn) {
      return false;
    }
    if (lineNumber === range.endLineNumber && column > range.endColumn) {
      return false;
    }
    return true;
  }

  // Shared hover lookup used by the registered provider: resolve the member
  // whose value range contains the position and return its stored markdown.
  function resolveHoverMarkdown(documentKey, lineNumber, column) {
    const current = store.get(documentKey);
    if (!current) {
      return null;
    }
    for (const [memberPath, range] of current.ranges) {
      if (rangeContains(range, lineNumber, column)) {
        const markdown = current.hovers.get(memberPath);
        if (markdown) {
          return { memberPath, markdown };
        }
      }
    }
    return null;
  }

  function findBlazorEditor(editorId) {
    try {
      const holders =
        (window.blazorMonaco && window.blazorMonaco.editors) || [];
      const holder = holders.find((candidate) => candidate && candidate.id === editorId);
      return holder && holder.editor ? holder.editor : null;
    } catch (error) {
      console.warn("[configlue] findBlazorEditor failed", error);
      return null;
    }
  }

  function ensureProviders() {
    if (providersRegistered) {
      return true;
    }
    try {
      if (typeof monaco === "undefined" || !monaco.languages) {
        return false;
      }
      disposables.push(
        monaco.languages.registerHoverProvider("json", {
          provideHover(model, position) {
            const hit = resolveHoverMarkdown(
              model.uri.toString(),
              position.lineNumber,
              position.column
            );
            if (!hit) {
              return null;
            }
            const range = entry(model.uri.toString()).ranges.get(hit.memberPath);
            return {
              range: range ? new monaco.Range(
                range.startLineNumber,
                range.startColumn,
                range.endLineNumber,
                range.endColumn
              ) : undefined,
              contents: [{ value: hit.markdown }],
            };
          },
        })
      );
      disposables.push(
        monaco.languages.registerInlayHintsProvider("json", {
          provideInlayHints(model) {
            const current = store.get(model.uri.toString());
            if (!current) {
              return { hints: [], dispose: () => {} };
            }
            const hints = [];
            for (const inlay of current.inlays) {
              hints.push({
                kind: monaco.languages.InlayHintKind.Inline,
                label: inlay.label,
                position: {
                  lineNumber: inlay.endLineNumber,
                  column: inlay.endColumn,
                },
                paddingLeft: true,
              });
            }
            return { hints, dispose: () => {} };
          },
        })
      );
      providersRegistered = true;
      return true;
    } catch (error) {
      console.warn("[configlue] ensureProviders failed", error);
      return false;
    }
  }

  function clearMarkersOnModels(documentKey, markers) {
    try {
      const models = monaco.editor.getModels();
      for (const model of models) {
        if (model.uri.toString() === documentKey) {
          monaco.editor.setModelMarkers(model, "configlue", markers);
        }
      }
    } catch (error) {
      console.warn("[configlue] clearMarkersOnModels failed", error);
    }
  }

  window.configlueDevToolsMonaco = {
    // Binds the BlazorMonaco editor model to the explicit Configlue document
    // URI so schema fileMatch and marker targeting match the real model.
    // Swaps the anonymous model once; later calls are no-ops that preserve
    // scroll/selection. Returns the bound model URI (or null when deferred).
    ensureDocumentModel(editorId, documentKey, language, fallbackText) {
      ensureProviders();
      try {
        const editor = findBlazorEditor(editorId);
        if (!editor) {
          return null;
        }
        const current = editor.getModel();
        if (current && current.uri.toString() === documentKey) {
          return documentKey;
        }
        const text =
          current && typeof current.getValue === "function"
            ? current.getValue()
            : fallbackText || "{\n}";
        const named = monaco.editor.createModel(
          text,
          language || "json",
          monaco.Uri.parse(documentKey)
        );
        editor.setModel(named);
        if (current && typeof current.dispose === "function") {
          current.dispose();
        }
        return documentKey;
      } catch (error) {
        console.warn("[configlue] ensureDocumentModel failed", error);
        return null;
      }
    },
    // Configures the browser-side JSON language service once per model/schema.
    // fileMatch targets the explicit document URI (not the schema URI and not
    // the anonymous model URI). No per-keystroke round-trips and no remote
    // schema fetching.
    setJsonSchema(schemaUri, schemaJson, documentUri) {
      ensureProviders();
      try {
        const schema = JSON.parse(schemaJson);
        const fileMatch = documentUri || schemaUri;
        monaco.languages.json.jsonDefaults.setDiagnosticsOptions({
          validate: true,
          allowComments: false,
          schemas: [{ uri: schemaUri, fileMatch: [fileMatch], schema }],
        });
      } catch (error) {
        console.warn("[configlue] setJsonSchema failed", error);
      }
    },
    // Hover/explain payloads keyed by member path plus the member value ranges
    // used to resolve a document position to its member efficiently.
    setHoverData(documentKey, hovers, ranges) {
      ensureProviders();
      const current = entry(documentKey);
      current.hovers.clear();
      current.ranges.clear();
      for (const hover of hovers || []) {
        current.hovers.set(hover.memberPath, hover.markdown);
      }
      for (const range of ranges || []) {
        current.ranges.set(range.memberPath, toMonacoRange(range));
      }
    },
    // Compact source labels rendered as inlay hints (virtual UI, never text).
    setInlayLabels(documentKey, inlays) {
      ensureProviders();
      entry(documentKey).inlays = inlays || [];
    },
    // Server-side Configlue validation beyond JSON Schema, as extra markers.
    // An empty payload clears the markers on the matching model.
    setRuntimeMarkers(documentKey, markers) {
      ensureProviders();
      const current = entry(documentKey);
      current.markers = (markers || []).map((marker) => ({
        severity: severityToMonaco(marker.severity),
        message: marker.message,
        ...toMonacoRange(marker),
      }));
      clearMarkersOnModels(documentKey, current.markers);
    },
    // Drops cached payloads for a closed document and clears its markers, so
    // selection changes and disposal never leak overlays across states.
    clearDocument(documentKey) {
      store.delete(documentKey);
      try {
        clearMarkersOnModels(documentKey, []);
      } catch (error) {
        console.warn("[configlue] clearDocument failed", error);
      }
    },
    // Deterministic test hook for the real-browser smoke test: proves provider
    // registration and per-document payload state without reaching into
    // Monaco's internal provider registries.
    __configlueTestState() {
      return {
        providersRegistered,
        hoverProviderCount: disposables.length > 0 ? 1 : 0,
        inlayProviderCount: disposables.length > 1 ? 1 : 0,
        documents: Array.from(store.keys()),
      };
    },
    // Position lookup shared with the hover provider, exposed for direct
    // Monaco API assertions in the smoke test.
    __resolveHover(documentKey, lineNumber, column) {
      return resolveHoverMarkdown(documentKey, lineNumber, column);
    },
  };

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
})();
