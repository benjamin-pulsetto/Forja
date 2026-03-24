using Forja.Core.Models;

namespace Forja.Core.Services;

public interface IPipelineNotifier
{
    Task NotifyStageStarted(string runId, string stageName);
    Task NotifyStageCompleted(string runId, string stageName, StageResult result);
    Task NotifyHealingRetry(string runId, int attempt, string reason);
    Task NotifyPipelineCompleted(string runId, bool success, string summary);
    Task NotifyError(string runId, string error);
    Task NotifyLog(string runId, string message);
}
