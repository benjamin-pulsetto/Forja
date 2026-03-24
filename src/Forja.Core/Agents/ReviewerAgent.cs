using Forja.Core.Models;
using Forja.Core.Services;
using Microsoft.Extensions.Logging;

namespace Forja.Core.Agents;

public class ReviewerAgent : IAgent
{
    private readonly IClaudeCliRunner _claude;
    private readonly GitService _git;
    private readonly ILogger<ReviewerAgent> _logger;

    public string Name => "Reviewer";

    public ReviewerAgent(IClaudeCliRunner claude, GitService git, ILogger<ReviewerAgent> logger)
    {
        _claude = claude;
        _git = git;
        _logger = logger;
    }

    public virtual async Task<StageResult> ExecuteAsync(AgentContext context, CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        _logger.LogInformation("[Reviewer] Starting review for spec: {SpecName}", context.Spec.Name);

        var diff = await _git.GetDiffAsync(context.RepoPath);
        var prompt = BuildPrompt(context, diff);

        // Reviewer runs in read-only mode (-p only, no tools needed)
        var (success, output) = await _claude.RunAsync(prompt, context.RepoPath, ct);

        return new StageResult
        {
            Success = success,
            RawOutput = output,
            Summary = success ? ExtractVerdict(output) : $"Reviewer failed: {output}",
            StartedAt = started,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    private static string BuildPrompt(AgentContext context, string diff)
    {
        var requirements = string.Join("\n", context.Spec.Requirements.Select(r => $"- {r}"));

        return $"""
            You are the REVIEWER agent in a software factory pipeline. You review code changes AFTER they have passed tests. Your review is informational — it does not block the commit, but it provides feedback.

            ## Spec
            Name: {context.Spec.Name}
            Type: {context.Spec.Type}
            Description: {context.Spec.Description}

            Requirements:
            {requirements}

            ## Implementation Plan (from Planner)
            {context.PlannerOutput?.RawOutput}

            ## Test Results
            {context.TesterOutput?.Summary}

            ## Code Changes (git diff)
            {diff}

            ## Review Checklist
            Evaluate the code changes against these criteria:
            1. **Spec compliance** — Do the changes fulfill all requirements?
            2. **Code quality** — Is the code clean, readable, and following conventions?
            3. **Security** — Any injection, XSS, or other OWASP top 10 issues?
            4. **Edge cases** — Are boundary conditions handled?
            5. **Performance** — Any obvious performance concerns?
            6. **Naming** — Are names clear and consistent with the codebase?

            ## Output Format
            Provide your review as:
            - **Verdict**: APPROVE or CONCERNS
            - **Summary**: 2-3 sentence overview
            - **Issues**: List any concerns (if CONCERNS verdict)
            - **Suggestions**: Optional improvements (non-blocking)
            """;
    }

    private static string ExtractVerdict(string output)
    {
        if (output.Contains("APPROVE", StringComparison.OrdinalIgnoreCase))
            return "APPROVED";
        if (output.Contains("CONCERNS", StringComparison.OrdinalIgnoreCase))
            return "CONCERNS RAISED — see review output";
        return "Review completed";
    }
}
