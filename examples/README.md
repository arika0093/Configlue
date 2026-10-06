# Configlue Examples

A small runnable showcase for Configlue, orchestrated with
[.NET Aspire](https://learn.microsoft.com/en-us/dotnet/aspire/).
Aspire owns process/container wiring only; every Configlue usage stays
readable without Aspire-specific knowledge.

## Projects

| Project | Role |
|---|---|
| `Configlue.Examples.AppHost` | Aspire orchestrator: starts the Playground, the HttpServer, and PostgreSQL. |
| `Configlue.Examples.Playground` | The single primary example app. One scenario per endpoint (see below). |
| `Configlue.Examples.HttpServer` | Tiny companion for the HTTP scenario: serves one board document over the typed Configlue State HTTP protocol. |
| `Configlue.Examples.Shared` | Settings models shared by the Playground, the HttpServer, and the smoke tests. |
| `Configlue.Examples.Smoke` | Lightweight tests mirroring each scenario. No containers, no servers, no live database. |

PostgreSQL is the only container-backed dependency. Redis, S3/MinIO, Consul,
Vault, Etcd, cloud services, Kubernetes, and desktop/game hosts are
deliberately out of scope here.

## Run

A container runtime is the only prerequisite (for PostgreSQL):

```bash
dotnet run --project examples/Configlue.Examples.AppHost
```

Open the Aspire dashboard, then the `playground` URL (for example
`http://localhost:5050`). Start at `/` for the scenario list.

Without Aspire, the Playground and HttpServer also run standalone:

```bash
dotnet run --project examples/Configlue.Examples.HttpServer
dotnet run --project examples/Configlue.Examples.Playground
```

Standalone defaults assume the HttpServer at `http://localhost:5051/api/board`
(see `Properties/launchSettings.json`). Override it with
`HttpServer__Endpoint`. The PostgreSQL scenario reports `not-configured`
until `ConnectionStrings__playground` is provided (the AppHost wires this for
you, including schema migration at request time).

## Scenarios

| Endpoint | Configlue API | What it shows |
|---|---|---|
| `GET /api/local-settings`, `POST /api/local-settings` | `UseLocalJson`, `GetValueAsync`, `SaveAsync`, `OnChange` | Single-file settings: defaults before the first save, atomic save, validation, external-edit observation. |
| `GET /api/layered` | `UseCommonSources`, `WithExplicit`, `WithEnvironment`, `GetDetailsAsync` | Local file plus `PLAYGROUND__` environment overrides, with per-member effective value and provenance. Try `PLAYGROUND__THEME=Dark`. |
| `GET /api/write-behavior`, `POST /api/write-behavior` | `SaveAsync`, `StateConflictException`, `IsEditable` | Effective value vs writable destination: saves land on the file layer; shadowed members return 409 instead of silently diverging. |
| `GET /api/http-state`, `POST /api/http-state` | `MapConfiglueState` (server), `UseHttpState` (client) | Typed state over HTTP: reads, client writes, and server-to-client change propagation. |
| `GET /api/postgres-state`, `POST /api/postgres-state` | `FromPostgreSql`, `PostgreSqlSchemaMigrator` | Realistic database-backed state in the `playground-board` namespace. |

## Tests

```bash
dotnet test examples/Configlue.Examples.Smoke --configuration Release
```

The smoke tests replay each scenario against temporary files, an in-process
test server, and provider-injected environment input. Exhaustive behavioral
coverage stays in `tests/Configlue.Tests`; this project only prevents the
showcase from silently rotting.
