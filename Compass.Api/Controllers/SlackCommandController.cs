using System.Text;
using AgentGovernance.Trust;
using Compass.Api.Agents;
using Compass.Api.Approvals;
using Compass.Api.Policy;
using Compass.Api.Security;
using Compass.Api.Services;
using Compass.Api.SRE;
using Microsoft.AspNetCore.Mvc;

namespace Compass.Api.Controllers;

[ApiController]
public sealed class SlackCommandController : ControllerBase
{
    private readonly SlackRequestVerifier _verifier;
    private readonly IAgentRegistry _agentRegistry;
    private readonly IApprovalStore _approvalStore;
    private readonly FileTrustStore _trustStore;
    private readonly ICircuitBreaker _circuitBreaker;
    private readonly PolicyReloader _policyReloader;
    private readonly SlackWebClient _slackWebClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SlackCommandController> _logger;

    public SlackCommandController(
        SlackRequestVerifier verifier,
        IAgentRegistry agentRegistry,
        IApprovalStore approvalStore,
        FileTrustStore trustStore,
        ICircuitBreaker circuitBreaker,
        PolicyReloader policyReloader,
        SlackWebClient slackWebClient,
        IConfiguration configuration,
        ILogger<SlackCommandController> logger)
    {
        _verifier = verifier;
        _agentRegistry = agentRegistry;
        _approvalStore = approvalStore;
        _trustStore = trustStore;
        _circuitBreaker = circuitBreaker;
        _policyReloader = policyReloader;
        _slackWebClient = slackWebClient;
        _configuration = configuration;
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
        var fullText = form.TryGetValue("text", out var value) ? value.ToString().Trim() : "";
        var parts = fullText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var command = parts.Length > 0 ? parts[0].ToLowerInvariant() : "status";
        var arg1 = parts.Length > 1 ? parts[1] : "";
        var arg2 = parts.Length > 2 ? parts[2] : "";

        return command switch
        {
            "status" => await Status(),
            "agents" => await Agents(),
            "approvals" => await Approvals(),
            "trust" => Trust(),
            "circuit" => Circuit(),
            "suspend" => await Suspend(arg1),
            "policy" when arg2 == "reload" || arg1 == "reload" => await PolicyReload(),
            _ => Ok(Ephemeral(
                "Commands: `/compass status` · `agents` · `approvals` · `trust` · `circuit` · `suspend <agent-id>` · `policy reload`"))
        };
    }

    private async Task<IActionResult> Status()
    {
        var agents = await _agentRegistry.ListAgentsAsync(HttpContext.RequestAborted);
        var approvals = await _approvalStore.ListPendingAsync(HttpContext.RequestAborted);
        var openCircuits = _circuitBreaker.GetAllStatuses().Values.Count(s => s.IsOpen);
        return Ok(Ephemeral(
            $"Compass is online. Agents: {agents.Count} | Pending approvals: {approvals.Count} | Open circuits: {openCircuits}"));
    }

    private async Task<IActionResult> Agents()
    {
        var agents = await _agentRegistry.ListAgentsAsync(HttpContext.RequestAborted);
        if (agents.Count == 0)
        {
            return Ok(Ephemeral("No registered Compass agents yet."));
        }

        var lines = agents.Select(agent =>
            $"• `{agent.AgentId}` — {agent.Name} ring={agent.Ring} trust={agent.TrustScore:0} {(agent.Revoked ? "🚫 SUSPENDED" : "")}");
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

    private IActionResult Circuit()
    {
        var statuses = _circuitBreaker.GetAllStatuses();
        if (statuses.Count == 0)
        {
            return Ok(Ephemeral("No circuit breaker state yet."));
        }

        var lines = statuses.Select(kvp =>
        {
            var s = kvp.Value;
            var icon = s.IsOpen ? ":red_circle: OPEN" : ":large_green_circle: CLOSED";
            var reset = s.ResetAt.HasValue ? $" resets <t:{s.ResetAt.Value.ToUnixTimeSeconds()}:R>" : "";
            return $"• `{kvp.Key}` — {icon} ({s.FailureCount} failures){reset}";
        });

        return Ok(Ephemeral(string.Join('\n', lines)));
    }

    private async Task<IActionResult> Suspend(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return Ok(Ephemeral("Usage: `/compass suspend <agent-id>`"));
        }

        var agent = await _agentRegistry.GetAgentAsync(agentId, HttpContext.RequestAborted);
        if (agent is null)
        {
            return Ok(Ephemeral($"Agent `{agentId}` not found."));
        }

        await _agentRegistry.SuspendAgentAsync(agentId, HttpContext.RequestAborted);

        var alertsChannel = _configuration["Slack:AlertsChannel"];
        if (!string.IsNullOrWhiteSpace(alertsChannel))
        {
            await _slackWebClient.PostMessageAsync(
                alertsChannel,
                $":no_entry: *Compass kill-switch* — agent `{agentId}` ({agent.Name}) suspended via slash command.",
                cancellationToken: HttpContext.RequestAborted);
        }

        _logger.LogWarning("[kill-switch] Agent suspended via slash command agent={AgentId}", agentId);
        return Ok(Ephemeral($":no_entry: Agent `{agentId}` ({agent.Name}) has been suspended. All future calls will be blocked."));
    }

    private async Task<IActionResult> PolicyReload()
    {
        try
        {
            _policyReloader.Reload();

            var alertsChannel = _configuration["Slack:AlertsChannel"];
            if (!string.IsNullOrWhiteSpace(alertsChannel))
            {
                await _slackWebClient.PostMessageAsync(
                    alertsChannel,
                    ":arrows_counterclockwise: *Compass policies reloaded* via `/compass policy reload`.",
                    cancellationToken: HttpContext.RequestAborted);
            }

            return Ok(Ephemeral(":white_check_mark: Compass policies reloaded successfully."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Policy reload failed via slash command.");
            return Ok(Ephemeral($":x: Policy reload failed: {ex.Message}"));
        }
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
