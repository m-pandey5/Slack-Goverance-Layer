using AgentGovernance;
using AgentGovernance.Mcp;
using AgentGovernance.Policy;
using Compass.Api.Security;

var builder = WebApplication.CreateBuilder(args);

var policyDirectory = ResolvePolicyDirectory(builder.Environment.ContentRootPath);

builder.Services.AddControllers();
builder.Services.AddSingleton<SlackRequestVerifier>();

builder.Services.AddSingleton(_ => new GovernanceKernel(new GovernanceOptions
{
    PolicyPaths =
    [
        Path.Combine(policyDirectory, "slack-events.yaml"),
        Path.Combine(policyDirectory, "mcp-tools.yaml")
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

var app = builder.Build();

var kernel = app.Services.GetRequiredService<GovernanceKernel>();
kernel.OnAllEvents(evt =>
    app.Logger.LogInformation(
        "[governance] type={Type} agent={AgentId} policy={Policy}",
        evt.Type,
        evt.AgentId,
        evt.PolicyName ?? "(none)"));

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
