using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Forja.Core.Services;

public class DotnetTestRunner
{
    private readonly ILogger<DotnetTestRunner> _logger;

    public DotnetTestRunner(ILogger<DotnetTestRunner> logger)
    {
        _logger = logger;
    }

    public virtual async Task<(bool Success, string Output)> RunAsync(
        string repoPath, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("test");
        psi.ArgumentList.Add("--no-restore");
        psi.ArgumentList.Add("--verbosity");
        psi.ArgumentList.Add("normal");

        _logger.LogInformation("Running dotnet test in {RepoPath}", repoPath);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet test");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var combined = stdout + (string.IsNullOrWhiteSpace(stderr) ? "" : $"\n{stderr}");

        _logger.LogInformation("dotnet test exit code: {ExitCode}", process.ExitCode);

        return (process.ExitCode == 0, combined);
    }
}
