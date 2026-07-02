using Compass.Api.Mcp;
using Compass.Api.Services;

namespace Compass.Api.Approvals;

public sealed class ApprovalExecutor : IApprovalExecutor
{
    private readonly IApprovalStore _approvalStore;
    private readonly IMcpForwarder _mcpForwarder;
    private readonly SlackWebClient _slackWebClient;
    private readonly ILogger<ApprovalExecutor> _logger;

    public ApprovalExecutor(
        IApprovalStore approvalStore,
        IMcpForwarder mcpForwarder,
        SlackWebClient slackWebClient,
        ILogger<ApprovalExecutor> logger)
    {
        _approvalStore = approvalStore;
        _mcpForwarder = mcpForwarder;
        _slackWebClient = slackWebClient;
        _logger = logger;
    }

    public async Task<ApprovalExecutionResult> ExecuteDecisionAsync(
        ApprovalDecisionMessage message,
        CancellationToken cancellationToken = default)
    {
        var approval = await _approvalStore.GetAsync(message.RequestId, cancellationToken);
        if (approval is null)
        {
            return new ApprovalExecutionResult
            {
                RequestId = message.RequestId,
                Status = "not_found",
                Error = "approval_request_not_found"
            };
        }

        if (approval.Status is "executed" or "failed" or "denied")
        {
            return new ApprovalExecutionResult
            {
                RequestId = message.RequestId,
                Status = approval.Status,
                Error = $"approval_already_{approval.Status}"
            };
        }

        if (!string.Equals(message.Decision, "approved", StringComparison.Ordinal))
        {
            await _approvalStore.MarkExecutionAsync(message.RequestId, "denied", null, null, cancellationToken);
            return new ApprovalExecutionResult
            {
                RequestId = message.RequestId,
                Status = "denied"
            };
        }

        try
        {
            var result = await _mcpForwarder.ForwardAsync(
                approval.Payload,
                approval.AuthorizationHeader,
                cancellationToken);

            var status = result.Succeeded ? "executed" : "failed";
            await _approvalStore.MarkExecutionAsync(
                message.RequestId,
                status,
                result.Body,
                result.Succeeded ? null : $"slack_mcp_status_{result.StatusCode}",
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(approval.RequestedChannel))
            {
                await _slackWebClient.PostMessageAsync(
                    approval.RequestedChannel,
                    $"Compass approval `{message.RequestId}` {status} for `{approval.ToolName}`.",
                    cancellationToken: cancellationToken);
            }

            return new ApprovalExecutionResult
            {
                RequestId = message.RequestId,
                Status = status,
                ResponseBody = result.Body,
                Error = result.Succeeded ? null : $"slack_mcp_status_{result.StatusCode}"
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Approval replay failed request={RequestId}", message.RequestId);
            await _approvalStore.MarkExecutionAsync(
                message.RequestId,
                "failed",
                null,
                ex.Message,
                cancellationToken);

            return new ApprovalExecutionResult
            {
                RequestId = message.RequestId,
                Status = "failed",
                Error = ex.Message
            };
        }
    }
}
