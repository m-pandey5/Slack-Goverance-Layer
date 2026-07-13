namespace Compass.Api.SRE;

public sealed class CircuitBreakerStatus
{
    public bool IsOpen { get; init; }
    public int FailureCount { get; init; }
    public DateTimeOffset? OpenedAt { get; init; }
    public DateTimeOffset? ResetAt { get; init; }
}
