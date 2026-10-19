using Microsoft.EntityFrameworkCore;
using OrderFlow.Orders.Messaging;
using OrderFlow.Orders.Persistence;

namespace OrderFlow.Orders.Outbox;

/// <summary>
/// Moves pending outbox rows to the broker. It is a plain class (not the background loop) so tests can call it directly.
/// Delivery is at-least-once: if we crash after publishing but before marking the row, the message is sent again,
/// which is why the consumer must be idempotent.
/// </summary>
public sealed class OutboxProcessor(
    OrdersDbContext db,
    IMessagePublisher publisher,
    TimeProvider time,
    ILogger<OutboxProcessor> logger)
{
    public async Task<int> ProcessBatchAsync(int batchSize, CancellationToken ct)
    {
        var pending = await db.OutboxMessages
            .Where(m => m.PublishedAtUtc == null)
            .OrderBy(m => m.OccurredAtUtc)
            .Take(batchSize)
            .ToListAsync(ct);

        var published = 0;
        foreach (var message in pending)
        {
            try
            {
                await publisher.PublishAsync(message.Id, message.Type, message.Payload, ct);
                message.PublishedAtUtc = time.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync(ct);
                published++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Stop the batch so ordering is kept; the next poll retries from this message.
                logger.LogWarning(ex, "Failed to publish outbox message {MessageId}; will retry.", message.Id);
                break;
            }
        }

        return published;
    }
}

public sealed class OutboxPublisherService(IServiceScopeFactory scopeFactory, ILogger<OutboxPublisherService> logger)
    : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var processor = scope.ServiceProvider.GetRequiredService<OutboxProcessor>();
                    await processor.ProcessBatchAsync(50, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Outbox polling iteration failed.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}
