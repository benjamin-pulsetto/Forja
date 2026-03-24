using Forja.Core.Models;
using Forja.Core.Services;
using Microsoft.Extensions.Logging;

namespace Forja.Core.Agents;

/// <summary>
/// The BLIND tester. It only sees the spec + git diff + dotnet test results.
/// It never sees the Planner's plan or the Coder's summary.
/// This prevents bias — it tests intent, not implementation.
/// </summary>
public class TesterAgent : IAgent
{
    private readonly IClaudeCliRunner _claude;
    private readonly GitService _git;
    private readonly DotnetTestRunner _testRunner;
    private readonly ILogger<TesterAgent> _logger;

    public string Name => "Tester";

    public TesterAgent(
        IClaudeCliRunner claude,
        GitService git,
        DotnetTestRunner testRunner,
        ILogger<TesterAgent> logger)
    {
        _claude = claude;
        _git = git;
        _testRunner = testRunner;
        _logger = logger;
    }

    public virtual async Task<StageResult> ExecuteAsync(AgentContext context, CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        _logger.LogInformation("[Tester] Starting blind testing for spec: {SpecName}", context.Spec.Name);

        // Step 1: Get the git diff (what actually changed)
        var diff = await _git.GetDiffAsync(context.RepoPath);

        // Step 2: Build the blind prompt — spec + diff only, NO planner/coder output
        var prompt = BuildBlindPrompt(context.Spec, diff);

        // Step 3: Let Claude write and run tests
        var (claudeSuccess, claudeOutput) = await _claude.RunAsync(prompt, context.RepoPath, ct);

        if (!claudeSuccess)
        {
            return new StageResult
            {
                Success = false,
                RawOutput = claudeOutput,
                Summary = $"Tester agent failed to execute: {claudeOutput}",
                StartedAt = started,
                CompletedAt = DateTimeOffset.UtcNow
            };
        }

        // Step 4: Run dotnet test to get objective results
        var (testsPass, testOutput) = await _testRunner.RunAsync(context.RepoPath, ct);

        var combinedOutput = $"""
            === TESTER AGENT ANALYSIS ===
            {claudeOutput}

            === DOTNET TEST RESULTS ===
            Tests passed: {testsPass}
            {testOutput}
            """;

        return new StageResult
        {
            Success = testsPass,
            RawOutput = combinedOutput,
            Summary = testsPass
                ? "All tests pass"
                : ExtractFailureSummary(testOutput, claudeOutput),
            StartedAt = started,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    private static string BuildBlindPrompt(Spec spec, string diff)
    {
        var requirements = string.Join("\n", spec.Requirements.Select(r => $"- {r}"));

        return $"""
            You are the TESTER agent in a software factory pipeline. You are BLIND — you only know the spec and can see the code changes (git diff). You have NOT seen the implementation plan or the coder's reasoning.

            Your job: write tests that verify the SPEC REQUIREMENTS are met, then evaluate whether the implementation is correct.

            ## Spec (what was requested)
            Name: {spec.Name}
            Type: {spec.Type}
            Description: {spec.Description}

            Requirements:
            {requirements}

            Target Project: {spec.Target.Project}
            Target Namespace: {spec.Target.Namespace}

            ## Code Changes (git diff)
            {diff}

            ## Instructions
            1. Read the spec requirements carefully — these are your source of truth
            2. Read the git diff to understand what was implemented
            3. Write test classes that verify EACH requirement from the spec
            4. Use xUnit for test framework
            5. Place tests in the appropriate test project
            6. Tests should verify behavior from the spec, not implementation details
            7. Run `dotnet build` to make sure everything compiles
            8. After writing tests, provide a summary: which requirements are covered and any concerns

            IMPORTANT: Test the REQUIREMENTS, not the code structure. If the spec says "negative amounts should throw ArgumentException", write a test that passes a negative amount and expects ArgumentException — regardless of how the code is structured.
            """;
    }

    private static string ExtractFailureSummary(string testOutput, string claudeOutput)
    {
        var lines = testOutput.Split('\n');
        var failures = lines.Where(l =>
            l.Contains("Failed", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("Error", StringComparison.OrdinalIgnoreCase))
            .Take(10);

        var summary = string.Join("\n", failures);
        return string.IsNullOrWhiteSpace(summary)
            ? "Tests failed — see full output for details"
            : summary;
    }
}
