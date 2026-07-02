namespace Compass.Api.Mcp;

public interface IMcpForwarder
{
    Task<McpForwardResult> ForwardAsync(
        string payload,
        string? authorizationHeader = null,
        CancellationToken cancellationToken = default);
}
