using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

var telemetryBuilder = builder.Services.AddOpenTelemetry()
    .UseFunctionsWorkerDefaults();

var monitorConnectionString =
    Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING")
    ?? Environment.GetEnvironmentVariable("AzureMonitorConnectionString");

if (!string.IsNullOrWhiteSpace(monitorConnectionString))
{
    telemetryBuilder.UseAzureMonitorExporter(options =>
    {
        options.ConnectionString = monitorConnectionString;
    });
}

builder.Build().Run();
