using Forja.Core.Models;

namespace Forja.Core.Agents;

public interface IAgent
{
    string Name { get; }
    Task<StageResult> ExecuteAsync(AgentContext context, CancellationToken ct = default);
}
