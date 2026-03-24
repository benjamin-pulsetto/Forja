using Microsoft.AspNetCore.SignalR;

namespace Forja.Web.Hubs;

public class PipelineHub : Hub
{
    public async Task JoinRun(string runId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, runId);
    }

    public async Task LeaveRun(string runId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, runId);
    }
}
