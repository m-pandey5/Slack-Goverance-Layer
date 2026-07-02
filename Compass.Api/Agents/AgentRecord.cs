namespace Compass.Api.Agents;

public sealed class AgentRecord
{
    public required string AgentId { get; init; }

    public required string Name { get; init; }

    public required string Owner { get; init; }

    public string Workspace { get; init; } = "default";

    public string? ContactEmail { get; init; }

    public double TrustScore { get; init; } = 500;

    public List<string> AllowedTools { get; init; } = [];

    public List<string> BlockedTools { get; init; } = [];

    public bool Revoked { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
