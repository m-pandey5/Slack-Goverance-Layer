var builder = DistributedApplication.CreateBuilder(args);

var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));

var compassApi = builder.AddExecutable(
    "compass-api",
    "/opt/homebrew/opt/dotnet@8/bin/dotnet",
    repositoryRoot,
    "run",
    "--project",
    "Compass.Api/Compass.Api.csproj",
    "--launch-profile",
    "http");

compassApi.WithUrl("http://localhost:5000", "API");
compassApi.WithUrl("http://localhost:5000/healthz", "Health");

builder.Build().Run();
