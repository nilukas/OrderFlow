using Microsoft.EntityFrameworkCore;

namespace OrderFlow.Notifications.Persistence;

/// <summary>The primary key on MessageId is what makes the consumer idempotent: a message can only be claimed once.</summary>
public sealed class ProcessedMessage
{
    public Guid MessageId { get; set; }
    public DateTime ProcessedAtUtc { get; set; }
}

public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : DbContext(options)
{
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProcessedMessage>(b =>
        {
            b.ToTable("processed_messages");
            b.HasKey(m => m.MessageId);
        });
    }
}
