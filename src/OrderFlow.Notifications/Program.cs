using Microsoft.EntityFrameworkCore;
using OrderFlow.Notifications.Handlers;
using OrderFlow.Notifications.Insights;
using OrderFlow.Notifications.Messaging;
using OrderFlow.Notifications.Persistence;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);

var persistence = builder.Configuration["Persistence:Provider"] ?? "Postgres";
var inMemoryDbName = $"notifications-{Guid.NewGuid()}";
builder.Services.AddDbContext<NotificationsDbContext>(options =>
{
    if (persistence == "InMemory")
    {
        options.UseInMemoryDatabase(inMemoryDbName);
    }
    else
    {
        options.UseNpgsql(builder.Configuration.GetConnectionString("Notifications"));
    }
});

builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection("RabbitMq"));
builder.Services.Configure<AiOptions>(builder.Configuration.GetSection("Ai"));

// Typed HttpClient for the AI provider: base address fixed in config, never taken from message data (SSRF safe).
builder.Services.AddHttpClient<ClaudeOrderInsightService>((sp, client) =>
{
    var ai = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AiOptions>>().Value;
    client.BaseAddress = new Uri(ai.BaseUrl);
});
builder.Services.AddSingleton<TemplateOrderInsightService>();
builder.Services.AddScoped<IOrderInsightService, ResilientOrderInsightService>();
builder.Services.AddSingleton<INotificationSender, LoggingNotificationSender>();
builder.Services.AddScoped<OrderCreatedHandler>();
builder.Services.AddHostedService<RabbitMqConsumerService>();

var host = builder.Build();

if (builder.Configuration.GetValue("Persistence:EnsureCreated", true))
{
    using var scope = host.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Database.EnsureCreated();
}

host.Run();
