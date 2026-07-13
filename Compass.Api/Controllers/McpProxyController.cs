using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentGovernance;
using AgentGovernance.Mcp;
using Compass.Api.Agents;
using Compass.Api.Approvals;
using Compass.Api.Mcp;
using Compass.Api.Risk;
using Compass.Api.Services;
using Compass.Api.SRE;
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
    private readonly ICircuitBreaker _circuitBreaker;
    private readonly AivssScorer _scorer;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<McpProxyController> _logger;

    // Ring → maximum action tier permitted
    private static readonly ActionTier[] RingMaxTier =
    [
        ActionTier.DestructiveData,  // Ring 0 — core
        ActionTier.DataExfiltration, // Ring 1 — trusted
        ActionTier.WriteSensitive,   // Ring 2 — standard
        ActionTier.WriteBenign       // Ring 3 — untrusted
    ];

    public McpProxyController(
        McpGateway gateway,
        GovernanceKernel kernel,
        IAgentRegistry agentRegistry,
        IApprovalStore approvalStore,
        SlackWebClient slackWebClient,
        IMcpForwarder mcpForwarder,
        McpResponseSanitizer sanitizer,
        ICircuitBreaker circuitBreaker,
        AivssScorer scorer,
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
        _circuitBreaker = circuitBreaker;
        _scorer = scorer;
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

        // Gate: circuit breaker — open circuit means agent is blocked for 10 min
        if (_circuitBreaker.IsOpen(agentId))
        {
            _logger.LogWarning("[circuit-breaker] Agent blocked agent={AgentId}", agentId);
            await _slackWebClient.PostAlertAsync(agentId, toolName, "circuit_breaker_open",
                cancellationToken: HttpContext.RequestAborted);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "circuit_open",
                agent_id = agentId,
                message = "Too many policy violations. Agent blocked for 10 minutes.",
                status = _circuitBreaker.GetStatus(agentId)
            });
        }

        // Gate: confused deputy — X-Compass-Caller-Agent header identifies the orchestrator
        var callerAgentId = Request.Headers.TryGetValue("X-Compass-Caller-Agent", out var callerHeader)
            ? callerHeader.ToString()
            : null;

        if (!string.IsNullOrWhiteSpace(callerAgentId) && callerAgentId != agentId)
        {
            var callerResult = await CheckConfusedDeputyAsync(callerAgentId, agentId, toolName);
            if (callerResult is not null)
            {
                _circuitBreaker.RecordFailure(agentId);
                await _slackWebClient.PostAlertAsync(agentId, toolName, "confused_deputy",
                    cancellationToken: HttpContext.RequestAborted);
                return callerResult;
            }
        }

        // Gate: ring model enforcement
        var isMultiAgent = !string.IsNullOrWhiteSpace(callerAgentId);
        var risk = _scorer.Score(toolName, agent, isMultiAgent);
        var ring = Math.Clamp(agent?.Ring ?? 2, 0, 3);
        var maxTier = RingMaxTier[ring];

        if (risk.Tier > maxTier)
        {
            _logger.LogWarning(
                "[ring-model] Ring {Ring} agent blocked from {Tier} action agent={AgentId} tool={Tool}",
                ring, risk.Tier, agentId, toolName);

            _circuitBreaker.RecordFailure(agentId);
            await _slackWebClient.PostAlertAsync(agentId, toolName, $"ring_{ring}_violation",
                risk.Score, HttpContext.RequestAborted);

            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "ring_violation",
                agent_id = agentId,
                tool = toolName,
                ring,
                tier = risk.Tier.ToString(),
                max_allowed_tier = maxTier.ToString(),
                aivss_score = risk.Score,
                severity = risk.Severity
            });
        }

        // Gate: GovernanceKernel policy evaluation
        var policyDecision = _kernel.EvaluateToolCall(agentId, toolName, new Dictionary<string, object>
        {
            ["payload"] = rawBody,
            ["agent_name"] = agent?.Name ?? "",
            ["agent_owner"] = agent?.Owner ?? "",
            ["trust_score"] = agent?.TrustScore ?? 500,
            ["ring"] = ring,
            ["aivss_score"] = risk.Score,
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
                    reason = policyDecision.Reason,
                    aivss_score = risk.Score
                });
            }

            _circuitBreaker.RecordFailure(agentId);
            var statusCode = policyDecision.PolicyDecision?.RateLimited == true
                ? StatusCodes.Status429TooManyRequests
                : StatusCodes.Status403Forbidden;

            _logger.LogWarning(
                "Blocked MCP request by policy agent={AgentId} tool={Tool} rule={Rule}",
                agentId, toolName, policyDecision.PolicyDecision?.MatchedRule ?? "(none)");

            await _slackWebClient.PostAlertAsync(agentId, toolName,
                policyDecision.PolicyDecision?.MatchedRule ?? "policy_denied",
                risk.Score, HttpContext.RequestAborted);

            return StatusCode(statusCode, new
            {
                error = policyDecision.PolicyDecision?.Action ?? "policy_denied",
                agent_id = agentId,
                tool = toolName,
                rule = policyDecision.PolicyDecision?.MatchedRule,
                reason = policyDecision.Reason,
                aivss_score = risk.Score,
                severity = risk.Severity
            });
        }

        // Gate: McpGateway secondary check
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
                    tool = toolName,
                    aivss_score = risk.Score
                });
            }

            _circuitBreaker.RecordFailure(agentId);
            var statusCode = decision.Status == McpGatewayStatus.RateLimited
                ? StatusCodes.Status429TooManyRequests
                : StatusCodes.Status403Forbidden;

            _logger.LogWarning(
                "Blocked MCP request agent={AgentId} tool={Tool} status={Status}",
                agentId, toolName, decision.Status);

            await _slackWebClient.PostAlertAsync(agentId, toolName,
                decision.Status.ToString(), risk.Score, HttpContext.RequestAborted);

            return StatusCode(statusCode, new
            {
                error = decision.Status.ToString(),
                agent_id = agentId,
                tool = toolName,
                retry_after_seconds = decision.RetryAfterSeconds,
                aivss_score = risk.Score
            });
        }

        // All gates passed — forward to mcp.slack.com
        _circuitBreaker.RecordSuccess(agentId);
        return await ForwardToSlackMcpAsync(decision.SanitizedPayload, HttpContext.RequestAborted);
    }

    private async Task<IActionResult?> CheckConfusedDeputyAsync(
        string callerAgentId,
        string targetAgentId,
        string toolName)
    {
        var caller = await _agentRegistry.GetAgentAsync(callerAgentId, HttpContext.RequestAborted);
        if (caller is null)
        {
            return null;
        }

        // Confused deputy: caller is trying to delegate an action it doesn't hold
        if (caller.AllowedTools.Count > 0 &&
            !caller.AllowedTools.Contains(toolName, StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "[confused-deputy] Caller {CallerAgent} cannot delegate {Tool} to {TargetAgent}",
                callerAgentId, toolName, targetAgentId);

            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "confused_deputy",
                message = $"Orchestrator {callerAgentId} does not hold permission for {toolName}",
                caller_agent_id = callerAgentId,
                target_agent_id = targetAgentId,
                tool = toolName
            });
        }

        // Ring escalation: caller cannot delegate to a higher-privileged ring
        var targetAgent = await _agentRegistry.GetAgentAsync(targetAgentId, HttpContext.RequestAborted);
        if (caller is not null && targetAgent is not null && targetAgent.Ring < caller.Ring)
        {
            _logger.LogWarning(
                "[confused-deputy] Ring escalation caller={Caller}(ring={CallerRing}) → target={Target}(ring={TargetRing})",
                callerAgentId, caller.Ring, targetAgentId, targetAgent.Ring);

            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "ring_escalation",
                message = $"Ring {caller.Ring} agent cannot orchestrate Ring {targetAgent.Ring} agent",
                caller_agent_id = callerAgentId,
                caller_ring = caller.Ring,
                target_agent_id = targetAgentId,
                target_ring = targetAgent.Ring
            });
        }

        return null;
    }

    private async Task<IActionResult> ForwardToSlackMcpAsync(string payload, CancellationToken cancellationToken)
    {
        var result = await _mcpForwarder.ForwardAsync(
            payload,
            Request.Headers.Authorization.ToString(),
            cancellationToken);

        return Content(result.Body, result.ContentType, Encoding.UTF8);
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
