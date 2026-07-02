using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentGovernance;
using AgentGovernance.Mcp;
using Compass.Api.Agents;
using Compass.Api.Approvals;
using Compass.Api.Mcp;
using Compass.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Compass.Api.Controllers;

[ApiController]
public sealed class McpProxyController : ControllerBase
{
    private readonly McpGateway _gateway;
    private readonly GovernanceKernel _kernel;
    private readonly IAgentRegistry _agentRegistry;
    private readonly IApprovalStore _approvalStore;
    private readonly SlackWebClient _slackWebClient;
    private readonly IMcpForwarder _mcpForwarder;
    private readonly McpResponseSanitizer _sanitizer;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<McpProxyController> _logger;

    public McpProxyController(
        McpGateway gateway,
        GovernanceKernel kernel,
        IAgentRegistry agentRegistry,
        IApprovalStore approvalStore,
        SlackWebClient slackWebClient,
        IMcpForwarder mcpForwarder,
        McpResponseSanitizer sanitizer,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<McpProxyController> logger)
    {
        _gateway = gateway;
        _kernel = kernel;
        _agentRegistry = agentRegistry;
        _approvalStore = approvalStore;
        _slackWebClient = slackWebClient;
        _mcpForwarder = mcpForwarder;
        _sanitizer = sanitizer;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpPost("mcp")]
    [HttpPost("mcp-proxy")]
    public async Task<IActionResult> ProxyMcpCall()
    {
        using var reader = new StreamReader(Request.Body);
        var rawBody = await reader.ReadToEndAsync(HttpContext.RequestAborted);

        using var document = JsonDocument.Parse(rawBody);
        var method = ResolveMethod(document.RootElement);
        if (!string.Equals(method, "tools/call", StringComparison.Ordinal))
        {
            return await ForwardToSlackMcpAsync(rawBody, HttpContext.RequestAborted);
        }

        var toolName = ResolveToolCallName(document.RootElement);
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return BadRequest(new { error = "missing_mcp_tool_name" });
        }

        var agent = await ResolveAgentAsync(HttpContext.RequestAborted);
        var agentId = agent?.AgentId ?? ResolveAgentId();
        var policyDecision = _kernel.EvaluateToolCall(agentId, toolName, new Dictionary<string, object>
        {
            ["payload"] = rawBody,
            ["agent_name"] = agent?.Name ?? "",
            ["agent_owner"] = agent?.Owner ?? "",
            ["trust_score"] = agent?.TrustScore ?? 500,
            ["allowed_tools"] = agent?.AllowedTools.Cast<object>().ToList() ?? [],
            ["blocked_tools"] = agent?.BlockedTools.Cast<object>().ToList() ?? []
        });

        if (!policyDecision.Allowed)
        {
            if (string.Equals(policyDecision.PolicyDecision?.Action, "requireapproval", StringComparison.OrdinalIgnoreCase))
            {
                var approval = await CreateApprovalAsync(agentId, toolName, rawBody, HttpContext.RequestAborted);
                return StatusCode(StatusCodes.Status202Accepted, new
                {
                    status = "approval_required",
                    request_id = approval.RequestId,
                    agent_id = agentId,
                    tool = toolName,
                    rule = policyDecision.PolicyDecision?.MatchedRule,
                    reason = policyDecision.Reason
                });
            }

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
            if (decision.Status == McpGatewayStatus.RequiresApproval)
            {
                var approval = await CreateApprovalAsync(agentId, toolName, decision.SanitizedPayload, HttpContext.RequestAborted);
                return StatusCode(StatusCodes.Status202Accepted, new
                {
                    status = "approval_required",
                    request_id = approval.RequestId,
                    agent_id = agentId,
                    tool = toolName
                });
            }

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

        return await ForwardToSlackMcpAsync(decision.SanitizedPayload, HttpContext.RequestAborted);
    }

    private async Task<IActionResult> ForwardToSlackMcpAsync(string payload, CancellationToken cancellationToken)
    {
        var result = await _mcpForwarder.ForwardAsync(
            payload,
            Request.Headers.Authorization.ToString(),
            cancellationToken);

        return Content(
            result.Body,
            result.ContentType,
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

    private async Task<ApprovalRequest> CreateApprovalAsync(
        string agentId,
        string toolName,
        string payload,
        CancellationToken cancellationToken)
    {
        var approvalChannel = _configuration["Approvals:Channel"];
        var approval = await _approvalStore.CreateAsync(
            agentId,
            toolName,
            payload,
            approvalChannel,
            ShouldPersistAuthorizationHeader() ? Request.Headers.Authorization.ToString() : null,
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(approvalChannel))
        {
            await _slackWebClient.PostApprovalRequestAsync(
                approvalChannel,
                approval.RequestId,
                agentId,
                toolName,
                cancellationToken);
        }

        return approval;
    }

    private bool ShouldPersistAuthorizationHeader()
    {
        return string.Equals(_configuration["Approvals:PersistAuthorizationHeader"], "true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<AgentRecord?> ResolveAgentAsync(CancellationToken cancellationToken)
    {
        if (!Request.Headers.TryGetValue("Authorization", out var authorization) ||
            !authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = authorization.ToString()["Bearer ".Length..].Trim();
        if (!token.StartsWith("compass-", StringComparison.Ordinal))
        {
            return null;
        }

        var agent = await _agentRegistry.ResolveByProxyTokenAsync(token, cancellationToken);
        return agent is { Revoked: false } ? agent : null;
    }

    private static string? ResolveMethod(JsonElement root)
    {
        return root.TryGetProperty("method", out var method) &&
               method.ValueKind == JsonValueKind.String
            ? method.GetString()
            : null;
    }

    private static string? ResolveToolCallName(JsonElement root)
    {
        if (root.TryGetProperty("params", out var parameters) &&
            parameters.TryGetProperty("name", out var name) &&
            name.ValueKind == JsonValueKind.String)
        {
            return name.GetString();
        }

        return null;
    }
}
