using System.Text.Json;
using Forja.Core.Models;
using Forja.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace Forja.Web.Endpoints;

public static class PipelineEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static void MapPipelineEndpoints(this WebApplication app)
    {
        app.MapPost("/api/pipeline/start", async (HttpContext ctx,
            [FromServices] PipelineOrchestrator orchestrator,
            [FromServices] SpecGenerator specGenerator,
            [FromServices] IPipelineRunStore store,
            [FromServices] IPipelineNotifier notifier) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            var request = JsonSerializer.Deserialize<PipelineStartRequest>(body, JsonOpts);

            if (request is null)
                return Results.BadRequest(new { error = "Invalid request body" });

            if (string.IsNullOrWhiteSpace(request.RepoPath))
                return Results.BadRequest(new { error = "Repository path is required" });

            if (!Directory.Exists(request.RepoPath))
                return Results.BadRequest(new { error = $"Repository path does not exist: {request.RepoPath}", code = "REPO_NOT_FOUND" });

            // Quick validation: need description or yaml
            if (string.IsNullOrWhiteSpace(request.Yaml) && string.IsNullOrWhiteSpace(request.Description))
                return Results.BadRequest(new { error = "Either 'description' or 'yaml' must be provided" });

            // Return immediately with runId — spec generation + pipeline run in background
            var runId = Guid.NewGuid().ToString("N")[..8];
            var run = new PipelineRun
            {
                Id = runId,
                RepoPath = request.RepoPath,
                Status = PipelineStatus.GeneratingSpec
            };
            await store.SaveAsync(run);

            _ = Task.Run(async () =>
            {
                try
                {
                    // Give the client time to join the SignalR group
                    await Task.Delay(1500);

                    Spec spec;
                    if (!string.IsNullOrWhiteSpace(request.Yaml))
                    {
                        spec = specGenerator.FromYaml(request.Yaml);
                    }
                    else
                    {
                        await notifier.NotifyLog(runId, "Generating spec from description via Claude CLI...");
                        spec = await specGenerator.FromNaturalLanguageAsync(
                            request.Description!, request.RepoPath);
                        await notifier.NotifyLog(runId, $"Spec generated: {spec.Name} ({spec.Type})");
                        await notifier.NotifyLog(runId, $"Requirements: {spec.Requirements.Count}, Constraints: {spec.Constraints.Count}");
                    }

                    run.Spec = spec;
                    run.Status = PipelineStatus.Pending;
                    await store.SaveAsync(run);

                    var result = await orchestrator.ExecuteAsync(spec, request.RepoPath, runId);
                    await store.SaveAsync(result);
                }
                catch (Exception ex)
                {
                    run.Status = PipelineStatus.Failed;
                    run.Error = ex.Message;
                    run.CompletedAt = DateTimeOffset.UtcNow;
                    await store.SaveAsync(run);
                    await notifier.NotifyError(runId, ex.Message);
                }
            });

            return Results.Ok(new { runId, description = request.Description });
        });

        app.MapGet("/api/pipeline/{runId}", async (string runId,
            [FromServices] IPipelineRunStore store) =>
        {
            var run = await store.GetAsync(runId);
            return run is not null ? Results.Ok(run) : Results.NotFound();
        });

        app.MapGet("/api/pipeline/history", async ([FromServices] IPipelineRunStore store) =>
        {
            var runs = await store.GetRecentAsync(20);
            return Results.Ok(runs);
        });

        app.MapPost("/api/spec/generate", async (HttpContext ctx,
            [FromServices] SpecGenerator specGenerator) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            var request = JsonSerializer.Deserialize<SpecGenerateRequest>(body, JsonOpts);

            if (request is null)
                return Results.BadRequest(new { error = "Invalid request body" });

            if (string.IsNullOrWhiteSpace(request.RepoPath) || !Directory.Exists(request.RepoPath))
                return Results.BadRequest(new { error = $"Repository path does not exist: {request.RepoPath}", code = "REPO_NOT_FOUND" });

            try
            {
                var spec = await specGenerator.FromNaturalLanguageAsync(
                    request.Description, request.RepoPath);
                var yaml = specGenerator.ToYaml(spec);
                return Results.Ok(new { spec, yaml });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/repo/init", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            var request = JsonSerializer.Deserialize<RepoInitRequest>(body, JsonOpts);

            if (request is null || string.IsNullOrWhiteSpace(request.RepoPath))
                return Results.BadRequest(new { error = "Repository path is required" });

            try
            {
                Directory.CreateDirectory(request.RepoPath);

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = request.RepoPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("init");
                using var process = System.Diagnostics.Process.Start(psi);
                process?.WaitForExit();

                var commitPsi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = request.RepoPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                commitPsi.ArgumentList.Add("commit");
                commitPsi.ArgumentList.Add("--allow-empty");
                commitPsi.ArgumentList.Add("-m");
                commitPsi.ArgumentList.Add("Initial commit");
                using var commitProcess = System.Diagnostics.Process.Start(commitPsi);
                commitProcess?.WaitForExit();

                return Results.Ok(new { message = $"Repository initialized at {request.RepoPath}" });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}

public class RepoInitRequest
{
    public string RepoPath { get; set; } = string.Empty;
}

public class PipelineStartRequest
{
    public string? Description { get; set; }
    public string? Yaml { get; set; }
    public string RepoPath { get; set; } = string.Empty;
}

public class SpecGenerateRequest
{
    public string Description { get; set; } = string.Empty;
    public string RepoPath { get; set; } = string.Empty;
}
