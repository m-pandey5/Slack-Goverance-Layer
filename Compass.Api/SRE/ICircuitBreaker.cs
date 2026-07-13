namespace Compass.Api.SRE;

public interface ICircuitBreaker
{
    bool IsOpen(string agentId);
    void RecordFailure(string agentId);
    void RecordSuccess(string agentId);
    CircuitBreakerStatus GetStatus(string agentId);
    IReadOnlyDictionary<string, CircuitBreakerStatus> GetAllStatuses();
}
