using System.Text.Json;
using AgentGovernance.Audit;

namespace Compass.Api.Audit;

public sealed class FileCompassAuditSink : ICompassAuditSink
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _filePath;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public FileCompassAuditSink(IWebHostEnvironment environment, IConfiguration configuration)
    {
        _filePath = configuration["Audit:FilePath"]
            ?? Path.Combine(environment.ContentRootPath, "data", "audit.jsonl");
    }

    public async Task AppendAsync(GovernanceEvent governanceEvent, CancellationToken cancellationToken = default)
    {
        var record = new CompassAuditRecord
        {
            EventId = governanceEvent.EventId,
            Type = governanceEvent.Type.ToString(),
            AgentId = governanceEvent.AgentId,
            SessionId = governanceEvent.SessionId,
            PolicyName = governanceEvent.PolicyName,
            Timestamp = governanceEvent.Timestamp,
            Data = governanceEvent.Data
        };

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath) ?? ".");
            await File.AppendAllTextAsync(
                _filePath,
                JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine,
                cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<IReadOnlyList<CompassAuditRecord>> ReadRecentAsync(int count, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        var lines = await File.ReadAllLinesAsync(_filePath, cancellationToken);
        return lines
            .Reverse()
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Take(Math.Clamp(count, 1, 500))
            .Select(line => JsonSerializer.Deserialize<CompassAuditRecord>(line, JsonOptions))
            .OfType<CompassAuditRecord>()
            .ToList();
    }
}
