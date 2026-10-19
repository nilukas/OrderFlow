using Microsoft.EntityFrameworkCore;
using OrderFlow.Contracts;
using OrderFlow.Notifications.Insights;
using OrderFlow.Notifications.Persistence;

namespace OrderFlow.Notifications.Handlers;

public enum HandleResult
{
    Processed,
    Duplicate
}

public interface INotificationSender
{
    Task SendAsync(Guid orderId, string message, CancellationToken ct);
}

/// <summary>Stand-in for email/SMS/push. Replace with a real sender; the handler does not care.</summary>
public sealed class LoggingNotificationSender(ILogger<LoggingNotificationSender> logger) : INotificationSender
{
    public Task SendAsync(Guid orderId, string message, CancellationToken ct)
    {
        logger.LogInformation("Notification for order {OrderId}: {Message}", orderId, message);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Idempotent consumer. Brokers deliver at-least-once, so the same message can arrive twice.
/// Strategy: "claim" the MessageId first (primary key = only one claim can win), do the side effect,
/// and if the side effect fails, release the claim so the redelivery can try again.
/// </summary>
public sealed class OrderCreatedHandler(
    NotificationsDbContext db,
    IOrderInsightService insights,
    INotificationSender sender,
    TimeProvider time,
    ILogger<OrderCreatedHandler> logger)
{
    public async Task<HandleResult> HandleAsync(Guid messageId, OrderCreated order, CancellationToken ct)
    {
        if (await db.ProcessedMessages.AnyAsync(p => p.MessageId == messageId, ct))
        {
            logger.LogInformation("Duplicate message {MessageId} ignored.", messageId);
            return HandleResult.Duplicate;
        }

        var claim = new ProcessedMessage { MessageId = messageId, ProcessedAtUtc = time.GetUtcNow().UtcDateTime };
        db.ProcessedMessages.Add(claim);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another consumer instance claimed it between our check and our insert.
            db.Entry(claim).State = EntityState.Detached;
            return HandleResult.Duplicate;
        }

        try
        {
            var summary = await insights.SummarizeAsync(order, ct);
            await sender.SendAsync(order.OrderId, summary, ct);
            return HandleResult.Processed;
        }
        catch
        {
            db.ProcessedMessages.Remove(claim);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }
}
