using Forja.Core.Models;
using Forja.Core.Services;
using Microsoft.Extensions.Logging;

namespace Forja.Core.Agents;

public class PlannerAgent : IAgent
{
    private readonly IClaudeCliRunner _claude;
    private readonly GitService _git;
    private readonly ILogger<PlannerAgent> _logger;

    public string Name => "Planner";

    public PlannerAgent(IClaudeCliRunner claude, GitService git, ILogger<PlannerAgent> logger)
    {
        _claude = claude;
        _git = git;
        _logger = logger;
    }

    public virtual async Task<StageResult> ExecuteAsync(AgentContext context, CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        _logger.LogInformation("[Planner] Starting for spec: {SpecName}", context.Spec.Name);

        var fileTree = await _git.GetFileTreeAsync(context.RepoPath);
        var prompt = BuildPrompt(context.Spec, fileTree);

        var (success, output) = await _claude.RunAsync(prompt, context.RepoPath, ct);

        return new StageResult
        {
            Success = success,
            RawOutput = output,
            Summary = success ? ExtractSummary(output) : $"Planner failed: {output}",
            StartedAt = started,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    private static string BuildPrompt(Spec spec, string fileTree)
    {
        var requirements = string.Join("\n", spec.Requirements.Select(r => $"- {r}"));
        var constraints = string.Join("\n", spec.Constraints.Select(c => $"- {c}"));

        return $"""
            You are the PLANNER agent in a software factory pipeline. Your job is to analyze the spec and the codebase, then produce a detailed implementation plan. You do NOT write code — you only plan.

            ## Spec
            Name: {spec.Name}
            Type: {spec.Type}
            Description: {spec.Description}

            Requirements:
            {requirements}

            Target Project: {spec.Target.Project}
            Target Namespace: {spec.Target.Namespace}

            Constraints:
            {constraints}

            ## Codebase File Tree
            {fileTree}

            ## Your Task
            Produce a detailed implementation plan in markdown format. Include:
            1. **Files to create** — list each new file with its full path and purpose
            2. **Files to modify** — list each existing file and what changes are needed
            3. **Method signatures** — for each new class/method, specify the signature
            4. **Dependencies** — any NuGet packages needed
            5. **Testing strategy** — what test classes and test methods should be created, what each test verifies
            6. **Implementation order** — the sequence in which files should be created/modified

            Be specific. The Coder agent will follow your plan exactly. Do not be vague.
            Output ONLY the plan in markdown. No preamble.
            """;
    }

    private static string ExtractSummary(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length > 3
            ? string.Join("\n", lines.Take(5)) + "\n..."
            : output;
    }
}
