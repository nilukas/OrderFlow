namespace OrderFlow.Contracts;

/// <summary>
/// Integration event published when an order is created. This is a public contract between services:
/// treat it as versioned (the message "Type" header carries "OrderCreated.v1") and only make additive changes.
/// </summary>
public sealed record OrderCreated(
    Guid OrderId,
    string CustomerReference,
    decimal TotalAmount,
    IReadOnlyList<OrderCreatedLine> Lines,
    DateTime OccurredAtUtc);

public sealed record OrderCreatedLine(string Sku, int Quantity, decimal UnitPrice);

public static class MessagingTopology
{
    public const string OrderCreatedType = "OrderCreated.v1";
    public const string OrdersCreatedQueue = "orderflow.orders.created";

    // Messages that cannot be processed (poison messages) are dead-lettered here instead of looping forever.
    public const string OrdersCreatedDeadLetterQueue = "orderflow.orders.created.dlq";
}
