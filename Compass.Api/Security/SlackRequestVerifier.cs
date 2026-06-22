using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Primitives;

namespace Compass.Api.Security;

public sealed class SlackRequestVerifier
{
    private static readonly TimeSpan TimestampTolerance = TimeSpan.FromMinutes(5);

    private readonly IConfiguration _configuration;
    private readonly TimeProvider _timeProvider;

    public SlackRequestVerifier(IConfiguration configuration, TimeProvider? timeProvider = null)
    {
        _configuration = configuration;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool TryVerify(IHeaderDictionary headers, string rawBody, out string failureReason)
    {
        var signingSecret = _configuration["Slack:SigningSecret"];
        if (string.IsNullOrWhiteSpace(signingSecret))
        {
            failureReason = "Slack signing secret is not configured.";
            return false;
        }

        if (!headers.TryGetValue("X-Slack-Request-Timestamp", out var timestampValues) ||
            !TryGetSingleHeader(timestampValues, out var timestampText) ||
            !long.TryParse(timestampText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timestamp))
        {
            failureReason = "Missing or invalid X-Slack-Request-Timestamp.";
            return false;
        }

        var requestTime = DateTimeOffset.FromUnixTimeSeconds(timestamp);
        var now = _timeProvider.GetUtcNow();
        if (now - requestTime > TimestampTolerance || requestTime - now > TimestampTolerance)
        {
            failureReason = "Slack request timestamp is outside the allowed replay window.";
            return false;
        }

        if (!headers.TryGetValue("X-Slack-Signature", out var signatureValues) ||
            !TryGetSingleHeader(signatureValues, out var suppliedSignature))
        {
            failureReason = "Missing X-Slack-Signature.";
            return false;
        }

        var signatureBase = $"v0:{timestamp}:{rawBody}";
        var expectedSignature = "v0=" + ComputeSha256Hex(signingSecret, signatureBase);

        if (!FixedTimeEquals(expectedSignature, suppliedSignature))
        {
            failureReason = "Slack signature mismatch.";
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    private static string ComputeSha256Hex(string secret, string value)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private static bool TryGetSingleHeader(StringValues values, out string value)
    {
        value = values.Count > 0 ? values[0] ?? string.Empty : string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }
}
