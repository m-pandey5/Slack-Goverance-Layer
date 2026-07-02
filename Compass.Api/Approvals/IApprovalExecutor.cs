namespace Compass.Api.Approvals;

public interface IApprovalExecutor
{
    Task<ApprovalExecutionResult> ExecuteDecisionAsync(
        ApprovalDecisionMessage message,
        CancellationToken cancellationToken = default);
}
