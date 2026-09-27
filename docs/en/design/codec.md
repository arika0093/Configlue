---
title: "Design: Codec"
description: Bytes-to-values conversion and per-format notes.
---

# Design: Codec (conversion)

A Codec converts bytes to typed values and back, with no Resource I/O. It owns "how to read", not "where to keep".

## One Codec per format

- JSON: `JsonStateCodec`, combined with section resources, file registration, and JSON Schema export. Pass a source-generated `JsonSerializerContext` for trimming-safe, NativeAOT-friendly behavior.
- XML: the XML Codec, with section resources and file registration.
- YAML: the YAML Codec, with section resources and file registration. See `example/Example.ConsoleApp.Yaml` for camel-case naming.
- Legacy: `ConfigurationWritableJsonStateCodec` / `ConfigurationWritableYamlStateCodec`, for reading `Configuration.Writable` files as read-only migration inputs. See the [adoption guide](../migration/adopting-configuration-writable.md).

## Choosing

When in doubt, match the file format. Add one Codec per format you read, and narrow writes to one destination (STEP 10's YAML-first, JSON-second shape is typical). Mixing formats changes nothing about Source priority or `WriteRoute`.
