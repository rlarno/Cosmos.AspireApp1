using Microsoft.Azure.Cosmos;
using OpenTelemetry.Trace;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire components.
builder.AddServiceDefaults();
builder.AddAzureCosmosClient("cosmos"); // should set the DisableServerCertificateValidation
// https://github.com/Azure/azure-cosmos-dotnet-v3/blob/master/Microsoft.Azure.Cosmos/src/CosmosClientOptions.cs#L955
// AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==;AccountEndpoint=https://127.0.0.1:53558;DisableServerCertificateValidation=True;


// Add services to the container.
builder.Services.AddProblemDetails();

var app = builder.Build();

await app.CreateDatabaseAndContainer();

// Configure the HTTP request pipeline.
app.UseExceptionHandler();

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

// add ILogger to the WeatherForecast class
app.MapGet("/weatherforecast", async (ILogger<WeatherForecast> logger, CosmosClient client) =>
{

    using (var cosmosActivity = Telemetry.ActivitySource.StartActivity("CosmosDB Activity"))
    {   // Simulate some CosmosDB activity - Imagine getting the Forecasts per city from CosmosDB
        var container = client.GetContainer("Weather", "Cities");

        logger.LogInformation("Creating CosmosDB item");
        var item = await container.UpsertItemAsync(new { id = Guid.NewGuid(), city = "Seattle" });
        cosmosActivity?.SetTag("CosmosDB Item", item);
        cosmosActivity?.SetStatus(Status.Ok);
    }

    using Activity? activity = Telemetry.ActivitySource.StartActivity("WeatherForecast Activity");
    logger.LogInformation("Creating weather forecast");

    var forecast = Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();

    logger.LogInformation("Returning weather forecast");
    activity?.SetTag("WeatherForecast", forecast);
    activity?.SetStatus(Status.Ok);
    Telemetry.WeatherForecastCalls.Add(1, ("City", "Seattle"));
    return forecast;
});

app.MapDefaultEndpoints();

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}

    internal static class CosmosExtensions
    {
        private class CosmosDB { }
        internal static async Task CreateDatabaseAndContainer(this WebApplication app)
        {
            var logger = app.Services.GetRequiredService<ILogger<CosmosDB>>();
            logger.LogInformation("Creating CosmosDB database and container");
            var client = app.Services.GetRequiredService<CosmosClient>();
            while (true)
            {
                try
                {
                    await client.ReadAccountAsync();
                    break;
                }
                catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.SecureConnectionError)
                {
                    /* The CosmosDB emulator seems to take a very long time to start up, and returns this exception.
                     * System.Net.Http.HttpRequestException: The SSL connection could not be established, see inner exception.
                    ---> System.IO.IOException: Received an unexpected EOF or 0 bytes from the transport stream.
                     */
                    logger.LogWarning("CosmosDB connection retry");
                    Telemetry.CosmosDBConnectionRetries.Add(1);
                    await Task.Delay(1000);
                }
            }
            logger.LogInformation("CosmosDB connection success");
            using var activity = Telemetry.ActivitySource.StartActivity("CreateDatabaseAndContainer Activity");
            Database database = await client.CreateDatabaseIfNotExistsAsync("Weather");
            activity?.AddEvent(new ActivityEvent("CosmosDB Database Created"));
            Container container = await database.CreateContainerIfNotExistsAsync("Cities", "/city", 400);
            activity?.AddEvent(new ActivityEvent("CosmosDB Container Created"));
            logger.LogInformation("CosmosDB container created");
        }
    }

internal static class Telemetry
{
    internal static readonly AssemblyName AssemblyName = typeof(Telemetry).Assembly.GetName();

    internal static readonly string ActivitySourceName = AssemblyName.Name + '.' + nameof(WeatherForecast);
    internal static readonly Version? Version = AssemblyName.Version;
    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName, Version?.ToString());
    // https://learn.microsoft.com/en-us/dotnet/core/diagnostics/distributed-tracing-instrumentation-walkthroughs

    // TODO: convert to use the DI MeterFactory:
    // https://learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-instrumentation#get-a-meter-via-dependency-injection
    // https://learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-instrumentation#best-practices-4
    internal static readonly Meter Meter = new(ActivitySourceName, Version?.ToString());
    internal static readonly Counter<int> WeatherForecastCalls = Meter.CreateCounter<int>(ActivitySourceName.ToLower() + "calls");

    internal static readonly Counter<int> CosmosDBConnectionRetries = Meter.CreateCounter<int>(ActivitySourceName.ToLower() + "cosmosdbconnectionretries");

    public static void Add(this Counter<int> counter, int value, (string key, object? value) tag)
    {
        counter.Add(value, new KeyValuePair<string, object?>(tag.key, tag.value));
    }
}
