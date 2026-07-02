using System.Collections.Concurrent;

namespace Compass.Api.TokenStore;

public sealed class InMemoryProxyTokenStore : IProxyTokenStore
{
    private readonly ConcurrentDictionary<string, string> _tokens = new(StringComparer.Ordinal);

    public InMemoryProxyTokenStore(IConfiguration configuration)
    {
        var mappings = configuration.GetSection("ProxyTokens:Mappings").Get<Dictionary<string, string>>();
        if (mappings is null)
        {
            return;
        }

        foreach (var (proxyToken, slackBotToken) in mappings)
        {
            if (!string.IsNullOrWhiteSpace(proxyToken) && !string.IsNullOrWhiteSpace(slackBotToken))
            {
                _tokens[ProxyTokenHasher.HashToken(proxyToken)] = slackBotToken;
            }
        }
    }

    public Task<string?> ResolveSlackTokenAsync(string proxyToken, CancellationToken cancellationToken = default)
    {
        _tokens.TryGetValue(ProxyTokenHasher.HashToken(proxyToken), out var slackToken);
        return Task.FromResult<string?>(slackToken);
    }

    public Task StoreSlackTokenAsync(
        string proxyToken,
        string slackBotToken,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        _tokens[ProxyTokenHasher.HashToken(proxyToken)] = slackBotToken;
        return Task.CompletedTask;
    }
}
