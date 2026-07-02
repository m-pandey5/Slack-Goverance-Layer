using AgentGovernance.Audit;
using AgentGovernance.Trust;

namespace Compass.Api.Audit;

public sealed class TrustScoreEventHandler
{
    private readonly FileTrustStore _trustStore;
    private readonly ILogger<TrustScoreEventHandler> _logger;

    public TrustScoreEventHandler(FileTrustStore trustStore, ILogger<TrustScoreEventHandler> logger)
    {
        _trustStore = trustStore;
        _logger = logger;
    }

    public void Handle(GovernanceEvent governanceEvent)
    {
        switch (governanceEvent.Type)
        {
            case GovernanceEventType.ToolCallBlocked:
            case GovernanceEventType.PolicyViolation:
            case GovernanceEventType.TrustFailed:
                _trustStore.RecordNegativeSignal(governanceEvent.AgentId, 25);
                _logger.LogInformation(
                    "Trust score lowered for {AgentId}; score={Score}",
                    governanceEvent.AgentId,
                    _trustStore.GetScore(governanceEvent.AgentId));
                break;

            case GovernanceEventType.PolicyCheck:
                _trustStore.RecordPositiveSignal(governanceEvent.AgentId, 1);
                break;
        }
    }
}
