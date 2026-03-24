using Forja.Core.Agents;
using Forja.Core.Models;
using Forja.Core.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Forja.Core.Tests.Services;

public class PipelineOrchestratorTests
{
    private readonly PlannerAgent _planner;
    private readonly CoderAgent _coder;
    private readonly TesterAgent _tester;
    private readonly ReviewerAgent _reviewer;
    private readonly GitService _git;
    private readonly IPipelineNotifier _notifier;
    private readonly PipelineOrchestrator _sut;

    private readonly Spec _spec = new()
    {
        Name = "test-feature",
        Type = "feature",
        Description = "A test feature",
        Requirements = ["Req 1"],
        Target = new SpecTarget { Project = "src/Test", Namespace = "Test" }
    };

    public PipelineOrchestratorTests()
    {
        var claude = Substitute.For<IClaudeCliRunner>();
        var gitConfig = new GitConfig { AutoPush = false };
        var gitLogger = Substitute.For<ILogger<GitService>>();

        // Use Substitute.For since all methods are virtual now
        _git = Substitute.For<GitService>(gitConfig, gitLogger);

        _planner = Substitute.For<PlannerAgent>(
            claude, _git, Substitute.For<ILogger<PlannerAgent>>());
        _coder = Substitute.For<CoderAgent>(
            claude, Substitute.For<ILogger<CoderAgent>>());

        var testRunner = Substitute.For<DotnetTestRunner>(Substitute.For<ILogger<DotnetTestRunner>>());
        _tester = Substitute.For<TesterAgent>(
            claude, _git, testRunner, Substitute.For<ILogger<TesterAgent>>());
        _reviewer = Substitute.For<ReviewerAgent>(
            claude, _git, Substitute.For<ILogger<ReviewerAgent>>());

        _notifier = Substitute.For<IPipelineNotifier>();
        var config = new PipelineConfig { MaxHealingAttempts = 2 };
        var logger = Substitute.For<ILogger<PipelineOrchestrator>>();

        _sut = new PipelineOrchestrator(
            _planner, _coder, _tester, _reviewer,
            _git, config, gitConfig, _notifier, logger);
    }

    private StageResult SuccessResult(string summary = "OK") => new()
    {
        Success = true,
        RawOutput = "raw",
        Summary = summary,
        StartedAt = DateTimeOffset.UtcNow,
        CompletedAt = DateTimeOffset.UtcNow.AddSeconds(1)
    };

    private StageResult FailResult(string summary = "Failed") => new()
    {
        Success = false,
        RawOutput = "error output",
        Summary = summary,
        StartedAt = DateTimeOffset.UtcNow,
        CompletedAt = DateTimeOffset.UtcNow.AddSeconds(1)
    };

