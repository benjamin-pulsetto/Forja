namespace Forja.Core.Models;

public class PipelineRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public Spec Spec { get; set; } = new();
    public string RepoPath { get; set; } = string.Empty;
    public string BranchName { get; set; } = string.Empty;
    public PipelineStatus Status { get; set; } = PipelineStatus.Pending;
    public StageResult? PlannerResult { get; set; }
    public StageResult? CoderResult { get; set; }
    public StageResult? TesterResult { get; set; }
    public StageResult? ReviewerResult { get; set; }
    public int HealingAttempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
}

public enum PipelineStatus
{
    Pending,
    GeneratingSpec,
    Planning,
    Coding,
    Testing,
    Reviewing,
    Committing,
    Completed,
    Failed
}
