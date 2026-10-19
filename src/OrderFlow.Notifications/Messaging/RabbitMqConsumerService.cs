using System.Text.Json;
using Microsoft.Extensions.Options;
using OrderFlow.Contracts;
using OrderFlow.Notifications.Handlers;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace OrderFlow.Notifications.Messaging;

public sealed class RabbitMqOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5672;

    // RabbitMQ credentials come from configuration/environment or a secret store. Never commit real values.
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
}

/// <summary>
/// Consumes OrderCreated events. Acknowledgement rules:
///  - handled (or duplicate)         -> ack
///  - unreadable message (poison)    -> nack, no requeue  -> goes to the dead-letter queue
///  - transient processing failure   -> nack, requeue     -> redelivered and retried
/// </summary>
public sealed class RabbitMqConsumerService(
    IOptions<RabbitMqOptions> options,
    IServiceScopeFactory scopeFactory,
    ILogger<RabbitMqConsumerService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "RabbitMQ consumer failed; reconnecting in 5 seconds.");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var o = options.Value;
        var factory = new ConnectionFactory { HostName = o.Host, Port = o.Port, UserName = o.UserName, Password = o.Password };

        await using var connection = await factory.CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);

        await channel.QueueDeclareAsync(
            MessagingTopology.OrdersCreatedDeadLetterQueue, durable: true, exclusive: false, autoDelete: false,
            cancellationToken: ct);
        await channel.QueueDeclareAsync(
            MessagingTopology.OrdersCreatedQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = string.Empty,
                ["x-dead-letter-routing-key"] = MessagingTopology.OrdersCreatedDeadLetterQueue
            },
            cancellationToken: ct);

        // Never hold more than 10 unacknowledged messages: protects memory and spreads work across instances.
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 10, global: false, cancellationToken: ct);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, ea) => OnMessageAsync(channel, ea, ct);
        await channel.BasicConsumeAsync(MessagingTopology.OrdersCreatedQueue, autoAck: false, consumer, ct);

        logger.LogInformation("Consuming {Queue}.", MessagingTopology.OrdersCreatedQueue);
        await Task.Delay(Timeout.Infinite, ct);
    }

    private async Task OnMessageAsync(IChannel channel, BasicDeliverEventArgs ea, CancellationToken ct)
    {
        OrderCreated? order;
        Guid messageId;
        try
        {
            if (!Guid.TryParse(ea.BasicProperties.MessageId, out messageId))
            {
                throw new JsonException("Missing or invalid MessageId.");
            }

            order = JsonSerializer.Deserialize<OrderCreated>(ea.Body.Span, Json)
                    ?? throw new JsonException("Empty payload.");
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Poison message; dead-lettering.");
            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, ct);
            return;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<OrderCreatedHandler>();
            await handler.HandleAsync(messageId, order, ct);
            await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Processing failed for message {MessageId}; requeueing.", messageId);
            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true, ct);
        }
    }
}
