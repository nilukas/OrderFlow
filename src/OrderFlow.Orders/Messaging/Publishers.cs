using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Options;
using OrderFlow.Contracts;
using RabbitMQ.Client;

namespace OrderFlow.Orders.Messaging;

public interface IMessagePublisher
{
    Task PublishAsync(Guid messageId, string type, string jsonPayload, CancellationToken ct);
}

public sealed class RabbitMqOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5672;

    // RabbitMQ credentials come from configuration/environment or a secret store. Never commit real values.
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
}

/// <summary>Used for tests and for running the API without any broker.</summary>
public sealed class InMemoryMessagePublisher : IMessagePublisher
{
    private readonly ConcurrentQueue<(Guid MessageId, string Type, string Payload)> _published = new();

    public IReadOnlyCollection<(Guid MessageId, string Type, string Payload)> Published => _published.ToArray();

    public Task PublishAsync(Guid messageId, string type, string jsonPayload, CancellationToken ct)
    {
        _published.Enqueue((messageId, type, jsonPayload));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Publishes durable, persistent messages with publisher confirms, so PublishAsync only completes after the
/// broker has accepted the message. Combined with the outbox this gives at-least-once delivery.
/// </summary>
public sealed class RabbitMqPublisher(IOptions<RabbitMqOptions> options) : IMessagePublisher, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;

    public async Task PublishAsync(Guid messageId, string type, string jsonPayload, CancellationToken ct)
    {
        var channel = await GetChannelAsync(ct);
        var properties = new BasicProperties
        {
            Persistent = true,
            MessageId = messageId.ToString(),
            Type = type,
            ContentType = "application/json"
        };

        await channel.BasicPublishAsync(
            exchange: string.Empty,
            routingKey: MessagingTopology.OrdersCreatedQueue,
            mandatory: true,
            basicProperties: properties,
            body: Encoding.UTF8.GetBytes(jsonPayload),
            cancellationToken: ct);
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_channel is { IsOpen: true })
            {
                return _channel;
            }

            if (_connection is not { IsOpen: true })
            {
                var o = options.Value;
                var factory = new ConnectionFactory
                {
                    HostName = o.Host,
                    Port = o.Port,
                    UserName = o.UserName,
                    Password = o.Password
                };
                _connection = await factory.CreateConnectionAsync(ct);
            }

            _channel = await _connection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
                ct);

            await _channel.QueueDeclareAsync(
                MessagingTopology.OrdersCreatedDeadLetterQueue, durable: true, exclusive: false, autoDelete: false,
                cancellationToken: ct);
            await _channel.QueueDeclareAsync(
                MessagingTopology.OrdersCreatedQueue, durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object?>
                {
                    ["x-dead-letter-exchange"] = string.Empty,
                    ["x-dead-letter-routing-key"] = MessagingTopology.OrdersCreatedDeadLetterQueue
                },
                cancellationToken: ct);

            return _channel;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.DisposeAsync();
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _gate.Dispose();
    }
}
