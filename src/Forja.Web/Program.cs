using Forja.Core.Agents;
using Forja.Core.Models;
using Forja.Core.Services;
using Forja.Web.Endpoints;
using Forja.Web.Hubs;
using Forja.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// --- JSON serialization ---
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

// Disable .NET 10 preview strict validation
builder.Services.AddProblemDetails();

// --- Configuration ---
var appConfig = new AppConfig();
builder.Configuration.GetSection("Forja").Bind(appConfig);

builder.Services.AddSingleton(appConfig.Claude);
builder.Services.AddSingleton(appConfig.Git);
builder.Services.AddSingleton(appConfig.Pipeline);

// --- Core Services ---
builder.Services.AddSingleton<IClaudeCliRunner, ClaudeCliRunner>();
builder.Services.AddSingleton<GitService>();
builder.Services.AddSingleton<DotnetTestRunner>();
builder.Services.AddSingleton<SpecGenerator>();

// --- Persistence ---
var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
builder.Services.AddSingleton<IPipelineRunStore>(sp =>
    new JsonPipelineRunStore(
        Path.Combine(dataDir, "pipeline-runs.json"),
        sp.GetRequiredService<ILogger<JsonPipelineRunStore>>()));

// --- Agents ---
builder.Services.AddTransient<PlannerAgent>();
builder.Services.AddTransient<CoderAgent>();
builder.Services.AddTransient<TesterAgent>();
builder.Services.AddTransient<ReviewerAgent>();

// --- Pipeline ---
builder.Services.AddTransient<PipelineOrchestrator>();

// --- SignalR ---
builder.Services.AddSignalR();
builder.Services.AddSingleton<IPipelineNotifier, SignalRPipelineNotifier>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHub<PipelineHub>("/hubs/pipeline");
app.MapPipelineEndpoints();

// Debug endpoint
app.MapPost("/api/debug", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    return Results.Ok(new { received = body, length = body.Length });
});

Console.WriteLine();
Console.WriteLine("  ╔═══════════════════════════════════╗");
Console.WriteLine("  ║         FORJA - Code Factory      ║");
Console.WriteLine("  ╚═══════════════════════════════════╝");
Console.WriteLine();
Console.WriteLine($"  UI:     http://localhost:5200");
Console.WriteLine($"  Claude: {appConfig.Claude.CliPath}");
Console.WriteLine($"  Branch: {appConfig.Git.BaseBranch}");
Console.WriteLine($"  Heals:  {appConfig.Pipeline.MaxHealingAttempts} max retries");
Console.WriteLine();

app.Run();
