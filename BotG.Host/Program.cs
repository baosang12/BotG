using BotG.Config;
using BotG.Host;
using BotG.Runtime.Preprocessor;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using System.Linq;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

var configFileName = "config.runtime.json";
var potentialConfigPaths = new[]
{
    Path.Combine(AppContext.BaseDirectory, configFileName),
    Path.Combine(builder.Environment.ContentRootPath, configFileName),
    Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", configFileName)),
    Path.Combine("D:/botg/config", configFileName)
};

var configPath = potentialConfigPaths.FirstOrDefault(File.Exists);
if (configPath is null)
{
    throw new FileNotFoundException($"Không tìm thấy {configFileName} trong các đường dẫn: {string.Join(", ", potentialConfigPaths)}");
}

builder.Configuration.AddJsonFile(configPath, optional: false, reloadOnChange: true);
builder.Services.Configure<PreprocessorRuntimeConfig>(builder.Configuration.GetSection("Preprocessor"));

builder.Services.AddSingleton<PreprocessorRuntimeManager>();
builder.Services.AddSingleton<PreprocessorRolloutManager>();
builder.Services.AddHostedService<PreprocessorBackgroundService>();
builder.Services.AddHealthChecks()
    .AddCheck<PreprocessorHealthCheck>(
        "preprocessor",
        failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded,
        tags: new[] { "preprocessor", "trading", "core" });

builder.Services.AddHealthChecksUI(setup =>
    {
        setup.SetHeaderText("BotG - Preprocessor Health Dashboard");
        setup.AddHealthCheckEndpoint("Preprocessor", "/health/preprocessor");
        setup.SetEvaluationTimeInSeconds(30);
        setup.SetMinimumSecondsBetweenFailureNotifications(60);
    })
    .AddInMemoryStorage();

var app = builder.Build();

app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = _ => true,
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
});

app.MapHealthChecks("/health/preprocessor", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("preprocessor"),
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
});

app.MapHealthChecksUI(config =>
{
    config.UIPath = "/health-ui";
});

app.MapGet("/preprocessor/status", (PreprocessorRuntimeManager manager, PreprocessorRolloutManager rollout) =>
{
    var status = manager.GetStatus();
    var snapshot = manager.LatestSnapshot;
    return Results.Ok(new
    {
        status,
        snapshot,
        rollout = rollout.GetState()
    });
});

app.MapPost("/preprocessor/start", (PreprocessorRuntimeManager manager, IOptionsMonitor<PreprocessorRuntimeConfig> config) =>
{
    var started = manager.TryStart(config.CurrentValue);
    if (!started)
    {
        return Results.Problem("Không thể khởi động preprocessor. Kiểm tra config runtime", statusCode: 500);
    }

    return Results.Ok(new
    {
        message = "Preprocessor started",
        time = DateTime.UtcNow,
        status = manager.GetStatus()
    });
});

app.MapPost("/preprocessor/stop", (PreprocessorRuntimeManager manager) =>
{
    manager.Stop();
    return Results.Ok(new
    {
        message = "Preprocessor stopped",
        time = DateTime.UtcNow,
        status = manager.GetStatus()
    });
});

app.MapGet("/", (PreprocessorRolloutManager rollout) => new
{
    message = "BotG Preprocessor Host API",
    version = "1.0.0",
    rollout = rollout.GetState(),
    endpoints = new[]
    {
        "/health",
        "/health/preprocessor",
        "/health-ui",
        "/preprocessor/status",
        "/preprocessor/start",
        "/preprocessor/stop"
    }
});

app.Run("http://localhost:5050");
