using Forja.Core.Models;
using Forja.Core.Services;
using Forja.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Forja.Web.Services;

public class SignalRPipelineNotifier : IPipelineNotifier
{
    private readonly IHubContext<PipelineHub> _hub;

    public SignalRPipelineNotifier(IHubContext<PipelineHub> hub)
    {
        _hub = hub;
    }

    public async Task NotifyStageStarted(string runId, string stageName)
    {
        await _hub.Clients.Group(runId).SendAsync("StageStarted", stageName);
    }

    public async Task NotifyStageCompleted(string runId, string stageName, StageResult result)
    {
        await _hub.Clients.Group(runId).SendAsync("StageCompleted", stageName, new
        {
            result.Success,
            result.Summary,
            result.RawOutput,
            Duration = result.Duration.TotalSeconds
        });
    }

    public async Task NotifyHealingRetry(string runId, int attempt, string reason)
    {
        await _hub.Clients.Group(runId).SendAsync("HealingRetry", attempt, reason);
    }

    public async Task NotifyPipelineCompleted(string runId, bool success, string summary)
    {
        await _hub.Clients.Group(runId).SendAsync("PipelineCompleted", success, summary);
    }

    public async Task NotifyError(string runId, string error)
    {
        await _hub.Clients.Group(runId).SendAsync("PipelineError", error);
    }

    public async Task NotifyLog(string runId, string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        await _hub.Clients.Group(runId).SendAsync("Log", $"[{timestamp}] {message}");
    }
}
