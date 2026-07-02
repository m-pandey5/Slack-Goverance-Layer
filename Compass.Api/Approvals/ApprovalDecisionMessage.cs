namespace Compass.Api.Approvals;

public sealed class ApprovalDecisionMessage
{
    public required string RequestId { get; init; }

    public required string Decision { get; init; }

    public required string DecidedBy { get; init; }

    public DateTimeOffset DecidedAt { get; init; } = DateTimeOffset.UtcNow;
}
