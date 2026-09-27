---
title: What is Configlue
description: Where the name comes from, and how Configlue approaches settings.
---

# What is Configlue

## The name comes from "glue"

Configlue is **Config + glue**: a library that sticks multiple setting sources together.

A typical app's settings are scattered: a shipped defaults file, a user-saved file, company-distributed policy (over HTTP, for example), container environment variables, and command-line flags added only while debugging. Different locations, different owners, different change frequencies. Configlue takes the glue role: each source contributes only the fields it knows, and at runtime they become one typed model.

## The order in which settings break

Settings usually become painful in this order:

1. One JSON file is enough at first.
2. Per-user saving appears, so files get split.
3. Environment-variable and command-line overrides grow, and priority rules scatter through the code.
4. Saving one field rewrites the whole file and breaks unrelated fields or comments.
5. When fields are added or renamed, old files have no clear handling and migration stalls.

The common fix is adding one-off branches. It works at first, but "where did this value come from" becomes unreadable, and every change feels scary.

## How Configlue approaches it

Reverse the idea: instead of reading settings as "one tree", treat them as **a collection of small contributions, one per origin**.

- **Sources own fields.** A whole file, a nested section, an environment-variable prefix, or a remote fetch result all qualify. Multiple sources can each own a different part of one model.
- **Missing stays missing.** Generated code distinguishes "unset" from "set to `null` or a default". Layering never lets an unset field overwrite a default.
- **Reads merge, writes target.** Reads combine by priority. Writes go to one source by default, or split across destinations per field when needed. Unrelated sources stay clean.
- **Locations and shapes evolve separately.** Where bytes live (Resource), how bytes become values (Codec), and which logical contribution (Source) are separate ideas. Shape changes travel through versioning; location moves travel through verified copies.

That is why new sources can be added later without throwing old files away. For the mechanics, see the [design overview](../design/overview.md); to learn by doing, continue with the [tutorial](./01-first-file-app.md).

## Fits and misfits

The fit is "settings after growth": user files layered over shipped defaults, files plus environment plus command line, profiles or per-document settings, or life after a single `appsettings.json`.

If instead you have one static file read once at startup, with no writes, layering, or migration, plain options binding or a direct file read is enough. No need to force it.

## Relationship to Configuration.Writable

Configlue is the architectural successor to [Configuration.Writable](https://github.com/arika0093/Configuration.Writable): from "one file per type" to "layered sources with sparse fragments". Existing JSON/YAML files can be adopted as read-only migration inputs — see the [adoption guide](../migration/adopting-configuration-writable.md).

Next: [Installation](./installation.md).
