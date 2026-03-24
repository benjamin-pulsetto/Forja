using Forja.Core.Agents;
using Forja.Core.Models;
using Forja.Core.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Forja.Core.Tests.Agents;

public class CoderAgentTests
{
    private readonly IClaudeCliRunner _claude = Substitute.For<IClaudeCliRunner>();
    private readonly CoderAgent _sut;

    public CoderAgentTests()
    {
        _sut = new CoderAgent(_claude, Substitute.For<ILogger<CoderAgent>>());
    }

    [Fact]
    public void Name_IsCoder()
    {
        Assert.Equal("Coder", _sut.Name);
    }

    [Fact]
    public async Task ExecuteAsync_InitialAttempt_UsesInitialPrompt()
    {
        _claude.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((true, "Changes applied"));

        var context = new AgentContext
        {
            Spec = new Spec { Name = "test", Requirements = ["Req 1"], Constraints = ["C1"] },
            RepoPath = "/fake",
            BranchName = "forja/test",
            PlannerOutput = new StageResult { RawOutput = "The plan" },
            HealingAttempt = 0
        };

        var result = await _sut.ExecuteAsync(context);

        Assert.True(result.Success);
        Assert.Equal("Code changes applied", result.Summary);
        await _claude.Received(1).RunAsync(
            Arg.Is<string>(p => p.Contains("CODER") && p.Contains("Implementation Plan") && !p.Contains("FAILED testing")),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_HealingAttempt_UsesHealingPrompt()
    {
        _claude.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((true, "Fixed the issue"));

        var context = new AgentContext
        {
            Spec = new Spec { Name = "test", Requirements = ["Req 1"], Constraints = [] },
            RepoPath = "/fake",
            BranchName = "forja/test",
            PlannerOutput = new StageResult { RawOutput = "The plan" },
            HealingAttempt = 1,
            HealingFeedback = ["Test XYZ failed: expected 42 got 0"]
        };

        var result = await _sut.ExecuteAsync(context);

        Assert.True(result.Success);
        await _claude.Received(1).RunAsync(
            Arg.Is<string>(p => p.Contains("FAILED testing") && p.Contains("Test XYZ failed")),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFailure_WhenClaudeFails()
    {
        _claude.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((false, "Timeout"));

        var context = new AgentContext
        {
            Spec = new Spec { Name = "test", Requirements = [], Constraints = [] },
            RepoPath = "/fake",
            BranchName = "forja/test",
            HealingAttempt = 0
        };

        var result = await _sut.ExecuteAsync(context);

        Assert.False(result.Success);
        Assert.Contains("Coder failed", result.Summary);
    }
}
