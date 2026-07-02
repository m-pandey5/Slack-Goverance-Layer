namespace Compass.Api.TokenStore;

public interface IProxyTokenStore
{
    Task<string?> ResolveSlackTokenAsync(string proxyToken, CancellationToken cancellationToken = default);

    Task StoreSlackTokenAsync(
        string proxyToken,
        string slackBotToken,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default);
}
