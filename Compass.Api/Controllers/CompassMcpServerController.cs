using System.Text.Json;
using Compass.Api.Agents;
using Compass.Api.Approvals;
using Compass.Api.Audit;
using Compass.Api.Risk;
using Microsoft.AspNetCore.Mvc;

namespace Compass.Api.Controllers;

/// <summary>
/// Compass acts as an MCP server — Slack's agent runtime calls this endpoint
/// to get governance verdicts before executing any tool.
/// </summary>
[ApiController]
[Route("mcp/compass")]
public sealed class CompassMcpServerController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly object[] ToolList =
    [
        new
        {
            name = "check-action",
            description = "Evaluate whether an agent is permitted to call a Slack tool. Returns allow/deny/approval_required.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    agent_id = new { type = "string", description = "DID or agent identifier" },
                    tool_name = new { type = "string", description = "Slack MCP tool name, e.g. conversations.archive" },
                    payload = new { type = "string", description = "Raw JSON-RPC payload (optional)" },
                    caller_agent_id = new { type = "string", description = "Delegating agent ID for confused-deputy check (optional)" }
                },
                required = new[] { "agent_id", "tool_name" }
            }
        },
        new
        {
            name = "get-approval-status",
            description = "Poll the status of a pending human approval request.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    request_id = new { type = "string", description = "Approval request ID" }
                },
                required = new[] { "request_id" }
            }
        },
        new
        {
            name = "audit-log",
            description = "Retrieve recent governance audit events for an agent.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    agent_id = new { type = "string", description = "Filter by agent ID (optional)" },
                    count = new { type = "integer", description = "Max records to return (1–100)" }
                },
                required = new string[] { }
            }
        }
    ];

    private readonly IAgentRegistry _agentRegistry;
    private readonly IApprovalStore _approvalStore;
    private readonly ICompassAuditSink _auditSink;
    private readonly AivssScorer _scorer;
    private readonly ILogger<CompassMcpServerController> _logger;

    public CompassMcpServerController(
        IAgentRegistry agentRegistry,
        IApprovalStore approvalStore,
        ICompassAuditSink auditSink,
        AivssScorer scorer,
        ILogger<CompassMcpServerController> logger)
    {
        _agentRegistry = agentRegistry;
        _approvalStore = approvalStore;
        _auditSink = auditSink;
        _scorer = scorer;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Handle()
    {
        using var reader = new StreamReader(Request.Body);
        var rawBody = await reader.ReadToEndAsync(HttpContext.RequestAborted);

        using var document = JsonDocument.Parse(rawBody);
        var root = document.RootElement;

        var method = root.TryGetProperty("method", out var m) ? m.GetString() : null;

        return method switch
        {
            "tools/list" => HandleToolsList(),
            "tools/call" => await HandleToolsCallAsync(root),
            _ => Ok(new
            {
                jsonrpc = "2.0",
                id = GetId(root),
                error = new { code = -32601, message = "Method not found" }
            })
        };
    }

    private IActionResult HandleToolsList()
    {
        return Ok(new
        {
            jsonrpc = "2.0",
            id = 1,
            result = new { tools = ToolList }
        });
    }

    private async Task<IActionResult> HandleToolsCallAsync(JsonElement root)
    {
        var id = GetId(root);
        if (!root.TryGetProperty("params", out var parameters))
        {
            return BadRequest(McpError(id, -32602, "Missing params"));
        }

        var toolName = parameters.TryGetProperty("name", out var tn) ? tn.GetString() : null;
        var args = parameters.TryGetProperty("arguments", out var a)
            ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(a.GetRawText(), JsonOptions)
            : null;

        _logger.LogInformation("[compass-mcp-server] tools/call name={Tool}", toolName);

        return toolName switch
        {
            "check-action" => await CheckActionAsync(id, args),
            "get-approval-status" => await GetApprovalStatusAsync(id, args),
            "audit-log" => await AuditLogAsync(id, args),
            _ => Ok(McpError(id, -32602, $"Unknown tool: {toolName}"))
        };
    }

    private async Task<IActionResult> CheckActionAsync(object? id, Dictionary<string, JsonElement>? args)
    {
        var agentId = args?.GetValueOrDefault("agent_id").GetString() ?? "";
        var toolName = args?.GetValueOrDefault("tool_name").GetString() ?? "";
        var callerAgentId = args?.GetValueOrDefault("caller_agent_id").GetString();

        if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(toolName))
        {
            return Ok(McpError(id, -32602, "agent_id and tool_name are required"));
        }

        var agent = await _agentRegistry.GetAgentAsync(agentId, HttpContext.RequestAborted);

        if (agent is { Revoked: true })
        {
            return Ok(McpResult(id, new
            {
                verdict = "deny",
                reason = "agent_suspended",
                agent_id = agentId,
                tool = toolName,
                risk = (object?)null
            }));
        }

        // Confused deputy check
        if (!string.IsNullOrWhiteSpace(callerAgentId) && callerAgentId != agentId)
        {
            var caller = await _agentRegistry.GetAgentAsync(callerAgentId, HttpContext.RequestAborted);
            if (caller is not null &&
                caller.AllowedTools.Count > 0 &&
                !caller.AllowedTools.Contains(toolName, StringComparer.OrdinalIgnoreCase))
            {
                return Ok(McpResult(id, new
                {
                    verdict = "deny",
                    reason = "confused_deputy",
                    message = $"Caller {callerAgentId} does not hold permission for {toolName}",
                    agent_id = agentId,
                    tool = toolName
                }));
            }
        }

        // Ring-based tier enforcement
        var isMultiAgent = !string.IsNullOrWhiteSpace(callerAgentId);
        var risk = _scorer.Score(toolName, agent, isMultiAgent);
        var maxTierForRing = (agent?.Ring ?? 2) switch
        {
            0 => ActionTier.DestructiveData,
            1 => ActionTier.DataExfiltration,
            2 => ActionTier.WriteSensitive,
            3 => ActionTier.WriteBenign,
            _ => ActionTier.WriteSensitive
        };

        if (risk.Tier > maxTierForRing)
        {
            return Ok(McpResult(id, new
            {
                verdict = "deny",
                reason = "ring_violation",
                message = $"Ring {agent?.Ring ?? 2} agents cannot perform {risk.Tier} actions",
                agent_id = agentId,
                tool = toolName,
                risk = RiskSummary(risk)
            }));
        }

        // High AIVSS score → require human approval
        if (risk.Score >= 7.0)
        {
            return Ok(McpResult(id, new
            {
                verdict = "approval_required",
                reason = "high_risk_score",
                message = $"AIVSS score {risk.Score} ({risk.Severity}) exceeds threshold",
                agent_id = agentId,
                tool = toolName,
                risk = RiskSummary(risk)
            }));
        }

        // Blocked tool check
        if (agent?.BlockedTools.Contains(toolName, StringComparer.OrdinalIgnoreCase) == true)
        {
            return Ok(McpResult(id, new
            {
                verdict = "deny",
                reason = "blocked_tool",
                agent_id = agentId,
                tool = toolName,
                risk = RiskSummary(risk)
            }));
        }

        return Ok(McpResult(id, new
        {
            verdict = "allow",
            agent_id = agentId,
            tool = toolName,
            ring = agent?.Ring ?? 2,
            trust_score = agent?.TrustScore ?? 500,
            risk = RiskSummary(risk)
        }));
    }

    private async Task<IActionResult> GetApprovalStatusAsync(object? id, Dictionary<string, JsonElement>? args)
    {
        var requestId = args?.GetValueOrDefault("request_id").GetString() ?? "";
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return Ok(McpError(id, -32602, "request_id is required"));
        }

        var approvals = await _approvalStore.ListPendingAsync(HttpContext.RequestAborted);
        var approval = approvals.FirstOrDefault(a =>
            string.Equals(a.RequestId, requestId, StringComparison.Ordinal));

        if (approval is null)
        {
            return Ok(McpResult(id, new
            {
                request_id = requestId,
                status = "not_found"
            }));
        }

        return Ok(McpResult(id, new
        {
            request_id = approval.RequestId,
            status = approval.Status,
            agent_id = approval.AgentId,
            tool = approval.ToolName,
            created_at = approval.CreatedAt
        }));
    }

    private async Task<IActionResult> AuditLogAsync(object? id, Dictionary<string, JsonElement>? args)
    {
        var filterAgentId = args?.GetValueOrDefault("agent_id").GetString();
        var count = args?.GetValueOrDefault("count").TryGetInt32(out var c) == true ? Math.Clamp(c, 1, 100) : 20;

        var records = await _auditSink.ReadRecentAsync(count * 2, HttpContext.RequestAborted);

        var filtered = string.IsNullOrWhiteSpace(filterAgentId)
            ? records.Take(count)
            : records
                .Where(r => string.Equals(r.AgentId, filterAgentId, StringComparison.Ordinal))
                .Take(count);

        return Ok(McpResult(id, new
        {
            records = filtered.Select(r => new
            {
                event_id = r.EventId,
                type = r.Type,
                agent_id = r.AgentId,
                policy = r.PolicyName,
                timestamp = r.Timestamp
            })
        }));
    }

    private static object RiskSummary(Risk.AivssResult risk) => new
    {
        score = risk.Score,
        severity = risk.Severity,
        tier = risk.Tier.ToString(),
        cvss_base = risk.CvssBase,
        aars = risk.Aars
    };

    private static object McpResult(object? id, object result) => new
    {
        jsonrpc = "2.0",
        id,
        result = new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(result, JsonOptions) } } }
    };

    private static object McpError(object? id, int code, string message) => new
    {
        jsonrpc = "2.0",
        id,
        error = new { code, message }
    };

    private static object? GetId(JsonElement root) =>
        root.TryGetProperty("id", out var idEl)
            ? idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64() : (object?)idEl.GetString()
            : null;
}
