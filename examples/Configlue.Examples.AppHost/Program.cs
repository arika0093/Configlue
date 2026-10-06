// Configlue examples orchestrator. Aspire owns process and container wiring only:
// the Playground plus its HttpServer companion and PostgreSQL. No other
// infrastructure is required; PostgreSQL is the only container-backed
// dependency in this example environment.
var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder
    .AddPostgres("postgres")
    .WithDataVolume()
    .AddDatabase("playground");

var httpServer = builder.AddProject<Projects.Configlue_Examples_HttpServer>("httpserver");

builder
    .AddProject<Projects.Configlue_Examples_Playground>("playground")
    .WithReference(postgres)
    .WithReference(httpServer)
    .WaitFor(postgres)
    .WaitFor(httpServer);

builder.Build().Run();
