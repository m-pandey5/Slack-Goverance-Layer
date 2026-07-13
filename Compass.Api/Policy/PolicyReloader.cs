using AgentGovernance;

namespace Compass.Api.Policy;

/// <summary>
/// Wraps GovernanceKernel.LoadPolicy to provide hot reload across all policy files.
/// </summary>
public sealed class PolicyReloader
{
    private readonly GovernanceKernel _kernel;
    private readonly string[] _policyPaths;
    private readonly ILogger<PolicyReloader> _logger;

    public PolicyReloader(
        GovernanceKernel kernel,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger<PolicyReloader> logger)
    {
        _kernel = kernel;
        _logger = logger;

        var dir = ResolvePolicyDirectory(environment.ContentRootPath);
        _policyPaths =
        [
            Path.Combine(dir, "slack-events.yaml"),
            Path.Combine(dir, "mcp-tools.yaml"),
            Path.Combine(dir, "slack-api.yaml")
        ];
    }

    public IReadOnlyList<string> PolicyPaths => _policyPaths;

    public void Reload()
    {
        foreach (var path in _policyPaths)
        {
            if (!File.Exists(path))
            {
                _logger.LogWarning("[policy-reloader] Policy file not found: {Path}", path);
                continue;
            }

            _kernel.LoadPolicy(path);
            _logger.LogInformation("[policy-reloader] Reloaded {Path}", path);
        }
    }

    private static string ResolvePolicyDirectory(string contentRootPath)
    {
        var candidates = new[]
        {
            Path.Combine(contentRootPath, "policies"),
            Path.Combine(AppContext.BaseDirectory, "policies")
        };

        return candidates.FirstOrDefault(Directory.Exists)
               ?? Path.Combine(contentRootPath, "policies");
    }
}
