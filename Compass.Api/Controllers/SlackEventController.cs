using System.Text.Json;
using AgentGovernance;
using Compass.Api.Services;
using Compass.Api.Security;
using Microsoft.AspNetCore.Mvc;

namespace Compass.Api.Controllers;

[ApiController]
public sealed class SlackEventController : ControllerBase
{
    private readonly GovernanceKernel _kernel;
    private readonly SlackRequestVerifier _verifier;
    private readonly SlackWebClient _slackWebClient;
    private readonly ILogger<SlackEventController> _logger;

    public SlackEventController(
        GovernanceKernel kernel,
        SlackRequestVerifier verifier,
        SlackWebClient slackWebClient,
        ILogger<SlackEventController> logger)
    {
        _kernel = kernel;
        _verifier = verifier;
        _slackWebClient = slackWebClient;
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
        var botId = GetString(slackEvent, "bot_id", string.Empty);
        var appId = GetString(slackEvent, "app_id", string.Empty);
        var subtype = GetString(slackEvent, "subtype", string.Empty);
        var userId = GetString(slackEvent, "user", string.IsNullOrWhiteSpace(botId) ? "unknown" : botId);
        var channel = GetString(slackEvent, "channel", string.Empty);
        var text = GetString(slackEvent, "text", string.Empty);
        var threadTs = GetString(slackEvent, "ts", string.Empty);

        var agentId = $"did:mesh:slack-{userId}";
        var toolName = $"SLACK_{eventType.ToUpperInvariant()}";
        var args = new Dictionary<string, object>
        {
            ["text"] = text,
            ["channel"] = channel,
            ["channel_type"] = GetString(slackEvent, "channel_type", string.Empty),
            ["event_type"] = eventType,
            ["slack_user"] = userId,
            ["slack_bot_id"] = botId,
            ["slack_app_id"] = appId,
            ["slack_subtype"] = subtype,
            ["is_third_party_bot_message"] = IsThirdPartyBotMessage(eventType, botId, appId, subtype)
        };

        var decision = _kernel.EvaluateToolCall(agentId, toolName, args);
        if (!decision.Allowed)
        {
            if (!string.IsNullOrWhiteSpace(channel))
            {
                var responseText = IsThirdPartyBotMessage(eventType, botId, appId, subtype)
                    ? $"Compass Governor detected a reactive violation after this Slack message was posted: {decision.Reason}"
                    : $"Compass Governor blocked this request: {decision.Reason}";

                await _slackWebClient.PostMessageAsync(
                    channel,
                    responseText,
                    threadTs,
                    HttpContext.RequestAborted);
            }

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

    private static bool IsThirdPartyBotMessage(string eventType, string botId, string appId, string subtype)
    {
        return string.Equals(eventType, "message", StringComparison.Ordinal) &&
               (!string.IsNullOrWhiteSpace(botId) ||
                !string.IsNullOrWhiteSpace(appId) ||
                string.Equals(subtype, "bot_message", StringComparison.Ordinal));
    }
}
