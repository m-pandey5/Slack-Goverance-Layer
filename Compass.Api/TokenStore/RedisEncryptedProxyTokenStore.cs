using Microsoft.AspNetCore.DataProtection;
using StackExchange.Redis;

namespace Compass.Api.TokenStore;

public sealed class RedisEncryptedProxyTokenStore : IProxyTokenStore
{
    private const string KeyPrefix = "compass:proxy-token:";

    private readonly IDatabase _database;
    private readonly IDataProtector _protector;

    public RedisEncryptedProxyTokenStore(
        IConnectionMultiplexer redis,
        IDataProtectionProvider dataProtectionProvider)
    {
        _database = redis.GetDatabase();
        _protector = dataProtectionProvider.CreateProtector("Compass.ProxyTokenStore.v1");
    }

    public async Task<string?> ResolveSlackTokenAsync(string proxyToken, CancellationToken cancellationToken = default)
    {
        var protectedToken = await _database.StringGetAsync(ToRedisKey(proxyToken)).ConfigureAwait(false);
        if (protectedToken.IsNullOrEmpty)
        {
            return null;
        }

        return _protector.Unprotect(protectedToken.ToString());
    }

    public Task StoreSlackTokenAsync(
        string proxyToken,
        string slackBotToken,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        var protectedToken = _protector.Protect(slackBotToken);
        return StoreProtectedTokenAsync(ToRedisKey(proxyToken), protectedToken, ttl);
    }

    private async Task StoreProtectedTokenAsync(string redisKey, string protectedToken, TimeSpan? ttl)
    {
        await _database.StringSetAsync(redisKey, protectedToken).ConfigureAwait(false);
        if (ttl is not null)
        {
            await _database.KeyExpireAsync(redisKey, ttl).ConfigureAwait(false);
        }
    }

    private static string ToRedisKey(string proxyToken)
    {
        return KeyPrefix + ProxyTokenHasher.HashToken(proxyToken);
    }
}
