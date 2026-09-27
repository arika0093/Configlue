---
title: Why Configlue
description: What problems Configlue solves and how it differs from plain options binding.
---

# Why Configlue

`Microsoft.Extensions.Options` binds one configuration tree into one object. That works until settings come from several places with different owners and lifetimes: a shipped default file, a user file, environment variables in containers, command-line overrides in development, and a policy document fetched over HTTP.

Configlue treats each of those as an independent **source** that contributes a **sparse fragment** — only the members it actually provides. The runtime merges the present members by priority into one typed model. Writing goes back to a chosen source without disturbing the others.

## The core ideas

* **Sources own fields, not files.** A source can be a whole file, a nested section (`App:Policy`), one ZIP entry, one environment-variable prefix, or a remote endpoint. Several sources can feed one model subtree.
* **Presence is explicit.** Generated fragments distinguish a missing member from a present `null` or default value, so layering never confuses "not set" with "set to default".
* **Reads merge, writes route.** Reads combine every source by priority. Writes target one source by default (`WriteRoute`) or split across sources per path (`WritePlan`).
* **Storage and schema are separate concerns.** Resources (where bytes live), codecs (how bytes become values), and sources (which logical contribution) compose independently, and both can evolve: schema versions migrate models, storage migration copies contributions between sources with verification.

## When Configlue fits

* User settings layered over shipped defaults, with per-user files in platform-standard directories.
* Desktop or CLI apps that combine files, environment variables, and command-line overrides.
* Multi-profile applications (personal/work tenants, per-document settings) sharing one model.
* Applications outgrowing a single `appsettings.json` that need sections, remote policy, and safe migration paths.

## When it may be overkill

* A single static file read once at startup with no writes, profiles, or layering — plain options binding or a direct file read is simpler.
* Full-document replacement semantics where sparse merging would surprise you. (Configlue can do whole-contribution replacement via `SaveAsync(value)`, but its default mindset is sparse.)

## Relationship to Configuration.Writable

Configlue is the architectural successor to [Configuration.Writable](https://github.com/arika0093/Configuration.Writable): backend-neutral state, sparse fragments, and multi-source layering instead of one file per options type. Existing JSON/YAML files can be adopted as read-only migration inputs — see [Adopting Configuration.Writable](../migration/adopting-configuration-writable.md).

Next: [Installation](./installation.md).
