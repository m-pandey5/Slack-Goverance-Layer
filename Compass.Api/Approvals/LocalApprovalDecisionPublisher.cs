namespace Compass.Api.Approvals;

public sealed class LocalApprovalDecisionPublisher : IApprovalDecisionPublisher
{
    private readonly IApprovalExecutor _executor;

    public LocalApprovalDecisionPublisher(IApprovalExecutor executor)
    {
        _executor = executor;
    }

    public Task PublishAsync(ApprovalDecisionMessage message, CancellationToken cancellationToken = default)
    {
        return _executor.ExecuteDecisionAsync(message, cancellationToken);
    }
}
