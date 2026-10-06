using System.Text.Json;
using Configlue;
using Configlue.Examples.Shared;
using Configlue.Provider.Json;
using Configlue.Source.Http;
using Configlue.Source.PostgreSql;
using Configlue.Source.PostgreSql.Migrations;
using Configlue.Source.Presets;
using Configlue.State;
using Npgsql;

const string IndexHtml = """
    <!doctype html>
    <html lang="en">
    <head><meta charset="utf-8"><title>Configlue Playground</title></head>
    <body>
    <h1>Configlue Playground</h1>
    <p>One runnable showcase for typed read/write configuration state. Each scenario names the Configlue API it demonstrates.</p>
    <ul>
    <li><a href="/api/local-settings">Local writable settings</a> (<code>UseLocalJson</code>)</li>
    <li><a href="/api/layered">Layered configuration and provenance</a> (<code>UseCommonSources</code>, <code>GetDetailsAsync</code>)</li>
    <li><a href="/api/write-behavior">Write behavior: effective vs writable</a> (<code>SaveAsync</code>)</li>
    <li><a href="/api/http-state">HTTP client/server state</a> (<code>UseHttpState</code> / <code>MapConfiglueState</code>)</li>
    <li><a href="/api/postgres-state">PostgreSQL-backed state</a> (<code>FromPostgreSql</code>)</li>
    </ul>
    <p><a href="/api/scenarios">Scenario index (JSON)</a></p>
    </body>
    </html>
    """;

// Configlue Playground: one small web app hosting every introductory scenario.
// Each endpoint is a focused, readable Configlue usage sample:
// local writable settings, layered configuration with provenance, write
// behavior (effective value vs writable destination), HTTP client/server state,
// and PostgreSQL-backed state. Aspire only orchestrates this process (plus its
// HttpServer companion and PostgreSQL); none of the Configlue setup below
// requires Aspire-specific knowledge.
var builder = WebApplication.CreateBuilder(args);

var dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataDirectory);
var localSettingsPath = Path.Combine(dataDirectory, "local-settings.json");
var layeredSettingsPath = Path.Combine(dataDirectory, "layered-settings.json");
var boardEndpoint = ResolveBoardEndpoint(builder.Configuration);

builder.Services.AddHttpClient("board");
builder.Services.AddSingleton(new BoardEndpoint(boardEndpoint));
builder.Services.AddConfiglue(config =>
{
    // Scenario 1: a single local JSON file with ordinary settings defaults.
    config.Add<LocalSettings>().UseLocalJson(localSettingsPath);

    // Scenarios 2-3: layered file plus environment overrides. The explicit
    // file layer is the default write target; the environment layer is
    // read-only and wins when it contributes a value.
    config.UseCommonSources(sources =>
    {
        sources.WithExplicit(layeredSettingsPath);
        sources.WithEnvironment("PLAYGROUND");
        sources.Add<LayeredSettings>();
    });

    // Scenario 4: typed state served by the companion HttpServer project.
    config.Add<SharedBoardSettings>(model =>
        model.UseHttpState(
            boardEndpoint,
            options =>
            {
                options.Writable = true;
                options.ClientFactory = provider =>
                    provider!
                        .GetRequiredService<IHttpClientFactory>()
                        .CreateClient("board");
            }
        )
    );
});

var app = builder.Build();

app.MapGet("/", () => Results.Content(IndexHtml, "text/html"));

