using System.Text;
using AgentGovernance.Trust;
using Compass.Api.Agents;
using Compass.Api.Approvals;
using Compass.Api.Security;
using Microsoft.AspNetCore.Mvc;

namespace Compass.Api.Controllers;

[ApiController]
public sealed class SlackCommandController : ControllerBase
{
    private readonly SlackRequestVerifier _verifier;
    private readonly IAgentRegistry _agentRegistry;
    private readonly IApprovalStore _approvalStore;
    private readonly FileTrustStore _trustStore;
    private readonly ILogger<SlackCommandController> _logger;

    public SlackCommandController(
        SlackRequestVerifier verifier,
        IAgentRegistry agentRegistry,
        IApprovalStore approvalStore,
        FileTrustStore trustStore,
        ILogger<SlackCommandController> logger)
    {
        _verifier = verifier;
        _agentRegistry = agentRegistry;
        _approvalStore = approvalStore;
        _trustStore = trustStore;
        _logger = logger;
    }

    [HttpPost("slack/commands")]
    public async Task<IActionResult> HandleCommand()
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var rawBody = await reader.ReadToEndAsync(HttpContext.RequestAborted);

        if (!_verifier.TryVerify(Request.Headers, rawBody, out var failureReason))
        {
            _logger.LogWarning("Rejected Slack command: {Reason}", failureReason);
            return Unauthorized(new { error = "invalid_slack_signature", reason = failureReason });
        }

        var form = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(rawBody);
        var text = form.TryGetValue("text", out var value) ? value.ToString().Trim() : "";
        var command = string.IsNullOrWhiteSpace(text) ? "status" : text.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();

        return command switch
        {
            "status" => await Status(),
            "agents" => await Agents(),
            "approvals" => await Approvals(),
            "trust" => Trust(),
            _ => Ok(Ephemeral("Try `/compass status`, `/compass agents`, `/compass approvals`, or `/compass trust`."))
        };
    }

    private async Task<IActionResult> Status()
    {
        var agents = await _agentRegistry.ListAgentsAsync(HttpContext.RequestAborted);
        var approvals = await _approvalStore.ListPendingAsync(HttpContext.RequestAborted);
        return Ok(Ephemeral($"Compass is online. Agents: {agents.Count}. Pending approvals: {approvals.Count}."));
    }

    private async Task<IActionResult> Agents()
    {
        var agents = await _agentRegistry.ListAgentsAsync(HttpContext.RequestAborted);
        if (agents.Count == 0)
        {
            return Ok(Ephemeral("No registered Compass agents yet."));
        }

        var lines = agents.Select(agent =>
            $"• `{agent.AgentId}` — {agent.Name} owner={agent.Owner} trust={agent.TrustScore}");
        return Ok(Ephemeral(string.Join('\n', lines)));
    }

    private async Task<IActionResult> Approvals()
    {
        var approvals = await _approvalStore.ListPendingAsync(HttpContext.RequestAborted);
        if (approvals.Count == 0)
        {
            return Ok(Ephemeral("No pending Compass approvals."));
        }

        var lines = approvals.Select(approval =>
            $"• `{approval.RequestId}` agent={approval.AgentId} tool={approval.ToolName}");
        return Ok(Ephemeral(string.Join('\n', lines)));
    }

    private IActionResult Trust()
    {
        var scores = _trustStore.GetAllScores();
        if (scores.Count == 0)
        {
            return Ok(Ephemeral("No trust scores recorded yet."));
        }

        var lines = scores
            .OrderByDescending(pair => pair.Value)
            .Take(20)
            .Select(pair => $"• `{pair.Key}` — {pair.Value:0}");

        return Ok(Ephemeral(string.Join('\n', lines)));
    }

    private static object Ephemeral(string text)
    {
        return new
        {
            response_type = "ephemeral",
            text
        };
    }
}
