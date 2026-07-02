using AgentGovernance.Audit;

namespace Compass.Api.Audit;

public interface ICompassAuditSink
{
    Task AppendAsync(GovernanceEvent governanceEvent, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CompassAuditRecord>> ReadRecentAsync(int count, CancellationToken cancellationToken = default);
}
