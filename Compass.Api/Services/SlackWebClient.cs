using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Compass.Api.Services;

public sealed class SlackWebClient
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SlackWebClient> _logger;

    public SlackWebClient(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<SlackWebClient> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task PostMessageAsync(
        string channel,
        string text,
        string? threadTs = null,
        CancellationToken cancellationToken = default)
    {
        await PostMessageCoreAsync(channel, text, blocks: null, threadTs, cancellationToken);
    }

    public async Task PostApprovalRequestAsync(
        string channel,
        string requestId,
        string agentId,
        string toolName,
        CancellationToken cancellationToken = default)
    {
        var blocks = new object[]
        {
            new
            {
                type = "section",
                text = new
                {
                    type = "mrkdwn",
                    text = $"*Compass approval required*\nAgent `{agentId}` wants to call `{toolName}`.\nRequest `{requestId}`"
                }
            },
            new
            {
                type = "actions",
                elements = new object[]
                {
                    new
                    {
                        type = "button",
                        text = new { type = "plain_text", text = "Approve" },
                        style = "primary",
                        action_id = "compass_approval_approve",
                        value = requestId
                    },
                    new
                    {
                        type = "button",
                        text = new { type = "plain_text", text = "Deny" },
                        style = "danger",
                        action_id = "compass_approval_deny",
                        value = requestId
                    }
                }
            }
        };

        await PostMessageCoreAsync(
            channel,
            $"Compass approval required for {toolName}",
            blocks,
            threadTs: null,
            cancellationToken);
    }

    private async Task PostMessageCoreAsync(
        string channel,
        string text,
        object? blocks,
        string? threadTs,
        CancellationToken cancellationToken)
    {
        var botToken = _configuration["Slack:BotToken"];
        if (string.IsNullOrWhiteSpace(botToken))
        {
            _logger.LogWarning("Slack bot token is not configured; skipping chat.postMessage.");
            return;
        }

        var payload = new Dictionary<string, object?>
        {
            ["channel"] = channel,
            ["text"] = text
        };

        if (!string.IsNullOrWhiteSpace(threadTs))
        {
            payload["thread_ts"] = threadTs;
        }

        if (blocks is not null)
        {
            payload["blocks"] = blocks;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://slack.com/api/chat.postMessage")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", botToken);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode || !IsSlackOk(responseBody))
        {
            _logger.LogWarning(
                "Slack chat.postMessage failed status={StatusCode} body={Body}",
                (int)response.StatusCode,
                responseBody);
        }
    }

    private static bool IsSlackOk(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        return document.RootElement.TryGetProperty("ok", out var ok) &&
               ok.ValueKind == JsonValueKind.True;
    }
}
