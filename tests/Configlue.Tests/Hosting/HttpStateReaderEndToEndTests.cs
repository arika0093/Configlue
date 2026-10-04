using Configlue.Hosting.AspNetCore;
using Configlue.Source.Http;
using Configlue.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;

namespace Configlue.Tests;

/// <summary>Single end-to-end source integration path over the State HTTP wire protocol.</summary>
public sealed class HttpStateReaderEndToEndTests
{
    [Test]
    public async Task HttpSource_ReadWriteRoundtrip()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddConfiglue(config =>
        {
            config.Add<AppSettings>(model =>
            {
                model.Sources(sources =>
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "server-store",
                            store,
                            new StateSourceOptions<AppSettings.Fragment>
                            {
                                Writer = store,
                                Watcher = store,
                            }
                        )
                    )
                );
                model.Writes(write =>
                    write.DefaultTo(SourceKey<AppSettings>.Named("server-store"))
                );
            });
        });
        var app = builder.Build();
        app.MapConfiglueState<AppSettings>("/api/settings");
        await app.StartAsync();
        await using (app)
        {
            using var httpClient = app.GetTestClient();
            httpClient.BaseAddress = new Uri("http://localhost");

            await using var context = ConfiglueApp.CreateContext(builder =>
            {
                builder.Add<AppSettings>(model =>
                    model.Sources(sources =>
                        sources.FromHttpState(
                            new HttpStateSourceOptions
                            {
                                Id = "http-state",
                                EndPoint = "http://localhost/api/settings",
                                Client = httpClient,
                                Writable = true,
                            }
                        )
                    )
                );
            });

            var state = (IConfiglueRuntimeState<AppSettings>)context.GetState<AppSettings>();
            (await state.GetValueAsync()).RetryCount.ShouldBe(1);

            await state.SaveAsync(settings => settings.RetryCount = 21);
            (await state.GetValueAsync()).RetryCount.ShouldBe(21);
        }
    }
}
