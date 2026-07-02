using System.Text.Json;

namespace Compass.Api.Approvals;

public sealed class FileApprovalStore : IApprovalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public FileApprovalStore(IWebHostEnvironment environment, IConfiguration configuration)
    {
        _filePath = configuration["Approvals:FilePath"]
            ?? Path.Combine(environment.ContentRootPath, "data", "approvals.json");
    }

    public async Task<ApprovalRequest> CreateAsync(
        string agentId,
        string toolName,
        string payload,
        string? requestedChannel = null,
        string? authorizationHeader = null,
        CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var approvals = await ReadCoreAsync(cancellationToken);
            var approval = new ApprovalRequest
            {
                RequestId = $"apr_{Guid.NewGuid():N}"[..16],
                AgentId = agentId,
                ToolName = toolName,
                Payload = payload,
                RequestedChannel = requestedChannel,
                AuthorizationHeader = authorizationHeader,
                Status = "pending"
            };
            approvals[approval.RequestId] = approval;
            await WriteCoreAsync(approvals, cancellationToken);
            return approval;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<ApprovalRequest?> GetAsync(string requestId, CancellationToken cancellationToken = default)
    {
        var approvals = await ReadAsync(cancellationToken);
        return approvals.GetValueOrDefault(requestId);
    }

    public async Task<IReadOnlyList<ApprovalRequest>> ListPendingAsync(CancellationToken cancellationToken = default)
    {
        var approvals = await ReadAsync(cancellationToken);
        return approvals.Values
            .Where(approval => string.Equals(approval.Status, "pending", StringComparison.Ordinal))
            .OrderBy(approval => approval.CreatedAt)
            .ToList();
    }

    public async Task<ApprovalRequest?> DecideAsync(
        string requestId,
        string decision,
        string decidedBy,
        CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var approvals = await ReadCoreAsync(cancellationToken);
            if (!approvals.TryGetValue(requestId, out var approval))
            {
                return null;
            }

            approval.Status = decision;
            approval.DecisionBy = decidedBy;
            approval.DecidedAt = DateTimeOffset.UtcNow;
            await WriteCoreAsync(approvals, cancellationToken);
            return approval;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<ApprovalRequest?> MarkExecutionAsync(
        string requestId,
        string status,
        string? responseBody,
        string? error,
        CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var approvals = await ReadCoreAsync(cancellationToken);
            if (!approvals.TryGetValue(requestId, out var approval))
            {
                return null;
            }

            approval.Status = status;
            approval.ExecutedAt = DateTimeOffset.UtcNow;
            approval.ExecutionResponse = responseBody;
            approval.ExecutionError = error;
            await WriteCoreAsync(approvals, cancellationToken);
            return approval;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task<Dictionary<string, ApprovalRequest>> ReadAsync(CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            return await ReadCoreAsync(cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task<Dictionary<string, ApprovalRequest>> ReadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return new Dictionary<string, ApprovalRequest>(StringComparer.Ordinal);
        }

        var json = await File.ReadAllTextAsync(_filePath, cancellationToken);
        return JsonSerializer.Deserialize<Dictionary<string, ApprovalRequest>>(json, JsonOptions)
               ?? new Dictionary<string, ApprovalRequest>(StringComparer.Ordinal);
    }

    private async Task WriteCoreAsync(Dictionary<string, ApprovalRequest> approvals, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath) ?? ".");
        var json = JsonSerializer.Serialize(approvals, JsonOptions);
        await File.WriteAllTextAsync(_filePath, json, cancellationToken);
    }
}
