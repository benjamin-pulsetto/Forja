using System.Diagnostics;
using Forja.Core.Models;
using Microsoft.Extensions.Logging;

namespace Forja.Core.Services;

public class ClaudeCliRunner : IClaudeCliRunner
{
    private readonly ClaudeConfig _config;
    private readonly ILogger<ClaudeCliRunner> _logger;
    private readonly string? _resolvedCliJs;

    public ClaudeCliRunner(ClaudeConfig config, ILogger<ClaudeCliRunner> logger)
    {
        _config = config;
        _logger = logger;

        // On Windows, .cmd shims mangle arguments. Resolve to node + cli.js directly.
        if (_config.CliPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            && File.Exists(_config.CliPath))
        {
            var cliDir = Path.GetDirectoryName(_config.CliPath)!;
            var cliJs = Path.Combine(cliDir, "node_modules", "@anthropic-ai", "claude-code", "cli.js");
            if (File.Exists(cliJs))
            {
                _resolvedCliJs = cliJs;
                _logger.LogInformation("Resolved Claude CLI .cmd to node: {CliJs}", cliJs);
            }
        }
    }

    public async Task<(bool Success, string Output)> RunAsync(
        string prompt, string workingDirectory, CancellationToken ct = default)
    {
        if (!Directory.Exists(workingDirectory))
            return (false, $"Working directory does not exist: {workingDirectory}");

        var psi = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Remove all Claude env vars to prevent "nested session" detection
        var claudeKeys = psi.Environment.Keys
            .Where(k => k.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var key in claudeKeys)
            psi.Environment.Remove(key);

        if (_resolvedCliJs != null)
        {
            psi.FileName = "node";
            psi.ArgumentList.Add(_resolvedCliJs);
        }
        else
        {
            psi.FileName = _config.CliPath;
        }

        // Use -p with no argument — reads prompt from stdin to avoid Windows command line length limits
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("--dangerously-skip-permissions");

        if (!string.IsNullOrWhiteSpace(_config.AdditionalArgs))
        {
            foreach (var arg in _config.AdditionalArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                psi.ArgumentList.Add(arg);
        }

        _logger.LogInformation("Starting Claude CLI in {WorkingDirectory} (prompt: {PromptLength} chars)",
            workingDirectory, prompt.Length);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start Claude CLI");

        // Write prompt to stdin then close it
        await process.StandardInput.WriteAsync(prompt);
        process.StandardInput.Close();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(_config.TimeoutMinutes));

        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return (false, $"Claude CLI timed out after {_config.TimeoutMinutes} minutes");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (!string.IsNullOrWhiteSpace(stderr))
            _logger.LogWarning("[Claude CLI stderr] {Stderr}", stderr);

        if (process.ExitCode != 0)
            return (false, $"Claude CLI exited with code {process.ExitCode}:\n{stderr}");

        return (true, stdout);
    }
}
