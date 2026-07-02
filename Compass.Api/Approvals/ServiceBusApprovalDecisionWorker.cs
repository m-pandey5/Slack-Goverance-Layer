using System.Text.Json;
using Azure.Messaging.ServiceBus;

namespace Compass.Api.Approvals;

public sealed class ServiceBusApprovalDecisionWorker : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ServiceBusApprovalDecisionWorker> _logger;

    public ServiceBusApprovalDecisionWorker(
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        ILogger<ServiceBusApprovalDecisionWorker> logger)
    {
        _configuration = configuration;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connectionString = _configuration["ServiceBus:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var queueName = _configuration["ServiceBus:ApprovalDecisionsQueue"] ?? "compass-approval-decisions";
        await using var client = new ServiceBusClient(connectionString);
        await using var processor = client.CreateProcessor(queueName, new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false,
            MaxConcurrentCalls = 1
        });

        processor.ProcessMessageAsync += ProcessMessageAsync;
        processor.ProcessErrorAsync += args =>
        {
            _logger.LogError(args.Exception, "Service Bus approval worker error source={Source}", args.ErrorSource);
            return Task.CompletedTask;
        };

        await processor.StartProcessingAsync(stoppingToken);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            await processor.StopProcessingAsync(CancellationToken.None);
        }
    }

    private async Task ProcessMessageAsync(ProcessMessageEventArgs args)
    {
        ApprovalDecisionMessage? message = null;
        try
        {
            message = JsonSerializer.Deserialize<ApprovalDecisionMessage>(
                args.Message.Body.ToString(),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            if (message is null)
            {
                await args.DeadLetterMessageAsync(args.Message, "invalid_message", "Could not deserialize approval decision.");
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var executor = scope.ServiceProvider.GetRequiredService<IApprovalExecutor>();
            var result = await executor.ExecuteDecisionAsync(message, args.CancellationToken);

            if (result.Status == "failed")
            {
                _logger.LogWarning(
                    "Approval execution finished as failed request={RequestId} error={Error}",
                    result.RequestId,
                    result.Error ?? "(none)");
            }

            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed processing approval decision request={RequestId}", message?.RequestId ?? "(unknown)");
            await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken);
        }
    }
}
