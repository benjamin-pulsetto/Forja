using Forja.Core.Agents;
using Forja.Core.Models;
using Forja.Core.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Forja.Core.Tests.Agents;

public class ReviewerAgentTests
{
    private readonly IClaudeCliRunner _claude = Substitute.For<IClaudeCliRunner>();
    private readonly GitService _git;
    private readonly ReviewerAgent _sut;

    public ReviewerAgentTests()
    {
        var gitConfig = new GitConfig();
        var gitLogger = Substitute.For<ILogger<GitService>>();
        _git = Substitute.ForPartsOf<GitService>(gitConfig, gitLogger);

        _git.When(g => g.GetDiffAsync(Arg.Any<string>())).DoNotCallBase();
        _git.GetDiffAsync(Arg.Any<string>()).Returns("+added line\n-removed line");

        _sut = new ReviewerAgent(_claude, _git, Substitute.For<ILogger<ReviewerAgent>>());
    }

    [Fact]
    public void Name_IsReviewer()
    {
        Assert.Equal("Reviewer", _sut.Name);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsApproved_WhenOutputContainsApprove()
    {
        _claude.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((true, "**Verdict**: APPROVE\nLooks good!"));

        var context = new AgentContext
        {
            Spec = new Spec { Name = "test", Requirements = ["Req"], Constraints = [] },
            RepoPath = "/fake",
            BranchName = "forja/test",
            PlannerOutput = new StageResult { RawOutput = "plan" },
            TesterOutput = new StageResult { Summary = "All tests pass" }
        };

        var result = await _sut.ExecuteAsync(context);

        Assert.True(result.Success);
        Assert.Equal("APPROVED", result.Summary);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsConcerns_WhenOutputContainsConcerns()
    {
        _claude.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((true, "**Verdict**: CONCERNS\nSecurity issue found"));

        var context = new AgentContext
        {
            Spec = new Spec { Name = "test", Requirements = [], Constraints = [] },
            RepoPath = "/fake",
            BranchName = "forja/test"
        };

        var result = await _sut.ExecuteAsync(context);

        Assert.True(result.Success);
        Assert.Contains("CONCERNS", result.Summary);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFailure_WhenClaudeFails()
    {
        _claude.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((false, "CLI error"));

        var context = new AgentContext
        {
            Spec = new Spec { Name = "test", Requirements = [], Constraints = [] },
            RepoPath = "/fake",
            BranchName = "forja/test"
        };

        var result = await _sut.ExecuteAsync(context);

        Assert.False(result.Success);
        Assert.Contains("Reviewer failed", result.Summary);
    }
}