app.MapGet(
    "/api/scenarios",
    () =>
        Results.Json(
            new object[]
            {
                new
                {
                    id = "local-settings",
                    title = "Local writable settings",
                    href = "/api/local-settings",
                    configlueApis = new[] { "UseLocalJson", "GetValueAsync", "SaveAsync", "OnChange" },
                    description = "Single JSON file: defaults on first read, atomic save, external-edit observation.",
                },
                new
                {
                    id = "layered",
                    title = "Layered configuration and provenance",
                    href = "/api/layered",
                    configlueApis = new[] { "UseCommonSources", "WithExplicit", "WithEnvironment", "GetDetailsAsync" },
                    description = "Local file plus PLAYGROUND__ environment overrides with per-member source info.",
                },
                new
                {
                    id = "write-behavior",
                    title = "Write behavior: effective vs writable",
                    href = "/api/write-behavior",
                    configlueApis = new[] { "SaveAsync", "StateConflictException", "ConfiglueValidationException", "IsEditable" },
                    description = "Saves land on the writable file layer even when an override shadows the effective value.",
                },
                new
                {
                    id = "http-state",
                    title = "HTTP client/server state",
                    href = "/api/http-state",
                    configlueApis = new[] { "MapConfiglueState", "UseHttpState" },
                    description = "Typed state served by HttpServer and consumed here, including writes and change propagation.",
                },
                new
                {
                    id = "postgres-state",
                    title = "PostgreSQL-backed state",
                    href = "/api/postgres-state",
                    configlueApis = new[] { "FromPostgreSql", "PostgreSqlSchemaMigrator" },
                    description = "Realistic database-backed state. Needs the Aspire AppHost connection string.",
                },
            }
        )
);

// --- Scenario 1: local writable settings ---------------------------------
app.MapGet(
    "/api/local-settings",
    async (IWritableState<LocalSettings> settings) =>
    {
        var value = await settings.GetValueAsync();
        var details = await settings.GetDetailsAsync();
        return Results.Json(
            new
            {
                value,
                file = Path.GetFileName(localSettingsPath),
                fileExists = File.Exists(localSettingsPath),
                provenance = new
                {
                    theme = Provenance(details.Theme),
                    retryCount = Provenance(details.RetryCount),
                },
            }
        );
    }
);

app.MapPost(
    "/api/local-settings",
    async (IWritableState<LocalSettings> settings, LocalSettingsPatch patch) =>
    {
        try
        {
            await settings.SaveAsync(model =>
            {
                if (patch.Name is not null)
                {
                    model.Name = patch.Name;
                }

                if (patch.Theme is not null)
                {
                    model.Theme = patch.Theme;
                }

                if (patch.RetryCount is { } retryCount)
                {
                    model.RetryCount = retryCount;
                }
            });
        }
        catch (ConfiglueValidationException exception)
        {
            return Results.Json(new { failures = exception.Failures }, statusCode: 422);
        }

        return Results.Json(new { value = await settings.GetValueAsync() });
    }
);

// --- Scenario 2: layered configuration and provenance ----------------------
app.MapGet(
    "/api/layered",
    async (IWritableState<LayeredSettings> settings) =>
    {
        var value = await settings.GetValueAsync();
        var details = await settings.GetDetailsAsync();
        return Results.Json(
            new
            {
                effective = value,
                layers = new object[]
                {
                    new
                    {
                        id = "explicit-file",
                        kind = "local JSON file",
                        file = Path.GetFileName(layeredSettingsPath),
                        writable = true,
                    },
                    new
                    {
                        id = "environment",
                        kind = "environment variables",
                        prefix = "PLAYGROUND",
                        example = "PLAYGROUND__THEME=Dark",
                        writable = false,
                    },
                },
                provenance = new
                {
                    theme = Provenance(details.Theme),
                    retryCount = Provenance(details.RetryCount),
                    label = Provenance(details.Label),
                },
            }
        );
    }
);

// --- Scenario 3: write behavior -------------------------------------------
app.MapGet(
    "/api/write-behavior",
    async (IWritableState<LayeredSettings> settings) =>
    {
        var value = await settings.GetValueAsync();
        var details = await settings.GetDetailsAsync();
        return Results.Json(
            new
            {
                effective = value,
                writableDestination = new
                {
                    layer = "explicit file",
                    file = Path.GetFileName(layeredSettingsPath),
                },
                members = new object[]
                {
                    WriteMember("theme", details.Theme),
                    WriteMember("retryCount", details.RetryCount),
                    WriteMember("label", details.Label),
                },
                note = "POST a member patch to save it. Saving a shadowed member returns 409: "
                    + "the write reaches the file, but the higher-priority layer keeps the effective value.",
            }
        );
    }
);

