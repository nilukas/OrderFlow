using Microsoft.EntityFrameworkCore;
using OrderFlow.Orders.Domain;

namespace OrderFlow.Orders.Persistence;

/// <summary>
/// A row in the transactional outbox. It is written in the SAME transaction as the order, so we can never
/// have "order saved but event lost" or "event sent but order rolled back".
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
    public DateTime? PublishedAtUtc { get; set; }
}

public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(b =>
        {
            b.ToTable("orders");
            b.HasKey(o => o.Id);
            b.Property(o => o.CustomerReference).HasMaxLength(Order.MaxTextLength).IsRequired();
            b.Property(o => o.TotalAmount).HasPrecision(18, 2);

            b.OwnsMany(o => o.Lines, l =>
            {
                l.ToTable("order_lines");
                l.WithOwner().HasForeignKey("OrderId");
                l.Property<int>("Id").ValueGeneratedOnAdd();
                l.HasKey("Id");
                l.Property(x => x.Sku).HasMaxLength(Order.MaxTextLength).IsRequired();
                l.Property(x => x.UnitPrice).HasPrecision(18, 2);
                l.Ignore(x => x.LineTotal);
            });
            b.Navigation(o => o.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.ToTable("outbox_messages");
            b.HasKey(m => m.Id);
            b.Property(m => m.Type).HasMaxLength(200).IsRequired();
            b.Property(m => m.Payload).IsRequired();
            b.HasIndex(m => m.PublishedAtUtc);
        });
    }
}
