using Forja.Core.Agents;
using Forja.Core.Models;
using Forja.Core.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Forja.Core.Tests.Agents;

public class PlannerAgentTests
{
    private readonly IClaudeCliRunner _claude = Substitute.For<IClaudeCliRunner>();
    private readonly GitService _git;
    private readonly PlannerAgent _sut;

    public PlannerAgentTests()
    {
        var gitConfig = new GitConfig();
        var gitLogger = Substitute.For<ILogger<GitService>>();
        _git = Substitute.ForPartsOf<GitService>(gitConfig, gitLogger);

        _git.When(g => g.GetFileTreeAsync(Arg.Any<string>())).DoNotCallBase();
        _git.GetFileTreeAsync(Arg.Any<string>()).Returns("src/Program.cs\nsrc/Models/Foo.cs");

        _sut = new PlannerAgent(_claude, _git, Substitute.For<ILogger<PlannerAgent>>());
    }

    [Fact]
    public void Name_IsPlanner()
    {
        Assert.Equal("Planner", _sut.Name);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsSuccess_WhenClaudeSucceeds()
    {
        _claude.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((true, "# Implementation Plan\n\n## Files to create\n- src/NewFile.cs"));

        var context = new AgentContext
        {
            Spec = new Spec { Name = "test", Requirements = ["Req 1"], Constraints = ["C1"] },
            RepoPath = "/fake",
            BranchName = "forja/test"
        };

        var result = await _sut.ExecuteAsync(context);

        Assert.True(result.Success);
        Assert.Contains("Implementation Plan", result.RawOutput);
        Assert.True(result.Duration > TimeSpan.Zero);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFailure_WhenClaudeFails()
    {
        _claude.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((false, "CLI timeout"));

        var context = new AgentContext
        {
            Spec = new Spec { Name = "test", Requirements = [], Constraints = [] },
            RepoPath = "/fake",
            BranchName = "forja/test"
        };

        var result = await _sut.ExecuteAsync(context);

        Assert.False(result.Success);
        Assert.Contains("Planner failed", result.Summary);
    }

    [Fact]
    public async Task ExecuteAsync_IncludesFileTreeInPrompt()
    {
        _claude.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((true, "plan output"));

        var context = new AgentContext
        {
            Spec = new Spec { Name = "test", Requirements = [], Constraints = [] },
            RepoPath = "/fake",
            BranchName = "forja/test"
        };

        await _sut.ExecuteAsync(context);

        await _claude.Received(1).RunAsync(
            Arg.Is<string>(p => p.Contains("src/Program.cs") && p.Contains("PLANNER")),
            "/fake",
            Arg.Any<CancellationToken>());
    }
}
