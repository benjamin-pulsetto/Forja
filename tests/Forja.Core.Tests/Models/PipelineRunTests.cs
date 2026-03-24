using Forja.Core.Models;

namespace Forja.Core.Tests.Models;

public class PipelineRunTests
{
    [Fact]
    public void Id_Is8Characters()
    {
        var run = new PipelineRun();
        Assert.Equal(8, run.Id.Length);
    }

    [Fact]
    public void Defaults_AreCorrect()
    {
        var run = new PipelineRun();

        Assert.Equal(PipelineStatus.Pending, run.Status);
        Assert.NotNull(run.Spec);
        Assert.Equal(string.Empty, run.RepoPath);
        Assert.Equal(string.Empty, run.BranchName);
        Assert.Null(run.PlannerResult);
        Assert.Null(run.CoderResult);
        Assert.Null(run.TesterResult);
        Assert.Null(run.ReviewerResult);
        Assert.Equal(0, run.HealingAttempts);
        Assert.Null(run.CompletedAt);
        Assert.Null(run.Error);
    }

    [Fact]
    public void TwoRuns_HaveDifferentIds()
    {
        var run1 = new PipelineRun();
        var run2 = new PipelineRun();
        Assert.NotEqual(run1.Id, run2.Id);
    }
}
