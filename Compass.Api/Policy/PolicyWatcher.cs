using Compass.Api.Services;

namespace Compass.Api.Policy;

/// <summary>
/// Watches the policies directory for YAML changes and reloads the GovernanceKernel.
/// TC-06: `/compass policy reload` triggers this via the reload endpoint.
/// </summary>
public sealed class PolicyWatcher : BackgroundService
{
    private readonly PolicyReloader _reloader;
    private readonly SlackWebClient _slackWebClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PolicyWatcher> _logger;

    private FileSystemWatcher? _watcher;
    private int _reloadCount;

    public PolicyWatcher(
        PolicyReloader reloader,
        SlackWebClient slackWebClient,
        IConfiguration configuration,
        ILogger<PolicyWatcher> logger)
    {
        _reloader = reloader;
        _slackWebClient = slackWebClient;
        _configuration = configuration;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var policyDir = _configuration["Governance:PolicyDirectory"]
                        ?? ResolvePolicyDirectory(_reloader.PolicyPaths);

        if (!Directory.Exists(policyDir))
        {
            _logger.LogWarning("[policy-watcher] Policy directory not found: {Dir}", policyDir);
            return Task.CompletedTask;
        }

        _watcher = new FileSystemWatcher(policyDir, "*.yaml")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };

        _watcher.Changed += OnPolicyChanged;
        _watcher.Created += OnPolicyChanged;

        _logger.LogInformation("[policy-watcher] Watching {Dir} for policy changes.", policyDir);

        stoppingToken.Register(() => _watcher.EnableRaisingEvents = false);
        return Task.CompletedTask;
    }

    private void OnPolicyChanged(object sender, FileSystemEventArgs e)
    {
        _logger.LogInformation("[policy-watcher] Policy file changed: {File}", e.Name);

        // GovernanceKernel.ReloadPolicies() — call if available in the AGT version you have.
        // The kernel was constructed with file paths; the reload reads those same files.
        try
        {
            _reloader.Reload();
            var count = Interlocked.Increment(ref _reloadCount);
            _logger.LogInformation("[policy-watcher] Policies reloaded (reload #{Count}).", count);

            var alertsChannel = _configuration["Slack:AlertsChannel"];
            if (!string.IsNullOrWhiteSpace(alertsChannel))
            {
                _ = _slackWebClient.PostMessageAsync(
                    alertsChannel,
                    $":arrows_counterclockwise: *Compass policy hot-reloaded* — `{e.Name}` changed (reload #{count}).");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[policy-watcher] Failed to reload policies after {File} changed.", e.Name);
        }
    }

    private static string ResolvePolicyDirectory(IReadOnlyList<string> policyPaths)
    {
        return policyPaths.Count > 0
            ? Path.GetDirectoryName(policyPaths[0]) ?? AppContext.BaseDirectory
            : AppContext.BaseDirectory;
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        base.Dispose();
    }
}
