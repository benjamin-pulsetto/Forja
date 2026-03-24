using System.Text.RegularExpressions;
using Forja.Core.Agents;
using Forja.Core.Models;
using Microsoft.Extensions.Logging;

namespace Forja.Core.Services;

public class PipelineOrchestrator
{
    private readonly PlannerAgent _planner;
    private readonly CoderAgent _coder;
    private readonly TesterAgent _tester;
    private readonly ReviewerAgent _reviewer;
    private readonly GitService _git;
    private readonly PipelineConfig _config;
    private readonly GitConfig _gitConfig;
    private readonly IPipelineNotifier _notifier;
    private readonly ILogger<PipelineOrchestrator> _logger;

    public PipelineOrchestrator(
        PlannerAgent planner,
        CoderAgent coder,
        TesterAgent tester,
        ReviewerAgent reviewer,
        GitService git,
        PipelineConfig config,
        GitConfig gitConfig,
        IPipelineNotifier notifier,
        ILogger<PipelineOrchestrator> logger)
    {
        _planner = planner;
        _coder = coder;
        _tester = tester;
        _reviewer = reviewer;
        _git = git;
        _config = config;
        _gitConfig = gitConfig;
        _notifier = notifier;
        _logger = logger;
    }

    public async Task<PipelineRun> ExecuteAsync(Spec spec, string repoPath, CancellationToken ct = default)
        => await ExecuteAsync(spec, repoPath, null, ct);

    public async Task<PipelineRun> ExecuteAsync(Spec spec, string repoPath, string? runId, CancellationToken ct = default)
    {
        var run = new PipelineRun
        {
            Spec = spec,
            RepoPath = repoPath
        };
        if (runId != null) run.Id = runId;

        var context = new AgentContext
        {
            Spec = spec,
            RepoPath = repoPath,
            BranchName = string.Empty
        };

        try
        {
            // --- Prepare git branch ---
            await Log(run.Id, $"Pipeline started for spec: {spec.Name}");
            await Log(run.Id, $"Repository: {repoPath}");

            var branchName = GenerateBranchName(spec);
            await Log(run.Id, $"Fetching latest from base branch...");
            await _git.PrepareBaseBranchAsync(repoPath);

            await Log(run.Id, $"Creating branch: {branchName}");
            branchName = await _git.CreateBranchAsync(repoPath, branchName);
            run.BranchName = branchName;
            context.BranchName = branchName;
            await Log(run.Id, $"Working on branch: {branchName}");

            // --- Stage 1: PLANNER ---
            run.Status = PipelineStatus.Planning;
            await Log(run.Id, "--- STAGE 1: PLANNER ---");
            await Log(run.Id, "Analyzing codebase and creating implementation plan...");
            await _notifier.NotifyStageStarted(run.Id, _planner.Name);

            var plannerResult = await _planner.ExecuteAsync(context, ct);
            run.PlannerResult = plannerResult;
            context.PlannerOutput = plannerResult;
            await Log(run.Id, plannerResult.Success
                ? $"Planner completed ({plannerResult.Duration.TotalSeconds:F1}s)"
                : $"Planner FAILED: {plannerResult.Summary}");
            await LogOutput(run.Id, "PLANNER", plannerResult);
            await _notifier.NotifyStageCompleted(run.Id, _planner.Name, plannerResult);

            if (!plannerResult.Success)
            {
                run.Status = PipelineStatus.Failed;
                run.Error = "Planner failed";
                await CleanupOnFailure(run);
                await _notifier.NotifyPipelineCompleted(run.Id, false, run.Error);
                return run;
            }

            // --- Stage 2 & 3: CODER + TESTER with healing loop ---
            StageResult? coderResult = null;
            StageResult? testerResult = null;

            for (var attempt = 0; attempt <= _config.MaxHealingAttempts; attempt++)
            {
                context.HealingAttempt = attempt;

                // --- CODER ---
                run.Status = PipelineStatus.Coding;
                if (attempt == 0)
                    await Log(run.Id, "--- STAGE 2: CODER ---");
                else
                    await Log(run.Id, $"--- CODER (healing attempt {attempt}/{_config.MaxHealingAttempts}) ---");

                await Log(run.Id, "Implementing code changes via Claude CLI...");
                await _notifier.NotifyStageStarted(run.Id, _coder.Name);

                coderResult = await _coder.ExecuteAsync(context, ct);
                run.CoderResult = coderResult;
                context.CoderOutput = coderResult;
                await Log(run.Id, coderResult.Success
                    ? $"Coder completed ({coderResult.Duration.TotalSeconds:F1}s)"
                    : $"Coder FAILED: {coderResult.Summary}");
                await LogOutput(run.Id, "CODER", coderResult);
                await _notifier.NotifyStageCompleted(run.Id, _coder.Name, coderResult);

                if (!coderResult.Success)
                {
                    run.Status = PipelineStatus.Failed;
                    run.Error = "Coder failed";
                    await CleanupOnFailure(run);
                    await _notifier.NotifyPipelineCompleted(run.Id, false, run.Error);
                    return run;
                }

                // --- TESTER (blind) ---
                run.Status = PipelineStatus.Testing;
                await Log(run.Id, "--- STAGE 3: TESTER (blind) ---");
                await Log(run.Id, "Writing tests from spec (blind — no access to planner/coder output)...");
                await _notifier.NotifyStageStarted(run.Id, _tester.Name);

                testerResult = await _tester.ExecuteAsync(context, ct);
                run.TesterResult = testerResult;
                context.TesterOutput = testerResult;
                await Log(run.Id, testerResult.Success
                    ? $"Tests PASSED ({testerResult.Duration.TotalSeconds:F1}s)"
                    : $"Tests FAILED: {testerResult.Summary}");
                await LogOutput(run.Id, "TESTER", testerResult);
                await _notifier.NotifyStageCompleted(run.Id, _tester.Name, testerResult);

                if (testerResult.Success)
                {
                    _logger.LogInformation("Tests passed on attempt {Attempt}", attempt + 1);
                    break;
                }

                // Tests failed — prepare healing feedback
                if (attempt < _config.MaxHealingAttempts)
                {
                    run.HealingAttempts = attempt + 1;
                    context.HealingFeedback.Add(testerResult.RawOutput);
                    await Log(run.Id, $"Self-healing: sending test failures back to Coder (attempt {attempt + 1}/{_config.MaxHealingAttempts})...");
                    await _notifier.NotifyHealingRetry(run.Id, attempt + 1, testerResult.Summary);
                }
            }

            // If tests still fail after all retries
            if (testerResult is not { Success: true })
            {
                run.Status = PipelineStatus.Failed;
                run.Error = $"Tests failed after {_config.MaxHealingAttempts + 1} attempts";
                await Log(run.Id, $"PIPELINE FAILED: {run.Error}");
                await CleanupOnFailure(run);
                await _notifier.NotifyPipelineCompleted(run.Id, false, run.Error);
                return run;
            }

            // --- Stage 4: REVIEWER ---
            run.Status = PipelineStatus.Reviewing;
            await Log(run.Id, "--- STAGE 4: REVIEWER ---");
            await Log(run.Id, "Reviewing code quality, security, and spec compliance...");
            await _notifier.NotifyStageStarted(run.Id, _reviewer.Name);

            var reviewerResult = await _reviewer.ExecuteAsync(context, ct);
            run.ReviewerResult = reviewerResult;
            await Log(run.Id, $"Review completed: {reviewerResult.Summary}");
            await LogOutput(run.Id, "REVIEWER", reviewerResult);
            await _notifier.NotifyStageCompleted(run.Id, _reviewer.Name, reviewerResult);

            // --- COMMIT ---
            run.Status = PipelineStatus.Committing;
            await Log(run.Id, "--- COMMITTING ---");

            if (!await _git.HasChangesAsync(repoPath))
            {
                run.Status = PipelineStatus.Failed;
                run.Error = "No file changes produced";
                await Log(run.Id, $"PIPELINE FAILED: {run.Error}");
                await CleanupOnFailure(run);
                await _notifier.NotifyPipelineCompleted(run.Id, false, run.Error);
                return run;
            }

            var commitMessage = $"[Forja] {spec.Name}: {spec.Description.Split('\n')[0].Trim()}";
            await Log(run.Id, $"Committing: {commitMessage}");
            await _git.CommitChangesAsync(repoPath, commitMessage);

            // --- Auto-push if configured ---
            if (_gitConfig.AutoPush)
            {
                try
                {
                    await Log(run.Id, $"Pushing branch {run.BranchName} to origin...");
                    await _git.PushBranchAsync(repoPath, run.BranchName);
                    await Log(run.Id, "Push completed");
                }
                catch (Exception pushEx)
                {
                    await Log(run.Id, $"Push failed (non-fatal): {pushEx.Message}");
                    _logger.LogWarning(pushEx, "Auto-push failed, but code is committed locally");
                }
            }

            run.Status = PipelineStatus.Completed;
            run.CompletedAt = DateTimeOffset.UtcNow;

            var summary = $"Completed in {run.HealingAttempts} healing attempt(s). " +
                          $"Review: {reviewerResult.Summary}. " +
                          $"Branch: {run.BranchName}";

            await Log(run.Id, $"PIPELINE COMPLETED: {summary}");
            await _notifier.NotifyPipelineCompleted(run.Id, true, summary);

            return run;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pipeline failed with exception");
            run.Status = PipelineStatus.Failed;
            run.Error = ex.Message;
            await Log(run.Id, $"PIPELINE ERROR: {ex.Message}");
            await CleanupOnFailure(run);
            await _notifier.NotifyError(run.Id, ex.Message);
            return run;
        }
    }

