using Configlue;
using Configlue.Examples.Shared;
using Configlue.Hosting.AspNetCore;
using Configlue.Source.Http;
using Configlue.Sources;
using Configlue.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using TUnit.Core;

namespace Configlue.Examples.Smoke;

// Lightweight smoke coverage for the Playground HTTP client/server scenario:
// the same SharedBoardSettings shape is served over the State HTTP protocol
// and consumed through UseHttpState, including writes and change propagation.
// Uses in-process TestServer only; no sockets or containers.
public sealed class HttpStateSmokeTests
{
    [Test]
    public async Task HttpState_ReadWriteRoundtrip()
    {
        var store = new InMemoryStateSource<SharedBoardSettings.Fragment>(
            new SharedBoardSettings.Fragment
            {
                Title = Optional<string>.Present("Server board"),
            }
        );
        var serverBuilder = WebApplication.CreateBuilder();
        serverBuilder.WebHost.UseTestServer();
        serverBuilder.Services.AddConfiglue(config =>
        {
            config.Add<SharedBoardSettings>(model =>
            {
                model.Sources(sources =>
                    sources.Add(
                        new StateSource<SharedBoardSettings.Fragment>(
                            "server-store",
                            store,
                            new StateSourceOptions<SharedBoardSettings.Fragment>
                            {
                                Writer = store,
                                Watcher = store,
                            }
                        )
                    )
                );
                model.Writes(write =>
                    write.DefaultTo(SourceKey<SharedBoardSettings>.Named("server-store"))
                );
            });
        });
        var server = serverBuilder.Build();
        server.MapConfiglueState<SharedBoardSettings>("/api/board");
        await server.StartAsync();
        await using (server)
        {
            using var httpClient = server.GetTestClient();
            httpClient.BaseAddress = new Uri("http://localhost");

            await using var client = ConfiglueApp.CreateContext(config =>
            {
                config.Add<SharedBoardSettings>(model =>
                    model.UseHttpState(
                        "http://localhost/api/board",
                        options =>
                        {
                            options.Writable = true;
                            options.Client = httpClient;
                        }
                    )
                );
            });

            var state = client.GetState<SharedBoardSettings>();
            (await state.GetValueAsync()).Title.ShouldBe("Server board");

            // Writes travel through the client to the server document.
            await state.SaveAsync(patch =>
            {
                patch.Note = "from-client";
            });
            (await state.GetValueAsync()).Note.ShouldBe("from-client");
            (await server.Services.GetRequiredService<IWritableState<SharedBoardSettings>>().GetValueAsync()).Note.ShouldBe(
                "from-client"
            );
        }
    }

    [Test]
    public async Task HttpState_ServerChangePropagatesToClient()
    {
        var store = new InMemoryStateSource<SharedBoardSettings.Fragment>(
            new SharedBoardSettings.Fragment
            {
                Title = Optional<string>.Present("Server board"),
            }
        );
        var serverBuilder = WebApplication.CreateBuilder();
        serverBuilder.WebHost.UseTestServer();
        serverBuilder.Services.AddConfiglue(config =>
        {
            config.Add<SharedBoardSettings>(model =>
            {
                model.Sources(sources =>
                    sources.Add(
                        new StateSource<SharedBoardSettings.Fragment>(
                            "server-store",
                            store,
                            new StateSourceOptions<SharedBoardSettings.Fragment>
                            {
                                Writer = store,
                                Watcher = store,
                            }
                        )
                    )
                );
                model.Writes(write =>
                    write.DefaultTo(SourceKey<SharedBoardSettings>.Named("server-store"))
                );
            });
        });
        var server = serverBuilder.Build();
        server.MapConfiglueState<SharedBoardSettings>("/api/board");
        await server.StartAsync();
        await using (server)
        {
            using var httpClient = server.GetTestClient();
            httpClient.BaseAddress = new Uri("http://localhost");

            await using var client = ConfiglueApp.CreateContext(config =>
            {
                config.Add<SharedBoardSettings>(model =>
                    model.UseHttpState(
                        "http://localhost/api/board",
                        options =>
                        {
                            options.Writable = true;
                            options.Client = httpClient;
                        }
                    )
                );
            });

            var state = client.GetState<SharedBoardSettings>();
            // Establish the SSE watch before the server-side change.
            (await state.GetValueAsync()).Title.ShouldBe("Server board");

            string? observed = null;
            using var subscription = state.OnChange(changed =>
                Interlocked.Exchange(ref observed, changed.Title)
            );

            var serverState = server.Services.GetRequiredService<IWritableState<SharedBoardSettings>>();
            await serverState.SaveAsync(patch =>
            {
                patch.Title = "Changed on server";
            });

            for (
                var attempt = 0;
                attempt < 75 && Volatile.Read(ref observed) != "Changed on server";
                attempt++
            )
            {
                await Task.Delay(200);
            }

            Volatile.Read(ref observed).ShouldBe("Changed on server");
            (await state.GetValueAsync()).Title.ShouldBe("Changed on server");
        }
    }
}
