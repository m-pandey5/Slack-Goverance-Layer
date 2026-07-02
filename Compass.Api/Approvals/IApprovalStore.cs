namespace Compass.Api.Approvals;

public interface IApprovalStore
{
    Task<ApprovalRequest> CreateAsync(
        string agentId,
        string toolName,
        string payload,
        string? requestedChannel = null,
        string? authorizationHeader = null,
        CancellationToken cancellationToken = default);

    Task<ApprovalRequest?> GetAsync(string requestId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ApprovalRequest>> ListPendingAsync(CancellationToken cancellationToken = default);

    Task<ApprovalRequest?> DecideAsync(
        string requestId,
        string decision,
        string decidedBy,
        CancellationToken cancellationToken = default);

    Task<ApprovalRequest?> MarkExecutionAsync(
        string requestId,
        string status,
        string? responseBody,
        string? error,
        CancellationToken cancellationToken = default);
}
