using System.Text.Json;
using Compass.Api.TokenStore;

namespace Compass.Api.Agents;

public sealed class FileAgentRegistry : IAgentRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public FileAgentRegistry(IWebHostEnvironment environment, IConfiguration configuration)
    {
        _filePath = configuration["Agents:FilePath"]
            ?? Path.Combine(environment.ContentRootPath, "data", "agents.json");
    }

    public async Task<AgentRecord?> ResolveByProxyTokenAsync(string proxyToken, CancellationToken cancellationToken = default)
    {
        var store = await ReadStoreAsync(cancellationToken);
        return store.ProxyTokenToAgentId.TryGetValue(ProxyTokenHasher.HashToken(proxyToken), out var agentId) &&
               store.Agents.TryGetValue(agentId, out var agent)
            ? agent
            : null;
    }

    public async Task<AgentRecord?> GetAgentAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var store = await ReadStoreAsync(cancellationToken);
        return store.Agents.GetValueOrDefault(agentId);
    }

    public async Task<IReadOnlyList<AgentRecord>> ListAgentsAsync(CancellationToken cancellationToken = default)
    {
        var store = await ReadStoreAsync(cancellationToken);
        return store.Agents.Values.OrderBy(agent => agent.Name, StringComparer.Ordinal).ToList();
    }

    public async Task RegisterAgentAsync(
        string proxyToken,
        AgentRecord agent,
        CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await ReadStoreCoreAsync(cancellationToken);
            store.Agents[agent.AgentId] = agent;
            store.ProxyTokenToAgentId[ProxyTokenHasher.HashToken(proxyToken)] = agent.AgentId;
            await WriteStoreCoreAsync(store, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task SuspendAgentAsync(string agentId, CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await ReadStoreCoreAsync(cancellationToken);
            if (!store.Agents.TryGetValue(agentId, out var agent))
            {
                return;
            }

            store.Agents[agentId] = agent with { Revoked = true };
            await WriteStoreCoreAsync(store, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task<AgentRegistryStore> ReadStoreAsync(CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            return await ReadStoreCoreAsync(cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task<AgentRegistryStore> ReadStoreCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return new AgentRegistryStore();
        }

        var json = await File.ReadAllTextAsync(_filePath, cancellationToken);
        return JsonSerializer.Deserialize<AgentRegistryStore>(json, JsonOptions) ?? new AgentRegistryStore();
    }

    private async Task WriteStoreCoreAsync(AgentRegistryStore store, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath) ?? ".");
        var json = JsonSerializer.Serialize(store, JsonOptions);
        await File.WriteAllTextAsync(_filePath, json, cancellationToken);
    }

    private sealed class AgentRegistryStore
    {
        public Dictionary<string, AgentRecord> Agents { get; init; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> ProxyTokenToAgentId { get; init; } = new(StringComparer.Ordinal);
    }
}
