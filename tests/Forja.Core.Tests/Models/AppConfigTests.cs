using Forja.Core.Models;

namespace Forja.Core.Tests.Models;

public class AppConfigTests
{
    [Fact]
    public void ClaudeConfig_Defaults()
    {
        var config = new ClaudeConfig();

        Assert.Equal("claude", config.CliPath);
        Assert.Equal(string.Empty, config.AdditionalArgs);
        Assert.Equal(15, config.TimeoutMinutes);
    }

    [Fact]
    public void GitConfig_Defaults()
    {
        var config = new GitConfig();

        Assert.Equal("main", config.BaseBranch);
        Assert.Equal("forja", config.BranchPrefix);
        Assert.True(config.AutoPush);
    }

    [Fact]
    public void PipelineConfig_Defaults()
    {
        var config = new PipelineConfig();

        Assert.Equal(3, config.MaxHealingAttempts);
    }
}
