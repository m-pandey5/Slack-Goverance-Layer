namespace Compass.Api.Agents;

public sealed record AgentRecord
{
    public required string AgentId { get; init; }

    public required string Name { get; init; }

    public required string Owner { get; init; }

    public string Workspace { get; init; } = "default";

    public string? ContactEmail { get; init; }

    public double TrustScore { get; init; } = 500;

    /// <summary>
    /// Ring 0 = core/system, 1 = trusted, 2 = standard (default), 3 = untrusted/external
    /// </summary>
    public int Ring { get; init; } = 2;

    public List<string> AllowedTools { get; init; } = [];

    public List<string> BlockedTools { get; init; } = [];

    public bool Revoked { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
