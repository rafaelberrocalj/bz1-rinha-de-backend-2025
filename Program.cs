using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

ThreadPool.SetMinThreads(2, 2);

var builder = WebApplication.CreateBuilder();

var paymentApiItems = new List<PaymentApi>
{
    new PaymentApi { ProcessorType = ProcessorType.Default },
    new PaymentApi { ProcessorType = ProcessorType.Fallback }
};
var paymentChannel = Channel.CreateUnbounded<PaymentDb>();

builder.Services.AddSingleton(paymentApiItems);
builder.Services.AddSingleton(paymentChannel);

builder.Services.AddHttpClient();
builder.Services.AddHttpClient(nameof(ProcessorType.Default), client =>
{
    client.BaseAddress = new Uri(Environment.GetEnvironmentVariable("PAYMENT_PROCESSOR_URL_DEFAULT") ?? "http://localhost:8001");
});
builder.Services.AddHttpClient(nameof(ProcessorType.Fallback), client =>
{
    client.BaseAddress = new Uri(Environment.GetEnvironmentVariable("PAYMENT_PROCESSOR_URL_FALLBACK") ?? "http://localhost:8002");
});

builder.Services.AddHostedService<PaymentProcessorService>();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var pathSqliteDatabase = Environment.GetEnvironmentVariable("SQLITE_DATABASE") ?? "temp/app.db";
builder.Services.AddDbContext<PaymentDbContext>(options => options.UseSqlite($"Data Source={pathSqliteDatabase};"));

var app = builder.Build();

try
{
    using (var scope = app.Services.CreateScope())
    {
        var context = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        context.Database.EnsureCreated();
    }
}
catch (Exception ex)
{
    app.Logger.LogError(ex, "EnsureCreated failed: {message}", ex.Message);
}

app.MapPost("/payments", async (PaymentRequest request, Channel<PaymentDb> channel, CancellationToken cancellationToken) =>
{
    await channel.Writer.WriteAsync(new PaymentDb
    {
        CorrelationId = request.CorrelationId,
        Amount = request.Amount,
        RequestedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    }, cancellationToken);

    return Results.Accepted();
});

app.MapGet("/payments-summary", async (PaymentDbContext context, string from, string to, CancellationToken cancellationToken) =>
{
    var fromDateTime = DateTimeOffset.Parse(from).ToUnixTimeMilliseconds();
    var toDateTime = DateTimeOffset.Parse(to).ToUnixTimeMilliseconds();

    var payments = (await context.Payments
        .AsNoTracking()
        .Where(w => w.RequestedAt >= fromDateTime && w.RequestedAt <= toDateTime)
        .Select(s => new
        {
            s.ProcessorType,
            s.Amount
        })
        .ToListAsync(cancellationToken))
        .GroupBy(g => g.ProcessorType)
        .ToDictionary(
            d => d.Key,
            d => new
            {
                TotalRequests = d.Count(),
                TotalAmount = d.Sum(s => s.Amount)
            });

    payments.TryGetValue(ProcessorType.Default, out var paymentsDefault);
    payments.TryGetValue(ProcessorType.Fallback, out var paymentsFallback);

    return Results.Ok(new PaymentSummaryResponse
    {
        Default = new ProcessorSummary
        {
            TotalRequests = paymentsDefault?.TotalRequests ?? 0,
            TotalAmount = paymentsDefault?.TotalAmount ?? 0
        },
        Fallback = new ProcessorSummary
        {
            TotalRequests = paymentsFallback?.TotalRequests ?? 0,
            TotalAmount = paymentsFallback?.TotalAmount ?? 0
        }
    });
});

app.Run();

public class PaymentRequest
{
    public string CorrelationId { get; set; }
    public decimal Amount { get; set; }
}

public class PaymentSummaryResponse
{
    public ProcessorSummary Default { get; set; }
    public ProcessorSummary Fallback { get; set; }
}

public class ProcessorSummary
{
    public int TotalRequests { get; set; }
    public decimal TotalAmount { get; set; }
}

public class PaymentServiceHealthResponse
{
    public bool Failing { get; set; }
    public int MinResponseTime { get; set; }
}

public enum ProcessorType
{
    Default,
    Fallback
}

public class PaymentApi
{
    public ProcessorType ProcessorType { get; set; }
    public bool IsHealthy { get; set; } = true;
    public int DelayInMilliseconds { get; set; } = 0;
}

public class PaymentDbContext : DbContext
{
    public PaymentDbContext(DbContextOptions<PaymentDbContext> options) : base(options) { }

