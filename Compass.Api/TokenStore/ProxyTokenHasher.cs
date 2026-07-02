using System.Security.Cryptography;
using System.Text;

namespace Compass.Api.TokenStore;

public static class ProxyTokenHasher
{
    public static string HashToken(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
