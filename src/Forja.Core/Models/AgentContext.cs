namespace Forja.Core.Models;

public class AgentContext
{
    public required Spec Spec { get; set; }
    public required string RepoPath { get; set; }
    public required string BranchName { get; set; }
    public StageResult? PlannerOutput { get; set; }
    public StageResult? CoderOutput { get; set; }
    public StageResult? TesterOutput { get; set; }
    public List<string> HealingFeedback { get; set; } = [];
    public int HealingAttempt { get; set; }
}