app.MapPost(
    "/api/write-behavior",
    async (IWritableState<LayeredSettings> settings, WriteAttempt attempt) =>
    {
        try
        {
            switch (attempt.Member.ToLowerInvariant())
            {
                case "theme":
                    await settings.SaveAsync(model =>
                        model.Theme = attempt.Value.GetString() ?? string.Empty
                    );
                    break;
                case "label":
                    await settings.SaveAsync(model =>
                        model.Label = attempt.Value.GetString() ?? string.Empty
                    );
                    break;
                case "retrycount":
                    await settings.SaveAsync(model =>
                        model.RetryCount = attempt.Value.GetInt32()
                    );
                    break;
                default:
                    return Results.Json(
                        new { error = $"Unknown member '{attempt.Member}'. Use theme, label, or retryCount." },
                        statusCode: 400
                    );
            }
        }
        catch (StateConflictException exception)
        {
            return Results.Json(
                new
                {
                    error = "shadowed",
                    message = exception.Message,
                    hint = "A higher-priority layer overrides this member, so the requested effective value cannot take effect.",
                },
                statusCode: 409
            );
        }
        catch (ConfiglueValidationException exception)
        {
            return Results.Json(new { failures = exception.Failures }, statusCode: 422);
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        {
            return Results.Json(
                new { error = $"Value for '{attempt.Member}' has an unexpected JSON type." },
                statusCode: 400
            );
        }

        return Results.Json(new { value = await settings.GetValueAsync() });
    }
);

// --- Scenario 4: HTTP client/server state -----------------------------------
app.MapGet(
    "/api/http-state",
    async (IWritableState<SharedBoardSettings> board, BoardEndpoint endpoint) =>
    {
        try
        {
            var value = await board.GetValueAsync();
            var details = await board.GetDetailsAsync();
            return Results.Json(
                new
                {
                    status = "ok",
                    serverEndpoint = endpoint.Url,
                    effective = value,
                    provenance = new { title = Provenance(details.Title), note = Provenance(details.Note) },
                    changePropagation = "The client watches the server events endpoint: server-side saves notify this client.",
                }
            );
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return Results.Json(
                new
                {
                    status = "unavailable",
                    serverEndpoint = endpoint.Url,
                    hint = "Start the companion HttpServer (or run the Aspire AppHost) so this endpoint is reachable.",
                },
                statusCode: 503
            );
        }
    }
);

app.MapPost(
    "/api/http-state",
    async (IWritableState<SharedBoardSettings> board, BoardEndpoint endpoint, BoardPatch patch) =>
    {
        try
        {
            await board.SaveAsync(model =>
            {
                if (patch.Title is not null)
                {
                    model.Title = patch.Title;
                }

                if (patch.Note is not null)
                {
                    model.Note = patch.Note;
                }
            });
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return Results.Json(
                new
                {
                    status = "unavailable",
                    serverEndpoint = endpoint.Url,
                },
                statusCode: 503
            );
        }

        return Results.Json(new { value = await board.GetValueAsync() });
    }
);

// --- Scenario 5: PostgreSQL-backed state ------------------------------------
app.MapGet(
    "/api/postgres-state",
    async (IConfiguration configuration) =>
    {
        var outcome = await WithPostgresStateAsync(
            configuration,
            static async state =>
            {
                var value = await state.GetValueAsync();
                var details = await state.GetDetailsAsync();
                return Results.Json(
                    new
                    {
                        status = "ok",
                        resourceNamespace = PlaygroundPostgres.ResourceNamespace,
                        effective = value,
                        provenance = new { title = Provenance(details.Title), note = Provenance(details.Note) },
                    }
                );
            }
        );
        return outcome;
    }
);

app.MapPost(
    "/api/postgres-state",
    async (IConfiguration configuration, BoardPatch patch) =>
        await WithPostgresStateAsync(
            configuration,
            async state =>
            {
                await state.SaveAsync(model =>
                {
                    if (patch.Title is not null)
                    {
                        model.Title = patch.Title;
                    }

                    if (patch.Note is not null)
                    {
                        model.Note = patch.Note;
                    }
                });
                return Results.Json(new { value = await state.GetValueAsync() });
            }
        )
);

app.Run();

static string ResolveBoardEndpoint(IConfiguration configuration)
{
    var configured = configuration["HttpServer:Endpoint"];
    if (!string.IsNullOrWhiteSpace(configured))
    {
        return configured.TrimEnd('/');
    }

    // Aspire service reference injected by WithReference(httpServer).
    var discovered = configuration["services:httpserver:http:0"];
    if (!string.IsNullOrWhiteSpace(discovered))
    {
        return discovered.TrimEnd('/') + "/api/board";
    }

    return "http://localhost:5051/api/board";
}

static object Provenance<T>(ConfigValueDetails<T> leaf) =>
    new
    {
        value = leaf.Value,
        source = leaf.Source is null
            ? null
            : new
            {
                kind = leaf.Source.Kind,
                displayName = leaf.Source.DisplayName,
                canWrite = leaf.Source.CanWrite,
            },
        editable = leaf.IsEditable,
        editability = leaf.Editability.ToString(),
        contributions = leaf
            .Sources.Select(entry => new
            {
                source = entry.Source.DisplayName,
                kind = entry.Source.Kind,
                present = entry.IsPresent,
                shadowed = entry.IsShadowed,
                value = entry.Value,
            })
            .ToArray(),
    };

static object WriteMember<T>(string name, ConfigValueDetails<T> leaf) =>
    new
    {
        member = name,
        effectiveValue = leaf.Value,
        effectiveSource = leaf.Source?.DisplayName,
        editable = leaf.IsEditable,
        editability = leaf.Editability.ToString(),
    };

/// <summary>Runs one PostgreSQL-backed interaction when a connection string is configured.</summary>
static async Task<IResult> WithPostgresStateAsync(
    IConfiguration configuration,
    Func<IWritableState<SharedBoardSettings>, ValueTask<IResult>> interact
)
{
    // Aspire injects ConnectionStrings__playground via WithReference(postgres).
    var connectionString = configuration.GetConnectionString("playground");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Json(
            new
            {
                status = "not-configured",
                hint = "Run through the Aspire AppHost so a PostgreSQL connection string is provided, "
                    + "or set ConnectionStrings__playground to a migrated Configlue database.",
            },
            statusCode: 503
        );
    }

    try
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var tableOptions = new PostgreSqlTableOptions();
        await new PostgreSqlSchemaMigrator(
            dataSource,
            new PostgreSqlMigrationOptions { TableOptions = tableOptions }
        )
            .MigrateAsync();
        await using var context = ConfiglueApp.CreateContext(config =>
        {
            config.Add<SharedBoardSettings>(model =>
            {
                model.Sources(sources =>
                    sources.FromPostgreSql(
                        new PostgreSqlSourceOptions
                        {
                            Id = PlaygroundPostgres.SourceId,
                            ResourceNamespace = PlaygroundPostgres.ResourceNamespace,
                            DataSource = dataSource,
                            TableOptions = tableOptions,
                        }
                    )
                );
                model.Writes(write =>
                    write.DefaultTo(
                        SourceKey<SharedBoardSettings>.Named(PlaygroundPostgres.SourceId)
                    )
                );
            });
        });
        return await interact(context.GetState<SharedBoardSettings>());
    }
    catch (Exception exception) when (exception is NpgsqlException or TimeoutException or InvalidOperationException)
    {
        return Results.Json(
            new { status = "unavailable", message = exception.Message },
            statusCode: 503
        );
    }
}

/// <summary>Well-known PostgreSQL identity used by the Playground scenario.</summary>
static class PlaygroundPostgres
{
    public const string SourceId = "postgres";
    public const string ResourceNamespace = "playground-board";
}

/// <summary>The State HTTP endpoint served by the companion HttpServer project.</summary>
sealed record BoardEndpoint(string Url);

/// <summary>Patch body for the local-settings scenario endpoint.</summary>
sealed record LocalSettingsPatch(string? Name, string? Theme, int? RetryCount);

/// <summary>Single-member patch body for the write-behavior scenario endpoint.</summary>
sealed record WriteAttempt(string Member, JsonElement Value);

/// <summary>Patch body for the shared board endpoints.</summary>
sealed record BoardPatch(string? Title, string? Note);
