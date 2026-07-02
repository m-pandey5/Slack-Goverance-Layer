using System.Text.Json;
using Compass.Api.Approvals;
using Compass.Api.Security;
using Microsoft.AspNetCore.Mvc;

namespace Compass.Api.Controllers;

[ApiController]
public sealed class SlackInteractivityController : ControllerBase
{
    private readonly SlackRequestVerifier _verifier;
    private readonly IApprovalStore _approvalStore;
    private readonly IApprovalDecisionPublisher _decisionPublisher;
    private readonly ILogger<SlackInteractivityController> _logger;

    public SlackInteractivityController(
        SlackRequestVerifier verifier,
        IApprovalStore approvalStore,
        IApprovalDecisionPublisher decisionPublisher,
        ILogger<SlackInteractivityController> logger)
    {
        _verifier = verifier;
        _approvalStore = approvalStore;
        _decisionPublisher = decisionPublisher;
        _logger = logger;
    }

    [HttpPost("slack/interactivity")]
    public async Task<IActionResult> HandleInteractivity()
    {
        using var reader = new StreamReader(Request.Body);
        var rawBody = await reader.ReadToEndAsync(HttpContext.RequestAborted);

        if (!_verifier.TryVerify(Request.Headers, rawBody, out var failureReason))
        {
            _logger.LogWarning("Rejected Slack interactivity request: {Reason}", failureReason);
            return Unauthorized(new { error = "invalid_slack_signature", reason = failureReason });
        }

        var form = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(rawBody);
        if (!form.TryGetValue("payload", out var payloadValues))
        {
            return BadRequest(new { error = "missing_payload" });
        }

        using var document = JsonDocument.Parse(payloadValues.ToString());
        var root = document.RootElement;
        var userId = root.TryGetProperty("user", out var user) &&
                     user.TryGetProperty("id", out var id)
            ? id.GetString() ?? "unknown"
            : "unknown";

        var action = root.GetProperty("actions")[0];
        var actionId = action.GetProperty("action_id").GetString() ?? "";
        var requestId = action.GetProperty("value").GetString() ?? "";
        var decision = actionId switch
        {
            "compass_approval_approve" => "approved",
            "compass_approval_deny" => "denied",
            _ => ""
        };

        if (string.IsNullOrWhiteSpace(decision) || string.IsNullOrWhiteSpace(requestId))
        {
            return BadRequest(new { error = "unknown_action" });
        }

        var approval = await _approvalStore.DecideAsync(requestId, decision, userId, HttpContext.RequestAborted);
        if (approval is null)
        {
            return Ok(new { text = $"Approval request `{requestId}` was not found." });
        }

        await _decisionPublisher.PublishAsync(
            new ApprovalDecisionMessage
            {
                RequestId = requestId,
                Decision = decision,
                DecidedBy = userId
            },
            HttpContext.RequestAborted);

        return Ok(new
        {
            text = $"Compass approval `{requestId}` {decision} by <@{userId}>. Execution has been queued."
        });
    }
}
