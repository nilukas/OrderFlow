namespace OrderFlow.Orders.Domain;

public sealed class DomainException(string message) : Exception(message);

public enum OrderStatus
{
    Created = 1
}

public sealed record OrderLineInput(string Sku, int Quantity, decimal UnitPrice);

/// <summary>
/// Aggregate root. All invariants live here so no code path (API, import job, test) can create an invalid order.
/// Input is bounded on purpose (OWASP: validate type, length and range of every external input).
/// </summary>
public sealed class Order
{
    public const int MaxLines = 100;
    public const int MaxTextLength = 64;

    private readonly List<OrderLine> _lines = [];

    private Order()
    {
    }

    public Guid Id { get; private set; }
    public string CustomerReference { get; private set; } = string.Empty;
    public OrderStatus Status { get; private set; }
    public decimal TotalAmount { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public IReadOnlyCollection<OrderLine> Lines => _lines;

    public static Order Create(string? customerReference, IEnumerable<OrderLineInput>? lines, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(customerReference) || customerReference.Trim().Length > MaxTextLength)
        {
            throw new DomainException($"Customer reference is required and must be at most {MaxTextLength} characters.");
        }

        var inputs = lines?.ToList() ?? [];
        if (inputs.Count == 0)
        {
            throw new DomainException("An order needs at least one line.");
        }

        if (inputs.Count > MaxLines)
        {
            throw new DomainException($"An order can have at most {MaxLines} lines.");
        }

        var order = new Order
        {
            Id = Guid.NewGuid(),
            CustomerReference = customerReference.Trim(),
            Status = OrderStatus.Created,
            CreatedAtUtc = nowUtc
        };

        foreach (var input in inputs)
        {
            order._lines.Add(OrderLine.Create(input));
        }

        order.TotalAmount = order._lines.Sum(l => l.LineTotal);
        return order;
    }
}

public sealed class OrderLine
{
    private OrderLine()
    {
    }

    public string Sku { get; private set; } = string.Empty;
    public int Quantity { get; private set; }
    public decimal UnitPrice { get; private set; }
    public decimal LineTotal => Quantity * UnitPrice;

    internal static OrderLine Create(OrderLineInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Sku) || input.Sku.Trim().Length > Order.MaxTextLength)
        {
            throw new DomainException($"SKU is required and must be at most {Order.MaxTextLength} characters.");
        }

        if (input.Quantity is < 1 or > 10_000)
        {
            throw new DomainException("Quantity must be between 1 and 10,000.");
        }

        if (input.UnitPrice is < 0 or > 1_000_000)
        {
            throw new DomainException("Unit price must be between 0 and 1,000,000.");
        }

        return new OrderLine
        {
            Sku = input.Sku.Trim(),
            Quantity = input.Quantity,
            UnitPrice = input.UnitPrice
        };
    }
}
