namespace Forja.Core.Services;

public interface IClaudeCliRunner
{
    Task<(bool Success, string Output)> RunAsync(
        string prompt, string workingDirectory, CancellationToken ct = default);
}
