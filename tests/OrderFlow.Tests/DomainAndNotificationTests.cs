using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderFlow.Contracts;
using OrderFlow.Notifications.Handlers;
using OrderFlow.Notifications.Insights;
using OrderFlow.Notifications.Persistence;
using OrderFlow.Orders.Domain;

namespace OrderFlow.Tests;

public sealed class OrderDomainTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Total_is_the_sum_of_line_totals()
    {
        var order = Order.Create("CUST-1",
            [new OrderLineInput("A", 2, 10.5m), new OrderLineInput("B", 1, 4m)], Now);

        Assert.Equal(25m, order.TotalAmount);
        Assert.Equal(OrderStatus.Created, order.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Customer_reference_is_required(string? reference)
    {
        Assert.Throws<DomainException>(() => Order.Create(reference, [new OrderLineInput("A", 1, 1m)], Now));
    }

    [Fact]
    public void An_order_needs_at_least_one_line()
    {
        Assert.Throws<DomainException>(() => Order.Create("CUST-1", [], Now));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(10_001, 1)]
    [InlineData(1, -1)]
    public void Line_quantity_and_price_are_bounded(int quantity, decimal price)
    {
        Assert.Throws<DomainException>(() =>
            Order.Create("CUST-1", [new OrderLineInput("A", quantity, price)], Now));
    }

    [Fact]
    public void Too_many_lines_are_rejected()
    {
        var lines = Enumerable.Range(0, Order.MaxLines + 1).Select(i => new OrderLineInput($"S{i}", 1, 1m));
        Assert.Throws<DomainException>(() => Order.Create("CUST-1", lines, Now));
    }
}

public sealed class OrderCreatedHandlerTests
{
    private sealed class FakeInsights(Func<string>? behaviour = null) : IOrderInsightService
    {
        public Task<string> SummarizeAsync(OrderCreated order, CancellationToken ct) =>
            Task.FromResult(behaviour?.Invoke() ?? "summary");
    }

    private sealed class FakeSender : INotificationSender
    {
        public List<(Guid OrderId, string Message)> Sent { get; } = [];

        public Task SendAsync(Guid orderId, string message, CancellationToken ct)
        {
            Sent.Add((orderId, message));
            return Task.CompletedTask;
        }
    }

    private static NotificationsDbContext NewDb() =>
        new(new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseInMemoryDatabase($"notifications-{Guid.NewGuid()}").Options);

    private static OrderCreated SampleOrder() =>
        new(Guid.NewGuid(), "CUST-1", 25m, [new OrderCreatedLine("A", 2, 10.5m)], DateTime.UtcNow);

    [Fact]
    public async Task The_same_message_delivered_twice_sends_only_one_notification()
    {
        await using var db = NewDb();
        var sender = new FakeSender();
        var handler = new OrderCreatedHandler(db, new FakeInsights(), sender, TimeProvider.System,
            NullLogger<OrderCreatedHandler>.Instance);
        var messageId = Guid.NewGuid();
        var order = SampleOrder();

        var first = await handler.HandleAsync(messageId, order, CancellationToken.None);
        var second = await handler.HandleAsync(messageId, order, CancellationToken.None);

        Assert.Equal(HandleResult.Processed, first);
        Assert.Equal(HandleResult.Duplicate, second);
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task A_failed_attempt_releases_the_claim_so_the_retry_can_succeed()
    {
        await using var db = NewDb();
        var sender = new FakeSender();
        var attempts = 0;
        var insights = new FakeInsights(() => ++attempts == 1 ? throw new InvalidOperationException("boom") : "ok");
        var handler = new OrderCreatedHandler(db, insights, sender, TimeProvider.System,
            NullLogger<OrderCreatedHandler>.Instance);
        var messageId = Guid.NewGuid();
        var order = SampleOrder();

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(messageId, order, CancellationToken.None));
        var retry = await handler.HandleAsync(messageId, order, CancellationToken.None);

        Assert.Equal(HandleResult.Processed, retry);
        Assert.Single(sender.Sent);
    }
}

public sealed class OrderInsightTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? LastBody { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return respond(request);
        }
    }

    private static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent($$"""{"content":[{"type":"text","text":"{{text}}"}]}""")
    };

    private static OrderCreated Order() =>
        new(Guid.NewGuid(), "SECRET-CUSTOMER-REF", 25m, [new OrderCreatedLine("SKU-1", 2, 10.5m)], DateTime.UtcNow);

    private static (ResilientOrderInsightService Service, StubHandler Handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> respond, bool enabled = true)
    {
        var handler = new StubHandler(respond);
        var options = Options.Create(new AiOptions { Enabled = enabled, ApiKey = "test-key", BaseUrl = "https://example.test" });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var service = new ResilientOrderInsightService(
            new ClaudeOrderInsightService(http, options),
            new TemplateOrderInsightService(),
            options,
            NullLogger<ResilientOrderInsightService>.Instance);
        return (service, handler);
    }

    [Fact]
    public async Task Prompt_never_contains_the_customer_reference_and_uses_the_api_key_header()
    {
        var (service, handler) = Build(_ => Ok("A short factual summary."));

        var result = await service.SummarizeAsync(Order(), CancellationToken.None);

        Assert.Equal("A short factual summary.", result);
        Assert.DoesNotContain("SECRET-CUSTOMER-REF", handler.LastBody);
        Assert.Contains("SKU-1", handler.LastBody);
        Assert.Equal("test-key", handler.LastRequest!.Headers.GetValues("x-api-key").Single());
    }

    [Fact]
    public async Task Server_error_falls_back_to_the_template()
    {
        var (service, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await service.SummarizeAsync(Order(), CancellationToken.None);

        Assert.StartsWith("Order ", result);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    public async Task Output_that_fails_the_guardrails_falls_back_to_the_template(string badOutput)
    {
        var (service, _) = Build(_ => Ok(badOutput));

        var result = await service.SummarizeAsync(Order(), CancellationToken.None);

        Assert.StartsWith("Order ", result);
    }

    [Fact]
    public async Task Oversized_output_falls_back_to_the_template()
    {
        var (service, _) = Build(_ => Ok(new string('x', InsightGuard.MaxLength + 1)));

        var result = await service.SummarizeAsync(Order(), CancellationToken.None);

        Assert.StartsWith("Order ", result);
    }

    [Fact]
    public async Task When_ai_is_disabled_no_http_call_is_made()
    {
        var (service, handler) = Build(_ => Ok("never used"), enabled: false);

        var result = await service.SummarizeAsync(Order(), CancellationToken.None);

        Assert.Equal(0, handler.Calls);
        Assert.StartsWith("Order ", result);
    }
}
