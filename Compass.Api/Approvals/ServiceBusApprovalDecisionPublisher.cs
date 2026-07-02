using System.Text.Json;
using Azure.Messaging.ServiceBus;

namespace Compass.Api.Approvals;

public sealed class ServiceBusApprovalDecisionPublisher : IApprovalDecisionPublisher, IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusSender _sender;

    public ServiceBusApprovalDecisionPublisher(IConfiguration configuration)
    {
        var connectionString = configuration["ServiceBus:ConnectionString"]
            ?? throw new InvalidOperationException("ServiceBus:ConnectionString is required.");
        var queueName = configuration["ServiceBus:ApprovalDecisionsQueue"] ?? "compass-approval-decisions";

        _client = new ServiceBusClient(connectionString);
        _sender = _client.CreateSender(queueName);
    }

    public async Task PublishAsync(ApprovalDecisionMessage message, CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.Serialize(message, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await _sender.SendMessageAsync(
            new ServiceBusMessage(body)
            {
                MessageId = message.RequestId,
                Subject = "approval-decision"
            },
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _sender.DisposeAsync();
        await _client.DisposeAsync();
    }
}