    private void SetupGitForSuccess()
    {
        _git.PrepareBaseBranchAsync(Arg.Any<string>()).Returns(Task.CompletedTask);
        _git.CreateBranchAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => Task.FromResult((string)ci[1]));
        _git.HasChangesAsync(Arg.Any<string>()).Returns(true);
        _git.CommitChangesAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.CompletedTask);
        _git.CleanupOnFailureAsync(Arg.Any<string>()).Returns(Task.CompletedTask);
        _git.DeleteLocalBranchAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task FullPipeline_Success_WhenAllStagesPass()
    {
        SetupGitForSuccess();
        _planner.ExecuteAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(SuccessResult("Plan ready"));
        _coder.ExecuteAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(SuccessResult("Code done"));
        _tester.ExecuteAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(SuccessResult("Tests pass"));
        _reviewer.ExecuteAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(SuccessResult("APPROVED"));

        var run = await _sut.ExecuteAsync(_spec, "/fake/repo");

        Assert.Equal(PipelineStatus.Completed, run.Status);
        Assert.Null(run.Error);
        Assert.NotNull(run.PlannerResult);
        Assert.NotNull(run.CoderResult);
        Assert.NotNull(run.TesterResult);
        Assert.NotNull(run.ReviewerResult);
        Assert.NotNull(run.CompletedAt);
    }

    [Fact]
    public async Task Pipeline_Fails_WhenPlannerFails()
    {
        SetupGitForSuccess();
        _planner.ExecuteAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(FailResult("Planner failed"));

        var run = await _sut.ExecuteAsync(_spec, "/fake/repo");

        Assert.Equal(PipelineStatus.Failed, run.Status);
        Assert.Equal("Planner failed", run.Error);
        await _coder.DidNotReceive().ExecuteAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Pipeline_Fails_WhenCoderFails()
    {
        SetupGitForSuccess();
        _planner.ExecuteAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(SuccessResult());
        _coder.ExecuteAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(FailResult("Coder failed"));

        var run = await _sut.ExecuteAsync(_spec, "/fake/repo");

        Assert.Equal(PipelineStatus.Failed, run.Status);
        Assert.Equal("Coder failed", run.Error);
    }

    [Fact]
    public async Task Pipeline_HealsOnce_WhenFirstTestFails()
    {
        SetupGitForSuccess();
        _planner.ExecuteAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(SuccessResult());
        _coder.ExecuteAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(SuccessResult());
        _tester.ExecuteAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(
                FailResult("Test failed"),
                SuccessResult("Tests pass")
            );
        _reviewer.ExecuteAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(SuccessResult("APPROVED"));

        var run = await _sut.ExecuteAsync(_spec, "/fake/repo");

        Assert.Equal(PipelineStatus.Completed, run.Status);
        Assert.Equal(1, run.HealingAttempts);
        await _notifier.Received(1).NotifyHealingRetry(Arg.Any<string>(), 1, Arg.Any<string>());
    }

    [Fact]
    public async Task Pipeline_FailsAfterMaxHealingAttempts()
    {
        SetupGitForSuccess();
        _planner.ExecuteAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(SuccessResult());
        _coder.ExecuteAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(SuccessResult());
        _tester.ExecuteAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(FailResult("Still failing"));
        _reviewer.ExecuteAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(SuccessResult());

        var run = await _sut.ExecuteAsync(_spec, "/fake/repo");

        Assert.Equal(PipelineStatus.Failed, run.Status);
        Assert.Contains("Tests failed after", run.Error);
        await _reviewer.DidNotReceive().ExecuteAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Pipeline_Fails_WhenNoChangesProduced()
    {
        SetupGitForSuccess();
        _git.HasChangesAsync(Arg.Any<string>()).Returns(false);

        _planner.ExecuteAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(SuccessResult());
        _coder.ExecuteAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(SuccessResult());
        _tester.ExecuteAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(SuccessResult());
        _reviewer.ExecuteAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(SuccessResult());

        var run = await _sut.ExecuteAsync(_spec, "/fake/repo");

        Assert.Equal(PipelineStatus.Failed, run.Status);
        Assert.Equal("No file changes produced", run.Error);
    }

    [Fact]
    public async Task Pipeline_NotifiesStageStartAndComplete()
    {
        SetupGitForSuccess();
        _planner.ExecuteAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(SuccessResult());
        _coder.ExecuteAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(SuccessResult());
        _tester.ExecuteAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(SuccessResult());
        _reviewer.ExecuteAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(SuccessResult());

        await _sut.ExecuteAsync(_spec, "/fake/repo");

        await _notifier.Received(4).NotifyStageStarted(Arg.Any<string>(), Arg.Any<string>());
        await _notifier.Received(4).NotifyStageCompleted(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<StageResult>());
        await _notifier.Received(1).NotifyPipelineCompleted(Arg.Any<string>(), true, Arg.Any<string>());
    }

    [Fact]
    public async Task Pipeline_CleansUpOnFailure()
    {
        SetupGitForSuccess();
        _planner.ExecuteAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(FailResult());

        await _sut.ExecuteAsync(_spec, "/fake/repo");

        await _git.Received(1).CleanupOnFailureAsync(Arg.Any<string>());
        await _git.Received(1).DeleteLocalBranchAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Pipeline_CatchesExceptions_AndFails()
    {
        SetupGitForSuccess();
        _planner.ExecuteAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Throws(new Exception("Unexpected error"));

        var run = await _sut.ExecuteAsync(_spec, "/fake/repo");

        Assert.Equal(PipelineStatus.Failed, run.Status);
        Assert.Equal("Unexpected error", run.Error);
        await _notifier.Received(1).NotifyError(Arg.Any<string>(), "Unexpected error");
    }

    [Fact]
    public async Task Pipeline_GeneratesBranchName_FromSpec()
    {
        SetupGitForSuccess();
        _planner.ExecuteAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(FailResult());

        await _sut.ExecuteAsync(_spec, "/fake/repo");

        await _git.Received(1).CreateBranchAsync(Arg.Any<string>(), "forja/test-feature");
    }

    [Fact]
    public async Task Pipeline_DoesNotPush_WhenAutoPushDisabled()
    {
        SetupGitForSuccess();
        _planner.ExecuteAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(SuccessResult());
        _coder.ExecuteAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(SuccessResult());
        _tester.ExecuteAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(SuccessResult());
        _reviewer.ExecuteAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(SuccessResult());

        var run = await _sut.ExecuteAsync(_spec, "/fake/repo");

        Assert.Equal(PipelineStatus.Completed, run.Status);
        await _git.DidNotReceive().PushBranchAsync(Arg.Any<string>(), Arg.Any<string>());
    }
}

public class PipelineOrchestratorAutoPushTests
{
    [Fact]
    public async Task Pipeline_Pushes_WhenAutoPushEnabled()
    {
        var claude = Substitute.For<IClaudeCliRunner>();
        var gitConfig = new GitConfig { AutoPush = true };
        var git = Substitute.For<GitService>(gitConfig, Substitute.For<ILogger<GitService>>());
        git.PrepareBaseBranchAsync(Arg.Any<string>()).Returns(Task.CompletedTask);
        git.CreateBranchAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => Task.FromResult((string)ci[1]));
        git.HasChangesAsync(Arg.Any<string>()).Returns(true);
        git.CommitChangesAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.CompletedTask);
        git.PushBranchAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.CompletedTask);

        var planner = Substitute.For<PlannerAgent>(claude, git, Substitute.For<ILogger<PlannerAgent>>());
        var coder = Substitute.For<CoderAgent>(claude, Substitute.For<ILogger<CoderAgent>>());
        var testRunner = Substitute.For<DotnetTestRunner>(Substitute.For<ILogger<DotnetTestRunner>>());
        var tester = Substitute.For<TesterAgent>(claude, git, testRunner, Substitute.For<ILogger<TesterAgent>>());
        var reviewer = Substitute.For<ReviewerAgent>(claude, git, Substitute.For<ILogger<ReviewerAgent>>());
        var notifier = Substitute.For<IPipelineNotifier>();

        var successResult = new StageResult
        {
            Success = true, RawOutput = "ok", Summary = "OK",
            StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow.AddSeconds(1)
        };

        planner.ExecuteAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>()).Returns(successResult);
        coder.ExecuteAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>()).Returns(successResult);
        tester.ExecuteAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>()).Returns(successResult);
        reviewer.ExecuteAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>()).Returns(successResult);

        var sut = new PipelineOrchestrator(
            planner, coder, tester, reviewer,
            git, new PipelineConfig(), gitConfig, notifier,
            Substitute.For<ILogger<PipelineOrchestrator>>());

        var spec = new Spec
        {
            Name = "push-test",
            Type = "feature",
            Description = "Test push",
            Requirements = ["Req"],
            Target = new SpecTarget { Project = "src", Namespace = "Test" }
        };

        var run = await sut.ExecuteAsync(spec, "/fake/repo");

        Assert.Equal(PipelineStatus.Completed, run.Status);
        await git.Received(1).PushBranchAsync("/fake/repo", Arg.Any<string>());
    }
}
