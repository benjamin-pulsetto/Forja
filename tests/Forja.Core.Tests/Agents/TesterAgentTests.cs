using Forja.Core.Agents;
using Forja.Core.Models;
using Forja.Core.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Forja.Core.Tests.Agents;

public class TesterAgentTests
{
    private readonly IClaudeCliRunner _claude = Substitute.For<IClaudeCliRunner>();
    private readonly GitService _git;
    private readonly DotnetTestRunner _testRunner;
    private readonly TesterAgent _sut;

    public TesterAgentTests()
    {
        var gitConfig = new GitConfig();
        _git = Substitute.For<GitService>(gitConfig, Substitute.For<ILogger<GitService>>());
        _git.GetDiffAsync(Arg.Any<string>()).Returns("+new code\n-old code");

        _testRunner = Substitute.For<DotnetTestRunner>(Substitute.For<ILogger<DotnetTestRunner>>());

        _sut = new TesterAgent(_claude, _git, _testRunner, Substitute.For<ILogger<TesterAgent>>());
    }

    private AgentContext MakeContext() => new()
    {
        Spec = new Spec
        {
            Name = "test-feature",
            Type = "feature",
            Description = "A test feature",
            Requirements = ["Req 1", "Req 2"],
            Target = new SpecTarget { Project = "src/App", Namespace = "App" },
            Constraints = []
        },
        RepoPath = "/fake/repo",
        BranchName = "forja/test-feature"
    };

    [Fact]
    public void Name_IsTester()
    {
        Assert.Equal("Tester", _sut.Name);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsSuccess_WhenTestsPass()
    {
        _claude.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((true, "Wrote 3 test methods covering all requirements"));
        _testRunner.RunAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((true, "Passed! 3 tests passed, 0 failed"));

        var result = await _sut.ExecuteAsync(MakeContext());

        Assert.True(result.Success);
        Assert.Equal("All tests pass", result.Summary);
        Assert.Contains("TESTER AGENT ANALYSIS", result.RawOutput);
        Assert.Contains("DOTNET TEST RESULTS", result.RawOutput);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFail_WhenTestsFail()
    {
        _claude.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((true, "Wrote tests"));
        _testRunner.RunAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((false, "Failed! TestMethod1 - Error: expected 42 got 0\n1 test passed, 1 failed"));

        var result = await _sut.ExecuteAsync(MakeContext());

        Assert.False(result.Success);
        Assert.Contains("Failed", result.Summary);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFail_WhenClaudeFails()
    {
        _claude.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((false, "Claude CLI timed out"));

        var result = await _sut.ExecuteAsync(MakeContext());

        Assert.False(result.Success);
        Assert.Contains("Tester agent failed", result.Summary);
        // Test runner should not be called if Claude fails
        await _testRunner.DidNotReceive().RunAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_SendsBlindPrompt_WithoutPlannerOutput()
    {
        _claude.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((true, "Tests written"));
        _testRunner.RunAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((true, "All pass"));

        var context = MakeContext();
        context.PlannerOutput = new StageResult { RawOutput = "SECRET PLAN DETAILS" };
        context.CoderOutput = new StageResult { RawOutput = "SECRET CODER DETAILS" };

        await _sut.ExecuteAsync(context);

        // Verify the prompt does NOT contain planner or coder output
        await _claude.Received(1).RunAsync(
            Arg.Is<string>(p =>
                p.Contains("BLIND") &&
                p.Contains("Req 1") &&
                !p.Contains("SECRET PLAN DETAILS") &&
                !p.Contains("SECRET CODER DETAILS")),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_IncludesGitDiffInPrompt()
    {
        _claude.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((true, "Tests written"));
        _testRunner.RunAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((true, "All pass"));

        await _sut.ExecuteAsync(MakeContext());

        await _claude.Received(1).RunAsync(
            Arg.Is<string>(p => p.Contains("+new code") && p.Contains("-old code")),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RecordsDuration()
    {
        _claude.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((true, "ok"));
        _testRunner.RunAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((true, "ok"));

        var result = await _sut.ExecuteAsync(MakeContext());

        Assert.True(result.CompletedAt >= result.StartedAt);
        Assert.True(result.Duration >= TimeSpan.Zero);
    }
}
