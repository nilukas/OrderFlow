using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using OrderFlow.Orders.Api;
using OrderFlow.Orders.Messaging;
using OrderFlow.Orders.Outbox;
using OrderFlow.Orders.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddSingleton(TimeProvider.System);

// --- Persistence: PostgreSQL by default, in-memory for tests / running without Docker ---
var persistence = builder.Configuration["Persistence:Provider"] ?? "Postgres";
var inMemoryDbName = $"orders-{Guid.NewGuid()}";
builder.Services.AddDbContext<OrdersDbContext>(options =>
{
    if (persistence == "InMemory")
    {
        options.UseInMemoryDatabase(inMemoryDbName);
    }
    else
    {
        options.UseNpgsql(builder.Configuration.GetConnectionString("Orders"));
    }
});

// --- Messaging: RabbitMQ by default, in-memory for tests ---
if ((builder.Configuration["Messaging:Provider"] ?? "RabbitMq") == "InMemory")
{
    builder.Services.AddSingleton<InMemoryMessagePublisher>();
    builder.Services.AddSingleton<IMessagePublisher>(sp => sp.GetRequiredService<InMemoryMessagePublisher>());
}
else
{
    builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection("RabbitMq"));
    builder.Services.AddSingleton<IMessagePublisher, RabbitMqPublisher>();
}

builder.Services.AddScoped<OutboxProcessor>();
if (builder.Configuration.GetValue("Outbox:Enabled", true))
{
    builder.Services.AddHostedService<OutboxPublisherService>();
}

// --- AuthN/AuthZ: validate JWT access tokens issued by your OIDC identity provider. ---
// Authority/audience come from configuration ("Authentication:Schemes:Bearer"); there is no secret in this service.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.OrdersRead, p => p.RequireAuthenticatedUser().RequireAssertion(c => HasScope(c.User, Policies.OrdersRead)))
    .AddPolicy(Policies.OrdersWrite, p => p.RequireAuthenticatedUser().RequireAssertion(c => HasScope(c.User, Policies.OrdersWrite)));

var app = builder.Build();

if (app.Configuration.GetValue("Persistence:EnsureCreated", true))
{
    // Fine for a demo. In production, use EF Core migrations ("dotnet ef migrations add ...") instead.
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<OrdersDbContext>().Database.EnsureCreated();
}

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks("/health");
app.MapOrdersEndpoints();

app.Run();

static bool HasScope(System.Security.Claims.ClaimsPrincipal user, string scope) =>
    user.FindAll("scope").Concat(user.FindAll("scp"))
        .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        .Contains(scope, StringComparer.Ordinal);

// Needed so the integration tests can use WebApplicationFactory<Program>.
public partial class Program;
