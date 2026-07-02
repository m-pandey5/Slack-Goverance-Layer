namespace Compass.Api.Audit;

public sealed class CompassAuditRecord
{
    public required string EventId { get; init; }

    public required string Type { get; init; }

    public required string AgentId { get; init; }

    public required string SessionId { get; init; }

    public string? PolicyName { get; init; }

    public DateTimeOffset Timestamp { get; init; }

    public Dictionary<string, object> Data { get; init; } = new();
}
