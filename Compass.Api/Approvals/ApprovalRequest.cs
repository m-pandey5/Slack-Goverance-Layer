namespace Compass.Api.Approvals;

public sealed class ApprovalRequest
{
    public required string RequestId { get; init; }

    public required string AgentId { get; init; }

    public required string ToolName { get; init; }

    public required string Payload { get; init; }

    public required string Status { get; set; }

    public string? RequestedChannel { get; init; }

    public string? AuthorizationHeader { get; init; }

    public string? DecisionBy { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? DecidedAt { get; set; }

    public DateTimeOffset? ExecutedAt { get; set; }

    public string? ExecutionResponse { get; set; }

    public string? ExecutionError { get; set; }
}
