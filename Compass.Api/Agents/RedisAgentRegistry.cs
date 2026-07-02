using System.Text.Json;
using Compass.Api.TokenStore;
using StackExchange.Redis;

namespace Compass.Api.Agents;

public sealed class RedisAgentRegistry : IAgentRegistry
{
    private const string AgentPrefix = "compass:agent:";
    private const string ProxyPrefix = "compass:agent-token:";
    private const string AgentIdsKey = "compass:agents";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDatabase _database;

    public RedisAgentRegistry(IConnectionMultiplexer redis)
    {
        _database = redis.GetDatabase();
    }

    public async Task<AgentRecord?> ResolveByProxyTokenAsync(string proxyToken, CancellationToken cancellationToken = default)
    {
        var agentId = await _database.StringGetAsync(ProxyPrefix + ProxyTokenHasher.HashToken(proxyToken));
        return agentId.IsNullOrEmpty ? null : await GetAgentAsync(agentId!, cancellationToken);
    }

    public async Task<AgentRecord?> GetAgentAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var json = await _database.StringGetAsync(AgentPrefix + agentId);
        return json.IsNullOrEmpty ? null : JsonSerializer.Deserialize<AgentRecord>(json!, JsonOptions);
    }

    public async Task<IReadOnlyList<AgentRecord>> ListAgentsAsync(CancellationToken cancellationToken = default)
    {
        var ids = await _database.SetMembersAsync(AgentIdsKey);
        var agents = new List<AgentRecord>();
        foreach (var id in ids)
        {
            var agent = await GetAgentAsync(id!, cancellationToken);
            if (agent is not null)
            {
                agents.Add(agent);
            }
        }

        return agents.OrderBy(agent => agent.Name, StringComparer.Ordinal).ToList();
    }

    public async Task RegisterAgentAsync(
        string proxyToken,
        AgentRecord agent,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(agent, JsonOptions);
        await _database.StringSetAsync(AgentPrefix + agent.AgentId, json);
        await _database.StringSetAsync(ProxyPrefix + ProxyTokenHasher.HashToken(proxyToken), agent.AgentId);
        await _database.SetAddAsync(AgentIdsKey, agent.AgentId);
    }
}
