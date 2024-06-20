var builder = DistributedApplication.CreateBuilder(args);

var cosmosDb = builder.AddAzureCosmosDB("cosmos")
    .RunAsEmulator(c => c
        .WithEnvironment("AZURE_COSMOS_EMULATOR_PARTITION_COUNT", "2")); // attempt to start faster? - it no work
// https://localhost:8081/_explorer/index.html // => change to the emulator target port
    // https://github.com/dotnet/aspire/issues/4199


var apiService = builder.AddProject<Projects.AspireApp1_ApiService>("apiservice")
    .WithReference(cosmosDb);

builder.AddProject<Projects.AspireApp1_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithReference(apiService);

builder.Build().Run();
