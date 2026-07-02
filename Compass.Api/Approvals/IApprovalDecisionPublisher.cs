namespace Compass.Api.Approvals;

public interface IApprovalDecisionPublisher
{
    Task PublishAsync(ApprovalDecisionMessage message, CancellationToken cancellationToken = default);
}
