namespace Compass.Api.Mcp;

public sealed class McpForwardResult
{
    public required int StatusCode { get; init; }

    public required string Body { get; init; }

    public string ContentType { get; init; } = "application/json";

    public bool Succeeded => StatusCode is >= 200 and < 300;
}
