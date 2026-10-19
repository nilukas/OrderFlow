using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderFlow.Orders.Api;
using OrderFlow.Orders.Messaging;
using OrderFlow.Orders.Outbox;
using OrderFlow.Orders.Persistence;

namespace OrderFlow.Tests;

/// <summary>
/// Test-only authentication: lets tests choose who is calling and with which scopes via headers.
/// No header = anonymous (401). The real JwtBearer scheme is replaced only inside the test host.
/// </summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-User", out var user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.ToString()) };
        if (Request.Headers.TryGetValue("X-Test-Scopes", out var scopes))
        {
            claims.Add(new Claim("scope", scopes.ToString()));
        }

        var identity = new ClaimsIdentity(claims, "Test");
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), "Test");
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

public sealed class OrdersApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Persistence:Provider", "InMemory");
        builder.UseSetting("Messaging:Provider", "InMemory");
        builder.UseSetting("Outbox:Enabled", "false");
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(o =>
                {
                    o.DefaultAuthenticateScheme = "Test";
                    o.DefaultChallengeScheme = "Test";
                    o.DefaultForbidScheme = "Test";
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
        });
    }
}

public sealed class OrdersApiTests(OrdersApiFactory factory) : IClassFixture<OrdersApiFactory>
{
    private static object ValidOrder() => new
    {
        customerReference = "CUST-1",
        lines = new[] { new { sku = "SKU-1", quantity = 2, unitPrice = 10.5m }, new { sku = "SKU-2", quantity = 1, unitPrice = 4m } }
    };

    private HttpClient ClientWith(string? scopes)
    {
        var client = factory.CreateClient();
        if (scopes is not null)
        {
            client.DefaultRequestHeaders.Add("X-Test-User", "tester");
            client.DefaultRequestHeaders.Add("X-Test-Scopes", scopes);
        }

        return client;
    }

    [Fact]
    public async Task Create_without_a_token_returns_401()
    {
        var response = await ClientWith(null).PostAsJsonAsync("/api/v1/orders", ValidOrder());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_with_only_the_read_scope_returns_403()
    {
        var response = await ClientWith(Policies.OrdersRead).PostAsJsonAsync("/api/v1/orders", ValidOrder());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_then_get_returns_the_order_with_calculated_total()
    {
        var client = ClientWith($"{Policies.OrdersRead} {Policies.OrdersWrite}");

        var created = await client.PostAsJsonAsync("/api/v1/orders", ValidOrder());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<OrderResponse>();
        Assert.NotNull(body);
        Assert.Equal(25m, body.TotalAmount);

        var fetched = await client.GetFromJsonAsync<OrderResponse>($"/api/v1/orders/{body.Id}");
        Assert.Equal(body.Id, fetched!.Id);
        Assert.Equal(2, fetched.Lines.Count);
    }

    [Fact]
    public async Task Create_with_invalid_quantity_returns_400_problem_details()
    {
        var client = ClientWith(Policies.OrdersWrite);
        var invalid = new { customerReference = "CUST-1", lines = new[] { new { sku = "SKU-1", quantity = 0, unitPrice = 1m } } };

        var response = await client.PostAsJsonAsync("/api/v1/orders", invalid);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Get_unknown_order_returns_404()
    {
        var response = await ClientWith(Policies.OrdersRead).GetAsync($"/api/v1/orders/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Create_stores_the_order_and_its_outbox_event_together()
    {
        var client = ClientWith(Policies.OrdersWrite);
        var created = await client.PostAsJsonAsync("/api/v1/orders", ValidOrder());
        var body = await created.Content.ReadFromJsonAsync<OrderResponse>();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        var outbox = await db.OutboxMessages.SingleAsync(m => m.Payload.Contains(body!.Id.ToString()));

        Assert.Equal("OrderCreated.v1", outbox.Type);
        Assert.Null(outbox.PublishedAtUtc);
    }

    [Fact]
    public async Task Outbox_processor_publishes_each_message_exactly_once_per_run()
    {
        var client = ClientWith(Policies.OrdersWrite);
        var created = await client.PostAsJsonAsync("/api/v1/orders", ValidOrder());
        var body = await created.Content.ReadFromJsonAsync<OrderResponse>();

        using var scope = factory.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<OutboxProcessor>();
        var publisher = factory.Services.GetRequiredService<InMemoryMessagePublisher>();

        var firstRun = await processor.ProcessBatchAsync(100, CancellationToken.None);
        var secondRun = await processor.ProcessBatchAsync(100, CancellationToken.None);

        Assert.True(firstRun >= 1);
        Assert.Equal(0, secondRun);
        Assert.Contains(publisher.Published, p => p.Payload.Contains(body!.Id.ToString()));
    }
}
