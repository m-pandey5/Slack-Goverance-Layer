using AgentGovernance.Trust;
using Compass.Api.Agents;
using Compass.Api.Approvals;
using Compass.Api.Audit;
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

    public AdminController(
        IAgentRegistry agentRegistry,
        IApprovalStore approvalStore,
        ICompassAuditSink auditSink,
        FileTrustStore trustStore)
    {
        _agentRegistry = agentRegistry;
        _approvalStore = approvalStore;
        _auditSink = auditSink;
        _trustStore = trustStore;
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
            trust_scores = _trustStore.GetAllScores()
        });
    }

    [HttpGet("agents")]
    public async Task<IActionResult> Agents(CancellationToken cancellationToken)
    {
        return Ok(await _agentRegistry.ListAgentsAsync(cancellationToken));
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
}
