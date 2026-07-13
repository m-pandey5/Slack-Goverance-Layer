using AgentGovernance;
using AgentGovernance.Mcp;
using AgentGovernance.Policy;
using AgentGovernance.Trust;
using Compass.Api.Agents;
using Compass.Api.Audit;
using Compass.Api.Approvals;
using Compass.Api.Policy;
using Compass.Api.Risk;
using Compass.Api.Services;
using Compass.Api.Mcp;
using Compass.Api.Security;
using Compass.Api.SRE;
using Compass.Api.TokenStore;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

var policyDirectory = ResolvePolicyDirectory(builder.Environment.ContentRootPath);

builder.Services.AddControllers();
builder.Services.AddDataProtection();

// Core infrastructure
builder.Services.AddSingleton<SlackRequestVerifier>();
builder.Services.AddSingleton<ICompassAuditSink, FileCompassAuditSink>();
builder.Services.AddSingleton<TrustScoreEventHandler>();
builder.Services.AddSingleton<IApprovalStore, FileApprovalStore>();
builder.Services.AddSingleton<IApprovalExecutor, ApprovalExecutor>();
builder.Services.AddSingleton<IMcpForwarder, SlackMcpForwarder>();

// SRE — circuit breaker
builder.Services.AddSingleton<ICircuitBreaker, InMemoryCircuitBreaker>();

// Risk — AIVSS scorer
builder.Services.AddSingleton<AivssScorer>();

// Policy hot reload
builder.Services.AddSingleton<PolicyReloader>();

// Trust store
builder.Services.AddSingleton(provider =>
{
    var environment = provider.GetRequiredService<IWebHostEnvironment>();
    var trustDirectory = Path.Combine(environment.ContentRootPath, "trust");
    Directory.CreateDirectory(trustDirectory);
    return new FileTrustStore(
        Path.Combine(trustDirectory, "scores.json"),
        defaultScore: 500,
        allowedBaseDirectory: trustDirectory);
});

// Governance kernel — singleton so PolicyWatcher can call ReloadPolicies()
builder.Services.AddSingleton(_ => new GovernanceKernel(new GovernanceOptions
{
    PolicyPaths =
    [
        Path.Combine(policyDirectory, "slack-events.yaml"),
        Path.Combine(policyDirectory, "mcp-tools.yaml"),
        Path.Combine(policyDirectory, "slack-api.yaml")
    ],
    ConflictStrategy = ConflictResolutionStrategy.PriorityFirstMatch,
    EnablePromptInjectionDetection = true,
    EnableCircuitBreaker = true
}));

builder.Services.AddSingleton(_ => new McpGateway(new McpGatewayConfig
{
    DenyList = ["admin.*", "admin*", "files.delete"],
    ApprovalRequiredTools = ["conversations.archive", "conversations.kick", "files.delete"],
    BlockOnSuspiciousPayload = true
}));

builder.Services.AddSingleton<McpResponseSanitizer>();
builder.Services.AddHttpClient("slack-mcp");
builder.Services.AddHttpClient("slack-api");
builder.Services.AddHttpClient<SlackWebClient>();

// Approval pipeline
var serviceBusConnectionString = builder.Configuration["ServiceBus:ConnectionString"];
if (!string.IsNullOrWhiteSpace(serviceBusConnectionString))
{
    builder.Services.AddSingleton<IApprovalDecisionPublisher, ServiceBusApprovalDecisionPublisher>();
    builder.Services.AddHostedService<ServiceBusApprovalDecisionWorker>();
}
else
{
    builder.Services.AddSingleton<IApprovalDecisionPublisher, LocalApprovalDecisionPublisher>();
}

// Agent registry + token store
var redisConnectionString = builder.Configuration["ProxyTokens:RedisConnectionString"];
if (!string.IsNullOrWhiteSpace(redisConnectionString))
{
    builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
        ConnectionMultiplexer.Connect(redisConnectionString));
    builder.Services.AddSingleton<IProxyTokenStore, RedisEncryptedProxyTokenStore>();
    builder.Services.AddSingleton<IAgentRegistry, RedisAgentRegistry>();
}
else
{
    builder.Services.AddSingleton<IProxyTokenStore, InMemoryProxyTokenStore>();
    builder.Services.AddSingleton<IAgentRegistry, FileAgentRegistry>();
}

builder.Services.AddHostedService<ProxyTokenSeeder>();
builder.Services.AddHostedService<AgentRegistrySeeder>();
builder.Services.AddHostedService<PolicyWatcher>();

var app = builder.Build();

// Wire governance event pipeline
var kernel = app.Services.GetRequiredService<GovernanceKernel>();
var auditSink = app.Services.GetRequiredService<ICompassAuditSink>();
var trustHandler = app.Services.GetRequiredService<TrustScoreEventHandler>();
kernel.OnAllEvents(evt =>
{
    app.Logger.LogInformation(
        "[governance] type={Type} agent={AgentId} policy={Policy}",
        evt.Type,
        evt.AgentId,
        evt.PolicyName ?? "(none)");
    trustHandler.Handle(evt);
    _ = auditSink.AppendAsync(evt);
});

app.MapControllers();
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.Run();

static string ResolvePolicyDirectory(string contentRootPath)
{
    var candidates = new[]
    {
        Path.Combine(contentRootPath, "policies"),
        Path.Combine(contentRootPath, "Compass.Api", "policies"),
        Path.Combine(AppContext.BaseDirectory, "policies")
    };

    return candidates.FirstOrDefault(Directory.Exists)
        ?? throw new DirectoryNotFoundException("Could not find the Compass policies directory.");
}

public partial class Program;
