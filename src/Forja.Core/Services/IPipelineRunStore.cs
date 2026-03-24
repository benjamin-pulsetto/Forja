using Forja.Core.Models;

namespace Forja.Core.Services;

public interface IPipelineRunStore
{
    Task SaveAsync(PipelineRun run);
    Task<PipelineRun?> GetAsync(string runId);
    Task<IReadOnlyList<PipelineRun>> GetRecentAsync(int count = 20);
}
