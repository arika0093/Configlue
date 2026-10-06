using Configlue;
using Configlue.Examples.Shared;
using Configlue.Hosting.AspNetCore;
using Configlue.Provider.Json;

// Minimal Configlue state server for the Playground HTTP client/server scenario.
// It owns one SharedBoardSettings document (local JSON file) and exposes it over
// the typed Configlue State HTTP protocol. The Playground consumes this endpoint
// through UseHttpState, including writes and change propagation.
var builder = WebApplication.CreateBuilder(args);

var dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataDirectory);

builder.Services.AddConfiglue(config =>
{
    config
        .Add<SharedBoardSettings>()
        .UseLocalJson(Path.Combine(dataDirectory, "board-settings.json"));
});

var app = builder.Build();

app.MapGet(
    "/",
    () =>
        Results.Json(
            new
            {
                service = "configlue-examples-httpserver",
                stateEndpoint = "/api/board",
                eventsEndpoint = "/api/board/events",
            }
        )
);

app.MapConfiglueState<SharedBoardSettings>("/api/board");

app.Run();
