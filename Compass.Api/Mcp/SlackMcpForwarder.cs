using System.Net.Http.Headers;
using System.Text;
using AgentGovernance.Mcp;

namespace Compass.Api.Mcp;

public sealed class SlackMcpForwarder : IMcpForwarder
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly McpResponseSanitizer _sanitizer;

    public SlackMcpForwarder(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        McpResponseSanitizer sanitizer)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _sanitizer = sanitizer;
    }

    public async Task<McpForwardResult> ForwardAsync(
        string payload,
        string? authorizationHeader = null,
        CancellationToken cancellationToken = default)
    {
        var endpoint = _configuration["Slack:McpEndpoint"] ?? "https://mcp.slack.com/mcp";
        var client = _httpClientFactory.CreateClient("slack-mcp");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        var configuredBearer = _configuration["Slack:McpBearerToken"] ?? _configuration["Slack:BotToken"];
        if (!string.IsNullOrWhiteSpace(configuredBearer))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuredBearer);
        }
        else if (!string.IsNullOrWhiteSpace(authorizationHeader) &&
                 AuthenticationHeaderValue.TryParse(authorizationHeader, out var parsed))
        {
            request.Headers.Authorization = parsed;
        }

        using var response = await client.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var sanitizedResponse = _sanitizer.ScanText(responseBody);

        return new McpForwardResult
        {
            StatusCode = (int)response.StatusCode,
            Body = sanitizedResponse.Sanitized,
            ContentType = response.Content.Headers.ContentType?.MediaType ?? "application/json"
        };
    }
}
