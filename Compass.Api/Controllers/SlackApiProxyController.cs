using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentGovernance;
using Compass.Api.Agents;
using Compass.Api.TokenStore;
using Microsoft.AspNetCore.Mvc;

namespace Compass.Api.Controllers;

[ApiController]
[Route("api")]
public sealed class SlackApiProxyController : ControllerBase
{
    private readonly GovernanceKernel _kernel;
    private readonly IAgentRegistry _agentRegistry;
    private readonly IProxyTokenStore _tokenStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SlackApiProxyController> _logger;

    public SlackApiProxyController(
        GovernanceKernel kernel,
        IAgentRegistry agentRegistry,
        IProxyTokenStore tokenStore,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<SlackApiProxyController> logger)
    {
        _kernel = kernel;
        _agentRegistry = agentRegistry;
        _tokenStore = tokenStore;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpPost("{method}")]
    public async Task<IActionResult> ProxySlackApiCall(string method)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            return SlackError("missing_method");
        }

        var incomingToken = ResolveBearerToken();
        using var outbound = await BuildOutboundRequestAsync(method, incomingToken, HttpContext.RequestAborted);
        var agent = await ResolveAgentAsync(incomingToken, HttpContext.RequestAborted);
        var agentId = agent?.AgentId ?? ResolveAgentId(incomingToken);

        var decision = _kernel.EvaluateToolCall(agentId, method, new Dictionary<string, object>
        {
            ["slack_api_method"] = method,
            ["path"] = $"/api/{method}",
            ["agent_name"] = agent?.Name ?? "",
            ["agent_owner"] = agent?.Owner ?? "",
            ["trust_score"] = agent?.TrustScore ?? 500,
            ["allowed_tools"] = agent?.AllowedTools.Cast<object>().ToList() ?? [],
            ["blocked_tools"] = agent?.BlockedTools.Cast<object>().ToList() ?? []
        });

        if (!decision.Allowed)
        {
            _logger.LogWarning(
                "Blocked Slack API proxy call agent={AgentId} method={Method} rule={Rule}",
                agentId,
                method,
                decision.PolicyDecision?.MatchedRule ?? "(none)");

            return SlackError(
                "compass_policy_denied",
                new
                {
                    reason = decision.Reason,
                    rule = decision.PolicyDecision?.MatchedRule
                });
        }

        var resolvedToken = await ResolveSlackTokenAsync(outbound.PresentedToken, HttpContext.RequestAborted);
        if (string.IsNullOrWhiteSpace(resolvedToken))
        {
            return SlackError("compass_invalid_proxy_token");
        }

        if (outbound.UsesAuthorizationHeader)
        {
            outbound.Request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", resolvedToken);
        }

        using var response = await _httpClientFactory
            .CreateClient("slack-api")
            .SendAsync(outbound.Request, HttpContext.RequestAborted);

        var responseBody = await response.Content.ReadAsStringAsync(HttpContext.RequestAborted);
        return Content(
            responseBody,
            response.Content.Headers.ContentType?.MediaType ?? "application/json",
            Encoding.UTF8);
    }

    private async Task<OutboundSlackApiRequest> BuildOutboundRequestAsync(
        string method,
        string? bearerToken,
        CancellationToken cancellationToken)
    {
        var baseUrl = _configuration["Slack:ApiBaseUrl"]?.TrimEnd('/') ?? "https://slack.com/api";
        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/{method}");

        if (Request.HasFormContentType)
        {
            var form = await Request.ReadFormAsync(cancellationToken);
            var values = form.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToString(), StringComparer.Ordinal);
            var presentedToken = bearerToken ?? values.GetValueOrDefault("token");
            var slackToken = await ResolveSlackTokenAsync(presentedToken, cancellationToken);
            if (!string.IsNullOrWhiteSpace(slackToken) && values.ContainsKey("token"))
            {
                values["token"] = slackToken;
            }

            request.Content = new FormUrlEncodedContent(values);
            return new OutboundSlackApiRequest(request, presentedToken, bearerToken is not null);
        }

        using var reader = new StreamReader(Request.Body);
        var rawBody = await reader.ReadToEndAsync(cancellationToken);
        var contentType = Request.ContentType ?? "application/json";
        var tokenFromBody = default(string);

        if (IsJsonContent(contentType) && !string.IsNullOrWhiteSpace(rawBody))
        {
            var json = JsonNode.Parse(rawBody) as JsonObject ?? new JsonObject();
            tokenFromBody = json.TryGetPropertyValue("token", out var tokenNode)
                ? tokenNode?.GetValue<string>()
                : null;

            var presentedToken = bearerToken ?? tokenFromBody;
            var slackToken = await ResolveSlackTokenAsync(presentedToken, cancellationToken);
            if (!string.IsNullOrWhiteSpace(slackToken) && tokenFromBody is not null)
            {
                json["token"] = slackToken;
            }

            request.Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json");
            return new OutboundSlackApiRequest(request, presentedToken, bearerToken is not null);
        }

        request.Content = new StringContent(rawBody, Encoding.UTF8, contentType);
        return new OutboundSlackApiRequest(request, bearerToken, bearerToken is not null);
    }

    private async Task<string?> ResolveSlackTokenAsync(string? presentedToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(presentedToken))
        {
            return null;
        }

        if (presentedToken.StartsWith("compass-", StringComparison.Ordinal))
        {
            return await _tokenStore.ResolveSlackTokenAsync(presentedToken, cancellationToken);
        }

        return presentedToken;
    }

    private async Task<AgentRecord?> ResolveAgentAsync(string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) ||
            !token.StartsWith("compass-", StringComparison.Ordinal))
        {
            return null;
        }

        var agent = await _agentRegistry.ResolveByProxyTokenAsync(token, cancellationToken);
        return agent is { Revoked: false } ? agent : null;
    }

    private string ResolveAgentId(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return "did:mesh:slack-api-anonymous";
        }

        var tokenHash = ProxyTokenHasher.HashToken(token)[..12];
        return token.StartsWith("compass-", StringComparison.Ordinal)
            ? $"did:mesh:compass-token-{tokenHash}"
            : $"did:mesh:direct-slack-token-{tokenHash}";
    }

    private string? ResolveBearerToken()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var authorization))
        {
            return null;
        }

        var value = authorization.ToString();
        return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? value["Bearer ".Length..].Trim()
            : null;
    }

    private static bool IsJsonContent(string contentType)
    {
        return contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase);
    }

    private static ObjectResult SlackError(string error, object? details = null)
    {
        return new ObjectResult(new
        {
            ok = false,
            error,
            details
        })
        {
            StatusCode = StatusCodes.Status200OK
        };
    }

    private sealed record OutboundSlackApiRequest(
        HttpRequestMessage Request,
        string? PresentedToken,
        bool UsesAuthorizationHeader) : IDisposable
    {
        public void Dispose() => Request.Dispose();
    }
}
