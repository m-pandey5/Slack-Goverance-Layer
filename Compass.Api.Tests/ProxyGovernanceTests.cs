using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Compass.Api.Tests;

public sealed class ProxyGovernanceTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ProxyGovernanceTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Audit:FilePath", Path.Combine(Path.GetTempPath(), $"compass-audit-{Guid.NewGuid():N}.jsonl"));
            builder.UseSetting("Agents:FilePath", Path.Combine(Path.GetTempPath(), $"compass-agents-{Guid.NewGuid():N}.json"));
        });
    }

    [Fact]
    public async Task McpToolsCall_DestructiveTool_ReturnsApprovalRequired()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsync(
            "/mcp",
            JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = "req-1",
                method = "tools/call",
                @params = new
                {
                    name = "conversations.archive",
                    arguments = new { channel = "C_TEST" }
                }
            }));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("approval_required", document.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task SlackApiProxy_DeniedMethod_ReturnsSlackStyleError()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/files.delete")
        {
            Content = new StringContent("""{"file":"F_TEST"}""", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new("Bearer", "xoxb-test");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("compass_policy_denied", document.RootElement.GetProperty("error").GetString());
    }
}
