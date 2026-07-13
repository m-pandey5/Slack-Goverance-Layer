using AgentGovernance.Trust;
using Compass.Api.Agents;
using Compass.Api.Approvals;
using Compass.Api.Audit;
using Compass.Api.Policy;
using Compass.Api.Services;
using Compass.Api.SRE;
using Microsoft.AspNetCore.Mvc;

namespace Compass.Api.Controllers;

[ApiController]
[Route("admin")]
public sealed class AdminController : ControllerBase
{
    private readonly IAgentRegistry _agentRegistry;
    private readonly IApprovalStore _approvalStore;
    private readonly ICompassAuditSink _auditSink;
    private readonly FileTrustStore _trustStore;
    private readonly ICircuitBreaker _circuitBreaker;
    private readonly PolicyReloader _policyReloader;
    private readonly SlackWebClient _slackWebClient;
    private readonly IConfiguration _configuration;

    public AdminController(
        IAgentRegistry agentRegistry,
        IApprovalStore approvalStore,
        ICompassAuditSink auditSink,
        FileTrustStore trustStore,
        ICircuitBreaker circuitBreaker,
        PolicyReloader policyReloader,
        SlackWebClient slackWebClient,
        IConfiguration configuration)
    {
        _agentRegistry = agentRegistry;
        _approvalStore = approvalStore;
        _auditSink = auditSink;
        _trustStore = trustStore;
        _circuitBreaker = circuitBreaker;
        _policyReloader = policyReloader;
        _slackWebClient = slackWebClient;
        _configuration = configuration;
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken cancellationToken)
    {
        var agents = await _agentRegistry.ListAgentsAsync(cancellationToken);
        var approvals = await _approvalStore.ListPendingAsync(cancellationToken);
        return Ok(new
        {
            status = "ok",
            agents = agents.Count,
            pending_approvals = approvals.Count,
            trust_scores = _trustStore.GetAllScores(),
            circuit_breakers = _circuitBreaker.GetAllStatuses()
        });
    }

    [HttpGet("agents")]
    public async Task<IActionResult> Agents(CancellationToken cancellationToken)
    {
        return Ok(await _agentRegistry.ListAgentsAsync(cancellationToken));
    }

    [HttpPost("agents/{agentId}/suspend")]
    public async Task<IActionResult> SuspendAgent(string agentId, CancellationToken cancellationToken)
    {
        var agent = await _agentRegistry.GetAgentAsync(agentId, cancellationToken);
        if (agent is null)
        {
            return NotFound(new { error = "agent_not_found", agent_id = agentId });
        }

        await _agentRegistry.SuspendAgentAsync(agentId, cancellationToken);

        var alertsChannel = _configuration["Slack:AlertsChannel"];
        if (!string.IsNullOrWhiteSpace(alertsChannel))
        {
            await _slackWebClient.PostMessageAsync(
                alertsChannel,
                $":no_entry: *Compass kill-switch activated* — agent `{agentId}` ({agent.Name}) suspended by admin.",
                cancellationToken: cancellationToken);
        }

        return Ok(new
        {
            status = "suspended",
            agent_id = agentId,
            name = agent.Name
        });
    }

    [HttpPost("agents/{agentId}/reinstate")]
    public async Task<IActionResult> ReinstateAgent(string agentId, CancellationToken cancellationToken)
    {
        var agent = await _agentRegistry.GetAgentAsync(agentId, cancellationToken);
        if (agent is null)
        {
            return NotFound(new { error = "agent_not_found", agent_id = agentId });
        }

        // Re-register with Revoked = false (same proxy token path not available here; just update record)
        await _agentRegistry.RegisterAgentAsync(
            $"reinstated-{agentId}",
            agent with { Revoked = false },
            cancellationToken);

        return Ok(new { status = "reinstated", agent_id = agentId });
    }

    [HttpGet("approvals")]
    public async Task<IActionResult> Approvals(CancellationToken cancellationToken)
    {
        return Ok(await _approvalStore.ListPendingAsync(cancellationToken));
    }

    [HttpGet("audit")]
    public async Task<IActionResult> Audit([FromQuery] int count = 50, CancellationToken cancellationToken = default)
    {
        return Ok(await _auditSink.ReadRecentAsync(count, cancellationToken));
    }

    [HttpGet("circuit-breakers")]
    public IActionResult CircuitBreakers()
    {
        return Ok(_circuitBreaker.GetAllStatuses());
    }

    [HttpPost("circuit-breakers/{agentId}/reset")]
    public IActionResult ResetCircuitBreaker(string agentId)
    {
        _circuitBreaker.RecordSuccess(agentId);
        return Ok(new { status = "reset", agent_id = agentId });
    }

    [HttpPost("policy/reload")]
    public async Task<IActionResult> ReloadPolicies(CancellationToken cancellationToken)
    {
        try
        {
            _policyReloader.Reload();

            var alertsChannel = _configuration["Slack:AlertsChannel"];
            if (!string.IsNullOrWhiteSpace(alertsChannel))
            {
                await _slackWebClient.PostMessageAsync(
                    alertsChannel,
                    ":arrows_counterclockwise: *Compass policies reloaded* via admin API.",
                    cancellationToken: cancellationToken);
            }

            return Ok(new { status = "reloaded" });
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                error = "reload_failed",
                message = ex.Message
            });
        }
    }
}
