using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentGovernance;
using AgentGovernance.Mcp;
using Microsoft.AspNetCore.Mvc;

namespace Compass.Api.Controllers;

[ApiController]
public sealed class McpProxyController : ControllerBase
{
    private readonly McpGateway _gateway;
    private readonly GovernanceKernel _kernel;
    private readonly McpResponseSanitizer _sanitizer;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<McpProxyController> _logger;

    public McpProxyController(
        McpGateway gateway,
        GovernanceKernel kernel,
        McpResponseSanitizer sanitizer,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<McpProxyController> logger)
    {
        _gateway = gateway;
        _kernel = kernel;
        _sanitizer = sanitizer;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpPost("mcp-proxy")]
    public async Task<IActionResult> ProxyMcpCall()
    {
        using var reader = new StreamReader(Request.Body);
        var rawBody = await reader.ReadToEndAsync(HttpContext.RequestAborted);

        using var document = JsonDocument.Parse(rawBody);
        var toolName = ResolveToolName(document.RootElement);
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return BadRequest(new { error = "missing_mcp_tool_name" });
        }

        var agentId = ResolveAgentId();
        var policyDecision = _kernel.EvaluateToolCall(agentId, toolName, new Dictionary<string, object>
        {
            ["payload"] = rawBody
        });

        if (!policyDecision.Allowed)
        {
            var statusCode = policyDecision.PolicyDecision?.RateLimited == true
                ? StatusCodes.Status429TooManyRequests
                : StatusCodes.Status403Forbidden;

            _logger.LogWarning(
                "Blocked MCP request by policy agent={AgentId} tool={Tool} rule={Rule}",
                agentId,
                toolName,
                policyDecision.PolicyDecision?.MatchedRule ?? "(none)");

            return StatusCode(statusCode, new
            {
                error = policyDecision.PolicyDecision?.Action ?? "policy_denied",
                agent_id = agentId,
                tool = toolName,
                rule = policyDecision.PolicyDecision?.MatchedRule,
                reason = policyDecision.Reason
            });
        }

        var decision = _gateway.ProcessRequest(new McpGatewayRequest
        {
            AgentId = agentId,
            ToolName = toolName,
            Payload = rawBody
        });

        if (!decision.Allowed)
        {
            var statusCode = decision.Status == McpGatewayStatus.RateLimited
                ? StatusCodes.Status429TooManyRequests
                : StatusCodes.Status403Forbidden;

            _logger.LogWarning(
                "Blocked MCP request agent={AgentId} tool={Tool} status={Status}",
                agentId,
                toolName,
                decision.Status);

            return StatusCode(statusCode, new
            {
                error = decision.Status.ToString(),
                agent_id = agentId,
                tool = toolName,
                retry_after_seconds = decision.RetryAfterSeconds
            });
        }

        var endpoint = _configuration["Slack:McpEndpoint"] ?? "https://mcp.slack.com/mcp";
        var client = _httpClientFactory.CreateClient("slack-mcp");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(decision.SanitizedPayload, Encoding.UTF8, "application/json")
        };

        if (Request.Headers.TryGetValue("Authorization", out var authorization) &&
            AuthenticationHeaderValue.TryParse(authorization, out var authHeader))
        {
            request.Headers.Authorization = authHeader;
        }

        using var response = await client.SendAsync(request, HttpContext.RequestAborted);
        var responseBody = await response.Content.ReadAsStringAsync(HttpContext.RequestAborted);
        var sanitizedResponse = _sanitizer.ScanText(responseBody);

        return Content(
            sanitizedResponse.Sanitized,
            response.Content.Headers.ContentType?.MediaType ?? "application/json",
            Encoding.UTF8);
    }

    private string ResolveAgentId()
    {
        var name = User?.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(name))
        {
            return $"did:mesh:{name}";
        }

        if (Request.Headers.TryGetValue("Authorization", out var authorization) &&
            authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = authorization.ToString()["Bearer ".Length..].Trim();
            var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..12].ToLowerInvariant();
            return $"did:mesh:bearer-{tokenHash}";
        }

        return "did:mesh:mcp-anonymous";
    }

    private static string? ResolveToolName(JsonElement root)
    {
        if (root.TryGetProperty("params", out var parameters) &&
            parameters.TryGetProperty("name", out var name) &&
            name.ValueKind == JsonValueKind.String)
        {
            return name.GetString();
        }

        if (root.TryGetProperty("method", out var method) &&
            method.ValueKind == JsonValueKind.String)
        {
            return method.GetString();
        }

        return null;
    }
}
