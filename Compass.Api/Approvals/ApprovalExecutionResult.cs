namespace Compass.Api.Approvals;

public sealed class ApprovalExecutionResult
{
    public required string RequestId { get; init; }

    public required string Status { get; init; }

    public string? ResponseBody { get; init; }

    public string? Error { get; init; }
}
