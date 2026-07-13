namespace Compass.Api.Agents;

public sealed class AgentRegistrySeeder : IHostedService
{
    private readonly IAgentRegistry _registry;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AgentRegistrySeeder> _logger;

    public AgentRegistrySeeder(
        IAgentRegistry registry,
        IConfiguration configuration,
        ILogger<AgentRegistrySeeder> logger)
    {
        _registry = registry;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var seed = _configuration.GetSection("Agents:Seed").Get<List<AgentSeedRecord>>();
        if (seed is null || seed.Count == 0)
        {
            return;
        }

        foreach (var item in seed)
        {
            if (string.IsNullOrWhiteSpace(item.ProxyToken) ||
                string.IsNullOrWhiteSpace(item.AgentId) ||
                string.IsNullOrWhiteSpace(item.Name))
            {
                continue;
            }

            await _registry.RegisterAgentAsync(
                item.ProxyToken,
                new AgentRecord
                {
                    AgentId = item.AgentId,
                    Name = item.Name,
                    Owner = item.Owner ?? "unknown",
                    Workspace = item.Workspace ?? "default",
                    ContactEmail = item.ContactEmail,
                    TrustScore = item.TrustScore,
                    Ring = item.Ring,
                    AllowedTools = item.AllowedTools,
                    BlockedTools = item.BlockedTools
                },
                cancellationToken);
        }

        _logger.LogInformation("Seeded {Count} Compass agent record(s).", seed.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private sealed class AgentSeedRecord
    {
        public string ProxyToken { get; init; } = "";
        public string AgentId { get; init; } = "";
        public string Name { get; init; } = "";
        public string? Owner { get; init; }
        public string? Workspace { get; init; }
        public string? ContactEmail { get; init; }
        public double TrustScore { get; init; } = 500;
        public int Ring { get; init; } = 2;
        public List<string> AllowedTools { get; init; } = [];
        public List<string> BlockedTools { get; init; } = [];
    }
}
