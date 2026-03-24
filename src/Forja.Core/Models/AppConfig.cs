namespace Forja.Core.Models;

public class AppConfig
{
    public ClaudeConfig Claude { get; set; } = new();
    public GitConfig Git { get; set; } = new();
    public PipelineConfig Pipeline { get; set; } = new();
}

public class ClaudeConfig
{
    public string CliPath { get; set; } = "claude";
    public string AdditionalArgs { get; set; } = string.Empty;
    public int TimeoutMinutes { get; set; } = 15;
}

public class GitConfig
{
    public string BaseBranch { get; set; } = "main";
    public string BranchPrefix { get; set; } = "forja";
    public bool AutoPush { get; set; } = true;
}

public class PipelineConfig
{
    public int MaxHealingAttempts { get; set; } = 3;
}
