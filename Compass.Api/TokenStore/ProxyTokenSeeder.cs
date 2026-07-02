namespace Compass.Api.TokenStore;

public sealed class ProxyTokenSeeder : IHostedService
{
    private readonly IProxyTokenStore _tokenStore;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ProxyTokenSeeder> _logger;

    public ProxyTokenSeeder(
        IProxyTokenStore tokenStore,
        IConfiguration configuration,
        ILogger<ProxyTokenSeeder> logger)
    {
        _tokenStore = tokenStore;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var mappings = _configuration.GetSection("ProxyTokens:Mappings").Get<Dictionary<string, string>>();
        if (mappings is null || mappings.Count == 0)
        {
            return;
        }

        foreach (var (proxyToken, slackBotToken) in mappings)
        {
            if (string.IsNullOrWhiteSpace(proxyToken) || string.IsNullOrWhiteSpace(slackBotToken))
            {
                continue;
            }

            await _tokenStore.StoreSlackTokenAsync(proxyToken, slackBotToken, cancellationToken: cancellationToken);
        }

        _logger.LogInformation("Seeded {Count} Compass proxy token mapping(s).", mappings.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
