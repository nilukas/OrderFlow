using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OrderFlow.Contracts;

namespace OrderFlow.Notifications.Insights;

public interface IOrderInsightService
{
    Task<string> SummarizeAsync(OrderCreated order, CancellationToken ct);
}

public sealed class AiOptions
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "https://api.anthropic.com";
    public string Model { get; set; } = "claude-sonnet-5";

    // Read from an environment variable / secret store (for example Ai__ApiKey). Never commit it.
    public string? ApiKey { get; set; }
    public int TimeoutSeconds { get; set; } = 10;
}

/// <summary>Deterministic fallback: always works, never leaves the system, never says anything wrong.</summary>
public sealed class TemplateOrderInsightService : IOrderInsightService
{
    public Task<string> SummarizeAsync(OrderCreated order, CancellationToken ct)
    {
        var lineCount = order.Lines.Count;
        var summary = $"Order {order.OrderId}: {lineCount} line(s), total {order.TotalAmount:0.00}.";
        return Task.FromResult(summary);
    }
}

public static class InsightGuard
{
    public const int MaxLength = 400;

    /// <summary>
    /// Model output is untrusted input. We accept it only if it is short, single-purpose text;
    /// anything else is rejected and the caller falls back to the template.
    /// </summary>
    public static bool TryAccept(string? text, out string accepted)
    {
        accepted = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.Length > MaxLength || trimmed.Contains('<') || trimmed.Contains('>'))
        {
            return false;
        }

        accepted = trimmed;
        return true;
    }
}

/// <summary>
/// Calls the Anthropic Messages API. Guardrails: only non-personal order data goes into the prompt
/// (the customer reference is deliberately NOT sent), there is a hard timeout, and the output is validated.
/// </summary>
public sealed class ClaudeOrderInsightService(HttpClient http, IOptions<AiOptions> options) : IOrderInsightService
{
    private const string SystemPrompt =
        "You write ONE short, factual sentence (maximum 40 words) summarising an e-commerce order for an internal " +
        "operations log. Use only the data provided. Do not give advice and do not add information.";

    public async Task<string> SummarizeAsync(OrderCreated order, CancellationToken ct)
    {
        var o = options.Value;
        var userContent = JsonSerializer.Serialize(new
        {
            orderId = order.OrderId,
            total = order.TotalAmount,
            lines = order.Lines.Select(l => new { l.Sku, l.Quantity, l.UnitPrice })
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent.Create(new
            {
                model = o.Model,
                max_tokens = 150,
                system = SystemPrompt,
                messages = new[] { new { role = "user", content = userContent } }
            })
        };
        request.Headers.Add("x-api-key", o.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(o.TimeoutSeconds));

        using var response = await http.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);

        return doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty;
    }
}

/// <summary>
/// AI is an optimisation, not a dependency: if it is disabled, slow, down, or returns something we do not
/// trust, the order flow still completes using the deterministic template.
/// </summary>
public sealed class ResilientOrderInsightService(
    ClaudeOrderInsightService ai,
    TemplateOrderInsightService fallback,
    IOptions<AiOptions> options,
    ILogger<ResilientOrderInsightService> logger) : IOrderInsightService
{
    public async Task<string> SummarizeAsync(OrderCreated order, CancellationToken ct)
    {
        var o = options.Value;
        if (o.Enabled && !string.IsNullOrWhiteSpace(o.ApiKey))
        {
            try
            {
                var text = await ai.SummarizeAsync(order, ct);
                if (InsightGuard.TryAccept(text, out var accepted))
                {
                    return accepted;
                }

                logger.LogWarning("AI summary rejected by guardrails for order {OrderId}; using template.", order.OrderId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "AI summary failed for order {OrderId}; using template.", order.OrderId);
            }
        }

        return await fallback.SummarizeAsync(order, ct);
    }
}
