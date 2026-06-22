using System.Text.Json;
using AgentGovernance;
using Compass.Api.Security;
using Microsoft.AspNetCore.Mvc;

namespace Compass.Api.Controllers;

[ApiController]
public sealed class SlackEventController : ControllerBase
{
    private readonly GovernanceKernel _kernel;
    private readonly SlackRequestVerifier _verifier;
    private readonly ILogger<SlackEventController> _logger;

    public SlackEventController(
        GovernanceKernel kernel,
        SlackRequestVerifier verifier,
        ILogger<SlackEventController> logger)
    {
        _kernel = kernel;
        _verifier = verifier;
        _logger = logger;
    }

    [HttpPost("slack/events")]
    public async Task<IActionResult> HandleEvent()
    {
        using var reader = new StreamReader(Request.Body);
        var rawBody = await reader.ReadToEndAsync(HttpContext.RequestAborted);

        if (!_verifier.TryVerify(Request.Headers, rawBody, out var failureReason))
        {
            _logger.LogWarning("Rejected Slack event: {Reason}", failureReason);
            return Unauthorized(new { error = "invalid_slack_signature", reason = failureReason });
        }

        using var document = JsonDocument.Parse(rawBody);
        var root = document.RootElement;

        if (root.TryGetProperty("type", out var typeElement) &&
            string.Equals(typeElement.GetString(), "url_verification", StringComparison.Ordinal))
        {
            return Ok(new { challenge = root.GetProperty("challenge").GetString() });
        }

        if (!root.TryGetProperty("event", out var slackEvent))
        {
            return BadRequest(new { error = "missing_event" });
        }

        var eventType = GetString(slackEvent, "type", "unknown");
        var userId = GetString(slackEvent, "user", GetString(slackEvent, "bot_id", "unknown"));
        var channel = GetString(slackEvent, "channel", string.Empty);
        var text = GetString(slackEvent, "text", string.Empty);

        var agentId = $"did:mesh:slack-{userId}";
        var toolName = $"SLACK_{eventType.ToUpperInvariant()}";
        var args = new Dictionary<string, object>
        {
            ["text"] = text,
            ["channel"] = channel,
            ["channel_type"] = GetString(slackEvent, "channel_type", string.Empty),
            ["event_type"] = eventType,
            ["slack_user"] = userId
        };

        var decision = _kernel.EvaluateToolCall(agentId, toolName, args);
        if (!decision.Allowed)
        {
            return Ok(new
            {
                status = "blocked",
                reason = decision.Reason,
                rule = decision.PolicyDecision?.MatchedRule
            });
        }

        return Ok(new
        {
            status = "allowed",
            agent_id = agentId,
            tool = toolName
        });
    }

    private static string GetString(JsonElement element, string propertyName, string fallback)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? fallback
            : fallback;
    }
}
