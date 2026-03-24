using System.Diagnostics;
using Forja.Core.Models;
using Microsoft.Extensions.Logging;

namespace Forja.Core.Services;

public class GitService
{
    private readonly GitConfig _config;
    private readonly ILogger<GitService> _logger;

    public GitService(GitConfig config, ILogger<GitService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public virtual async Task PrepareBaseBranchAsync(string repoPath)
    {
        var baseBranch = await DetectBaseBranchAsync(repoPath);

        try { await RunGitAsync(repoPath, "fetch", "--all"); }
        catch { _logger.LogWarning("fetch --all failed (no remote?), continuing..."); }

        await RunGitAsync(repoPath, "checkout", baseBranch);

        try { await RunGitAsync(repoPath, "pull", "origin", baseBranch); }
        catch { _logger.LogWarning("pull failed (no remote tracking?), continuing..."); }
    }

    public virtual async Task<string> DetectBaseBranchAsync(string repoPath)
    {
        // Try configured branch first
        if (await BranchExistsAsync(repoPath, _config.BaseBranch))
            return _config.BaseBranch;

        // Fallback: try common names
        foreach (var candidate in new[] { "main", "master", "develop" })
        {
            if (await BranchExistsAsync(repoPath, candidate))
            {
                _logger.LogInformation("Base branch '{Configured}' not found, using '{Detected}'",
                    _config.BaseBranch, candidate);
                return candidate;
            }
        }

        // Last resort: use current branch
        var current = (await RunGitAsync(repoPath, "branch", "--show-current")).Trim();
        if (!string.IsNullOrEmpty(current))
        {
            _logger.LogInformation("No standard base branch found, using current: '{Current}'", current);
            return current;
        }

        return _config.BaseBranch;
    }

    public virtual async Task<string> CreateBranchAsync(string repoPath, string branchName)
    {
        var candidate = branchName;
        var suffix = 2;
        while (await BranchExistsAsync(repoPath, candidate))
        {
            candidate = $"{branchName}-{suffix}";
            suffix++;
        }
        await RunGitAsync(repoPath, "checkout", "-b", candidate);
        return candidate;
    }

    public virtual async Task<bool> HasChangesAsync(string repoPath)
    {
        var result = await RunGitAsync(repoPath, "status", "--porcelain");
        return !string.IsNullOrWhiteSpace(result);
    }

    public virtual async Task<string> GetDiffAsync(string repoPath)
    {
        // Stage everything first so diff shows all changes
        await RunGitAsync(repoPath, "add", "-A");
        return await RunGitAsync(repoPath, "diff", "--cached");
    }

    public virtual async Task<string> GetFileTreeAsync(string repoPath)
    {
        return await RunGitAsync(repoPath, "ls-files");
    }

    public virtual async Task CommitChangesAsync(string repoPath, string commitMessage)
    {
        await RunGitAsync(repoPath, "add", "-A");
        await RunGitAsync(repoPath, "commit", "-m", commitMessage);
    }

    public virtual async Task PushBranchAsync(string repoPath, string branchName)
    {
        await RunGitAsync(repoPath, "push", "-u", "origin", branchName);
    }

    public virtual async Task CleanupOnFailureAsync(string repoPath)
    {
        try
        {
            var baseBranch = await DetectBaseBranchAsync(repoPath);
            await RunGitAsync(repoPath, "checkout", "--", ".");
            await RunGitAsync(repoPath, "clean", "-fd");
            await RunGitAsync(repoPath, "checkout", baseBranch);
        }
        catch
        {
            // Best effort
        }
    }

    public virtual async Task DeleteLocalBranchAsync(string repoPath, string branchName)
    {
        try
        {
            var baseBranch = await DetectBaseBranchAsync(repoPath);
            await RunGitAsync(repoPath, "checkout", baseBranch);
            await RunGitAsync(repoPath, "branch", "-D", branchName);
        }
        catch
        {
            // Best effort
        }
    }

    private async Task<bool> BranchExistsAsync(string repoPath, string branchName)
    {
        try
        {
            await RunGitAsync(repoPath, "rev-parse", "--verify", branchName);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string> RunGitAsync(string repoPath, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        _logger.LogDebug("git {Args} in {RepoPath}", string.Join(" ", args), repoPath);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start git {string.Join(" ", args)}");

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(" ", args)} failed (exit {process.ExitCode}):\n{stderr}");

        return stdout;
    }
}
