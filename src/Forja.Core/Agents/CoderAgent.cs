using Forja.Core.Models;
using Forja.Core.Services;
using Microsoft.Extensions.Logging;

namespace Forja.Core.Agents;

public class CoderAgent : IAgent
{
    private readonly IClaudeCliRunner _claude;
    private readonly ILogger<CoderAgent> _logger;

    public string Name => "Coder";

    public CoderAgent(IClaudeCliRunner claude, ILogger<CoderAgent> logger)
    {
        _claude = claude;
        _logger = logger;
    }

    public virtual async Task<StageResult> ExecuteAsync(AgentContext context, CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        _logger.LogInformation("[Coder] Starting (healing attempt: {Attempt})", context.HealingAttempt);

        var prompt = context.HealingAttempt == 0
            ? BuildInitialPrompt(context)
            : BuildHealingPrompt(context);

        var (success, output) = await _claude.RunAsync(prompt, context.RepoPath, ct);

        return new StageResult
        {
            Success = success,
            RawOutput = output,
            Summary = success ? "Code changes applied" : $"Coder failed: {output}",
            StartedAt = started,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    private static string BuildInitialPrompt(AgentContext context)
    {
        var requirements = string.Join("\n", context.Spec.Requirements.Select(r => $"- {r}"));
        var constraints = string.Join("\n", context.Spec.Constraints.Select(c => $"- {c}"));

        return $"""
            You are the CODER agent in a software factory pipeline. Your job is to implement code changes by directly editing files in this repository. Use your tools (Edit, Write, Bash) to modify the codebase.

            ## Spec
            Name: {context.Spec.Name}
            Type: {context.Spec.Type}
            Description: {context.Spec.Description}

            Requirements:
            {requirements}

            Target Project: {context.Spec.Target.Project}
            Target Namespace: {context.Spec.Target.Namespace}

            Constraints:
            {constraints}

            ## Implementation Plan (from Planner)
            {context.PlannerOutput?.RawOutput}

            ## Instructions
            - Follow the implementation plan above exactly
            - Use Edit and Write tools to create/modify files
            - Follow existing code style and conventions in the repo
            - Make sure the code compiles (run `dotnet build` to verify)
            - Do NOT write tests — a separate Tester agent handles that
            - After making all changes, provide a brief summary of what you changed
            """;
    }

    private static string BuildHealingPrompt(AgentContext context)
    {
        var feedback = string.Join("\n---\n", context.HealingFeedback);

        return $"""
            You are the CODER agent in a software factory pipeline. Your previous implementation FAILED testing. You must fix the issues.

            ## Original Spec
            Name: {context.Spec.Name}
            Type: {context.Spec.Type}
            Description: {context.Spec.Description}

            Requirements:
            {string.Join("\n", context.Spec.Requirements.Select(r => $"- {r}"))}

            ## Implementation Plan (from Planner)
            {context.PlannerOutput?.RawOutput}

            ## Test Failures (attempt {context.HealingAttempt})
            The following feedback was received from the Tester. Fix these issues:

            {feedback}

            ## Instructions
            - Read the failing test output carefully
            - Use Edit and Write tools to fix the issues in the source code
            - Make sure the code compiles (run `dotnet build` to verify)
            - Do NOT modify tests — only fix the implementation
            - After making changes, provide a brief summary of what you fixed
            """;
    }
}
