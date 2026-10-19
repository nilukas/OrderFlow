using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderFlow.Contracts;
using OrderFlow.Orders.Domain;
using OrderFlow.Orders.Persistence;

namespace OrderFlow.Orders.Api;

public static class Policies
{
    public const string OrdersRead = "orders.read";
    public const string OrdersWrite = "orders.write";
}

public sealed record CreateOrderRequest(string? CustomerReference, List<CreateOrderLineRequest>? Lines);

public sealed record CreateOrderLineRequest(string Sku, int Quantity, decimal UnitPrice);

public sealed record OrderLineResponse(string Sku, int Quantity, decimal UnitPrice, decimal LineTotal);

public sealed record OrderResponse(
    Guid Id,
    string CustomerReference,
    string Status,
    decimal TotalAmount,
    DateTime CreatedAtUtc,
    IReadOnlyList<OrderLineResponse> Lines)
{
    public static OrderResponse From(Order o) => new(
        o.Id,
        o.CustomerReference,
        o.Status.ToString(),
        o.TotalAmount,
        o.CreatedAtUtc,
        o.Lines.Select(l => new OrderLineResponse(l.Sku, l.Quantity, l.UnitPrice, l.LineTotal)).ToList());
}

public static class OrdersEndpoints
{
    private static readonly JsonSerializerOptions EventJson = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapOrdersEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/orders");
        group.MapPost("/", CreateAsync).RequireAuthorization(Policies.OrdersWrite);
        group.MapGet("/{id:guid}", GetAsync).RequireAuthorization(Policies.OrdersRead);
        return app;
    }

    private static async Task<IResult> CreateAsync(
        CreateOrderRequest request,
        OrdersDbContext db,
        TimeProvider time,
        CancellationToken ct)
    {
        Order order;
        try
        {
            var lines = (request.Lines ?? []).Select(l => new OrderLineInput(l.Sku, l.Quantity, l.UnitPrice));
            order = Order.Create(request.CustomerReference, lines, time.GetUtcNow().UtcDateTime);
        }
        catch (DomainException ex)
        {
            return Results.Problem(title: "Invalid order", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }

        var integrationEvent = new OrderCreated(
            order.Id,
            order.CustomerReference,
            order.TotalAmount,
            order.Lines.Select(l => new OrderCreatedLine(l.Sku, l.Quantity, l.UnitPrice)).ToList(),
            order.CreatedAtUtc);

        db.Orders.Add(order);
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = MessagingTopology.OrderCreatedType,
            Payload = JsonSerializer.Serialize(integrationEvent, EventJson),
            OccurredAtUtc = order.CreatedAtUtc
        });

        // One SaveChanges = one transaction: the order and its event are stored together or not at all.
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/v1/orders/{order.Id}", OrderResponse.From(order));
    }

    private static async Task<IResult> GetAsync(Guid id, OrdersDbContext db, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct);
        return order is null ? Results.NotFound() : Results.Ok(OrderResponse.From(order));
    }
}
