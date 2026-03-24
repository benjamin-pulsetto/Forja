using Forja.Core.Models;
using Forja.Core.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Forja.Core.Tests.Integration;

public class GitServiceIntegrationTests : IDisposable
{
    private readonly string _repoPath;
    private readonly GitService _sut;

    public GitServiceIntegrationTests()
    {
        _repoPath = Path.Combine(Path.GetTempPath(), $"forja-git-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoPath);

        var config = new GitConfig { BaseBranch = "main", BranchPrefix = "forja" };
        _sut = new GitService(config, Substitute.For<ILogger<GitService>>());

        // Initialize a git repo with an initial commit
        RunGit("init");
        RunGit("config", "user.email", "test@forja.dev");
        RunGit("config", "user.name", "Forja Test");
        // Create main branch with initial commit
        RunGit("checkout", "-b", "main");
        File.WriteAllText(Path.Combine(_repoPath, "README.md"), "# Test Repo");
        RunGit("add", "-A");
        RunGit("commit", "-m", "Initial commit");
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoPath, true); } catch { }
    }

    private void RunGit(params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var proc = System.Diagnostics.Process.Start(psi)!;
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            var stderr = proc.StandardError.ReadToEnd();
            throw new Exception($"git {string.Join(" ", args)} failed: {stderr}");
        }
    }

    private string GetCurrentBranch()
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _repoPath,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("branch");
        psi.ArgumentList.Add("--show-current");
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd().Trim();
        proc.WaitForExit();
        return output;
    }

    [Fact]
    public async Task CreateBranchAsync_CreatesNewBranch()
    {
        var branchName = await _sut.CreateBranchAsync(_repoPath, "forja/test-branch");

        Assert.Equal("forja/test-branch", branchName);
        Assert.Equal("forja/test-branch", GetCurrentBranch());
    }

    [Fact]
    public async Task CreateBranchAsync_DeduplicatesBranchNames()
    {
        // Create the first branch, then go back to main
        await _sut.CreateBranchAsync(_repoPath, "forja/dupe");
        RunGit("checkout", "main");

        // Creating same branch again should get a suffix
        var branch2 = await _sut.CreateBranchAsync(_repoPath, "forja/dupe");

        Assert.Equal("forja/dupe-2", branch2);
    }

    [Fact]
    public async Task HasChangesAsync_ReturnsFalse_WhenClean()
    {
        var hasChanges = await _sut.HasChangesAsync(_repoPath);
        Assert.False(hasChanges);
    }

    [Fact]
    public async Task HasChangesAsync_ReturnsTrue_WhenFilesModified()
    {
        File.WriteAllText(Path.Combine(_repoPath, "new-file.txt"), "hello");

        var hasChanges = await _sut.HasChangesAsync(_repoPath);
        Assert.True(hasChanges);
    }

    [Fact]
    public async Task GetFileTreeAsync_ReturnsTrackedFiles()
    {
        var tree = await _sut.GetFileTreeAsync(_repoPath);

        Assert.Contains("README.md", tree);
    }

    [Fact]
    public async Task GetDiffAsync_ReturnsDiff_WhenChangesExist()
    {
        File.WriteAllText(Path.Combine(_repoPath, "new-file.txt"), "content");

        var diff = await _sut.GetDiffAsync(_repoPath);

        Assert.Contains("new-file.txt", diff);
        Assert.Contains("+content", diff);
    }

    [Fact]
    public async Task CommitChangesAsync_CreatesCommit()
    {
        await _sut.CreateBranchAsync(_repoPath, "forja/commit-test");
        File.WriteAllText(Path.Combine(_repoPath, "committed.txt"), "data");

        await _sut.CommitChangesAsync(_repoPath, "[Forja] test commit");

        // Verify the commit exists
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _repoPath,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("log");
        psi.ArgumentList.Add("-1");
        psi.ArgumentList.Add("--oneline");
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();

        Assert.Contains("[Forja] test commit", output);
    }

    [Fact]
    public async Task CleanupOnFailureAsync_ResetsWorkingTree()
    {
        // Create a dirty file
        File.WriteAllText(Path.Combine(_repoPath, "dirty.txt"), "should be cleaned");

        await _sut.CleanupOnFailureAsync(_repoPath);

        Assert.False(File.Exists(Path.Combine(_repoPath, "dirty.txt")));
        Assert.Equal("main", GetCurrentBranch());
    }

    [Fact]
    public async Task DeleteLocalBranchAsync_RemovesBranch()
    {
        await _sut.CreateBranchAsync(_repoPath, "forja/to-delete");
        // Go back to main first (DeleteLocalBranchAsync does this internally)

        await _sut.DeleteLocalBranchAsync(_repoPath, "forja/to-delete");

        // Verify branch doesn't exist
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("branch");
        psi.ArgumentList.Add("--list");
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();

        Assert.DoesNotContain("forja/to-delete", output);
    }

    [Fact]
    public async Task FullWorkflow_CreateBranch_MakeChanges_Commit()
    {
        // Simulate a mini pipeline workflow
        var branch = await _sut.CreateBranchAsync(_repoPath, "forja/full-workflow");
        Assert.Equal("forja/full-workflow", branch);

        // Make some changes
        File.WriteAllText(Path.Combine(_repoPath, "feature.cs"), "public class Feature {}");

        Assert.True(await _sut.HasChangesAsync(_repoPath));

        var diff = await _sut.GetDiffAsync(_repoPath);
        Assert.Contains("feature.cs", diff);

        await _sut.CommitChangesAsync(_repoPath, "[Forja] add feature");
        Assert.False(await _sut.HasChangesAsync(_repoPath));
    }
}
