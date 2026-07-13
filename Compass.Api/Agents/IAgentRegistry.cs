namespace Compass.Api.Agents;

public interface IAgentRegistry
{
    Task<AgentRecord?> ResolveByProxyTokenAsync(string proxyToken, CancellationToken cancellationToken = default);

    Task<AgentRecord?> GetAgentAsync(string agentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentRecord>> ListAgentsAsync(CancellationToken cancellationToken = default);

    Task RegisterAgentAsync(
        string proxyToken,
        AgentRecord agent,
        CancellationToken cancellationToken = default);

    Task SuspendAgentAsync(string agentId, CancellationToken cancellationToken = default);
}