    private async Task Log(string runId, string message)
    {
        _logger.LogInformation("[Pipeline {RunId}] {Message}", runId, message);
        await _notifier.NotifyLog(runId, message);
    }

    private async Task LogOutput(string runId, string stageName, StageResult result)
    {
        if (string.IsNullOrWhiteSpace(result.RawOutput)) return;

        await Log(runId, $"--- {stageName} OUTPUT ---");
        // Send output in chunks to avoid SignalR message size limits
        var lines = result.RawOutput.Split('\n');
        const int batchSize = 20;
        for (var i = 0; i < lines.Length; i += batchSize)
        {
            var chunk = string.Join("\n", lines.Skip(i).Take(batchSize));
            await _notifier.NotifyLog(runId, chunk);
        }
        await Log(runId, $"--- END {stageName} OUTPUT ---");
    }

    private async Task CleanupOnFailure(PipelineRun run)
    {
        await Log(run.Id, "Cleaning up failed run...");
        run.CompletedAt = DateTimeOffset.UtcNow;
        if (!string.IsNullOrEmpty(run.BranchName))
        {
            await _git.CleanupOnFailureAsync(run.RepoPath);
            await _git.DeleteLocalBranchAsync(run.RepoPath, run.BranchName);
        }
    }

    private static string GenerateBranchName(Spec spec)
    {
        var slug = Regex.Replace(spec.Name.ToLowerInvariant(), @"[^a-z0-9\s-]", "");
        slug = Regex.Replace(slug, @"\s+", "-");
        slug = Regex.Replace(slug, @"-+", "-").Trim('-');
        if (slug.Length > 40) slug = slug[..40].TrimEnd('-');
        return $"forja/{slug}";
    }
}
