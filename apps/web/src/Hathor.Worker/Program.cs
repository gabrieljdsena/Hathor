using Hathor.Infrastructure.Logging;
using Hathor.Worker;
using Serilog;
using Serilog.Events;

var builder = Host.CreateApplicationBuilder(args);

// Same logging flow as the API (gitignored secrets file, console + file,
// lab Postgres `logs` table for Error/Fatal) so background job failures
// land in the same place as request failures.
// Same layering as the API: secrets just above appsettings.json so env
// vars and CLI always win over the file.
builder.Configuration.Sources.Insert(1, new Microsoft.Extensions.Configuration.Json.JsonConfigurationSource
{
    Path = "appsettings.Secrets.json",
    Optional = true,
    ReloadOnChange = true,
});

builder.Services.AddSerilog((services, configuration) => configuration
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.Sink(
        new PostgresLogSink(LogsDbConnection.Resolve(builder.Configuration)),
        restrictedToMinimumLevel: LogEventLevel.Error));

builder.Services.AddHostedService<Worker>();

try
{
    var host = builder.Build();
    host.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Hathor Worker terminated unexpectedly.");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}