    public DbSet<PaymentDb> Payments { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PaymentDb>(entity =>
        {
            entity.HasKey(e => e.CorrelationId);
            entity.Property(e => e.Amount).HasPrecision(9, 2);
            entity.HasIndex(e => e.RequestedAt);
        });
    }
}

public class PaymentDb
{
    public string CorrelationId { get; set; }
    public decimal Amount { get; set; }
    public long RequestedAt { get; set; }
    public ProcessorType ProcessorType { get; set; }
}

public class PaymentProcessorService : BackgroundService
{
    private readonly HttpClient _httpClientDefault;
    private readonly HttpClient _httpClientFallback;
    private readonly PaymentDbContext _paymentDbContext;
    private readonly Channel<PaymentDb> _channel;
    private readonly List<PaymentApi> _paymentApiItems;
    private readonly ILogger<PaymentProcessorService> _logger;

    public PaymentProcessorService(
        IHttpClientFactory httpClientFactory,
        PaymentDbContext paymentDbContext,
        Channel<PaymentDb> channel,
        List<PaymentApi> paymentApiItems,
        ILogger<PaymentProcessorService> logger)
    {
        _httpClientDefault = httpClientFactory.CreateClient(nameof(ProcessorType.Default));
        _httpClientFallback = httpClientFactory.CreateClient(nameof(ProcessorType.Fallback));
        _paymentDbContext = paymentDbContext;
        _channel = channel;
        _paymentApiItems = paymentApiItems;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(() => HealthCheck(ProcessorType.Default, cancellationToken), cancellationToken);
        _ = Task.Run(() => HealthCheck(ProcessorType.Fallback, cancellationToken), cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            if (_paymentApiItems.All(a => !a.IsHealthy))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
                continue;
            }

            var paymentDb = await _channel.Reader.ReadAsync(cancellationToken);

            if (paymentDb == null)
            {
                continue;
            }

            bool processed = false;

            foreach (var paymentApiItem in _paymentApiItems.Where(w => w.IsHealthy))
            {
                if (paymentApiItem.IsHealthy = processed = await SendPaymentAndSave(paymentApiItem, paymentDb, cancellationToken))
                {
                    break;
                }
            }

            if (!processed)
            {
                await _channel.Writer.WriteAsync(paymentDb, cancellationToken);
            }
        }
    }

    async Task HealthCheck(ProcessorType processorType, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var paymentApiItem = _paymentApiItems.Single(a => a.ProcessorType == processorType);

            try
            {
                var httpClient = processorType switch
                {
                    ProcessorType.Default => _httpClientDefault,
                    ProcessorType.Fallback => _httpClientFallback,
                    _ => throw new NotImplementedException()
                };

                var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));

                var httpResponseMessage = await httpClient.GetAsync("/payments/service-health", cancellationTokenSource.Token);

                if (paymentApiItem.IsHealthy = httpResponseMessage.IsSuccessStatusCode)
                {
                    var json = await httpResponseMessage.Content.ReadAsStringAsync();
                    var paymentServiceHealth = JsonSerializer.Deserialize<PaymentServiceHealthResponse>(json);

                    paymentApiItem.IsHealthy = !paymentServiceHealth.Failing;
                    paymentApiItem.DelayInMilliseconds = paymentServiceHealth.MinResponseTime;
                }
            }
            catch (Exception)
            {
                paymentApiItem.IsHealthy = false;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }

    private async Task<bool> SendPaymentAndSave(PaymentApi paymentApi, PaymentDb paymentDb, CancellationToken cancellationToken)
    {
        try
        {
            var httpClient = paymentApi.ProcessorType switch
            {
                ProcessorType.Default => _httpClientDefault,
                ProcessorType.Fallback => _httpClientFallback,
                _ => throw new NotImplementedException()
            };

            var json = JsonSerializer.Serialize(new
            {
                correlationId = paymentDb.CorrelationId,
                amount = paymentDb.Amount,
                requestedAt = paymentDb.RequestedAt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")
            });

            var stringContent = new StringContent(json, Encoding.UTF8, "application/json");

            await Task.Delay(TimeSpan.FromMilliseconds(paymentApi.DelayInMilliseconds), cancellationToken);

            var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(paymentApi.DelayInMilliseconds + 1000));

            var httpResponseMessage = await httpClient.PostAsync("/payments", stringContent, cancellationTokenSource.Token);

            if (httpResponseMessage.IsSuccessStatusCode || httpResponseMessage.StatusCode == HttpStatusCode.UnprocessableEntity)
            {
                paymentDb.ProcessorType = paymentApi.ProcessorType;

                _paymentDbContext.Payments.Add(paymentDb);

                await _paymentDbContext.SaveChangesAsync(cancellationToken);

                return true;
            }
        }
        catch (Exception) { }

        return false;
    }
}
