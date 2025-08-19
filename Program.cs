using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

var builder = WebApplication.CreateSlimBuilder();

builder.Logging.ClearProviders();

builder.WebHost.UseUrls("http://+:9999");

var paymentApiItems = new List<PaymentApi>
{
    new PaymentApi { ProcessorType = ProcessorType.Default },
    new PaymentApi { ProcessorType = ProcessorType.Fallback }
};
var paymentChannel = Channel.CreateUnbounded<PaymentRequest>();

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

builder.Services.AddDbContext<PaymentDbContext1>(options =>
{
    var database = Environment.GetEnvironmentVariable("SQLITE_DATABASE") ?? "temp/app1.db";
    options.UseSqlite($"Data Source={database}");
});

builder.Services.AddDbContext<PaymentDbContext2>(options =>
{
    var database = Environment.GetEnvironmentVariable("SQLITE_DATABASE") ?? "temp/app2.db";
    options.UseSqlite($"Data Source={database}");
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();

try
{
    using (var scope = app.Services.CreateScope())
    {
        var context = scope.ServiceProvider.GetRequiredService<PaymentDbContext1>();
        context.Database.EnsureCreated();
    }
}
catch (Exception ex)
{
    app.Logger.LogError(ex, "{context} EnsureCreated failed: {message}", nameof(PaymentDbContext1), ex.Message);
}

try
{
    using (var scope = app.Services.CreateScope())
    {
        var context = scope.ServiceProvider.GetRequiredService<PaymentDbContext2>();
        context.Database.EnsureCreated();
    }
}
catch (Exception ex)
{
    app.Logger.LogError(ex, "{context} EnsureCreated failed: {message}", nameof(PaymentDbContext2), ex.Message);
}

app.MapPost("/payments", async (
    PaymentRequest request,
    Channel<PaymentRequest> channel,
    CancellationToken cancellationToken = default) =>
{
    if (request == null || string.IsNullOrWhiteSpace(request.CorrelationId) || request.Amount <= 0)
    {
        return Results.BadRequest();
    }

    await channel.Writer.WriteAsync(new PaymentRequest
    {
        CorrelationId = request.CorrelationId,
        Amount = request.Amount
    }, cancellationToken);

    return Results.Accepted();
});

app.MapGet("/payments-summary", async (
    PaymentDbContext1 context1,
    PaymentDbContext2 context2,
    [FromQuery] string from = null,
    [FromQuery] string to = null,
    CancellationToken cancellationToken = default) =>
{
    if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
    {
        return Results.Ok(new PaymentSummaryResponse());
    }

    long fromDateTime;
    long toDateTime;

    try
    {
        fromDateTime = DateTimeOffset.Parse(from).ToUnixTimeMilliseconds() - 1;
        toDateTime = DateTimeOffset.Parse(to).ToUnixTimeMilliseconds() + 1;
    }
    catch (Exception)
    {
        return Results.Ok(new PaymentSummaryResponse());
    }

    var payments1 = await context1.Payments
        .AsNoTracking()
        .Where(w => w.RequestedAt > fromDateTime && w.RequestedAt < toDateTime)
        .Select(s => new
        {
            s.ProcessorType,
            s.Amount
        })
        .ToArrayAsync(cancellationToken);

    var payments2 = await context2.Payments
        .AsNoTracking()
        .Where(w => w.RequestedAt >= fromDateTime && w.RequestedAt <= toDateTime)
        .Select(s => new
        {
            s.ProcessorType,
            s.Amount
        })
        .ToArrayAsync(cancellationToken);

    if (!payments1.Any() && !payments2.Any())
    {
        return Results.Ok(new PaymentSummaryResponse());
    }

    var payments = payments1.Concat(payments2)
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
    public DateTimeOffset RequestedAt { get; set; }
}

public class PaymentSummaryResponse
{
    public ProcessorSummary Default { get; set; } = new();
    public ProcessorSummary Fallback { get; set; } = new();
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

public enum ProcessorType : byte
{
    Default = 0,
    Fallback = 1
}

public class PaymentApi
{
    public ProcessorType ProcessorType { get; set; }
    public bool IsHealthy { get; set; } = true;
    public int DelayInMilliseconds { get; set; }
}

public class PaymentDbContext1 : DbContext
{
    public PaymentDbContext1(DbContextOptions<PaymentDbContext1> options) : base(options) { }

    public DbSet<PaymentDb> Payments { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PaymentDb>(entity =>
        {
            entity.HasKey(e => e.CorrelationId);
            entity.Property(e => e.Amount).HasPrecision(12, 2);
            entity.HasIndex(e => e.RequestedAt);
        });
    }
}

public class PaymentDbContext2 : DbContext
{
    public PaymentDbContext2(DbContextOptions<PaymentDbContext2> options) : base(options) { }

    public DbSet<PaymentDb> Payments { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PaymentDb>(entity =>
        {
            entity.HasKey(e => e.CorrelationId);
            entity.Property(e => e.Amount).HasPrecision(12, 2);
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
    private readonly Channel<PaymentRequest> _channel;
    private readonly List<PaymentApi> _paymentApiItems;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PaymentProcessorService> _logger;

    private readonly string BACKEND_ID;

    public PaymentProcessorService(
        IHttpClientFactory httpClientFactory,
        Channel<PaymentRequest> channel,
        List<PaymentApi> paymentApiItems,
        IServiceProvider serviceProvider,
        ILogger<PaymentProcessorService> logger)
    {
        _httpClientDefault = httpClientFactory.CreateClient(nameof(ProcessorType.Default));
        _httpClientFallback = httpClientFactory.CreateClient(nameof(ProcessorType.Fallback));
        _channel = channel;
        _paymentApiItems = paymentApiItems;
        _serviceProvider = serviceProvider;
        _logger = logger;
        BACKEND_ID = Environment.GetEnvironmentVariable("BACKEND_ID") ?? "1";
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

            var paymentRequest = await _channel.Reader.ReadAsync(cancellationToken);

            if (paymentRequest == null)
            {
                continue;
            }

            bool processed = false;

            foreach (var paymentApiItem in _paymentApiItems.Where(w => w.IsHealthy))
            {
                if (paymentApiItem.IsHealthy = processed = await SendPaymentAndSave(paymentApiItem, paymentRequest, cancellationToken))
                {
                    break;
                }
            }

            if (!processed)
            {
                await _channel.Writer.WriteAsync(paymentRequest, cancellationToken);
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
            catch (Exception ex)
            {
                paymentApiItem.IsHealthy = false;

                _logger.LogError(ex, "Health check failed for {ProcessorType}: {Message}", processorType, ex.Message);
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }

    private async Task<bool> SendPaymentAndSave(PaymentApi paymentApi, PaymentRequest paymentRequest, CancellationToken cancellationToken)
    {
        try
        {
            var httpClient = paymentApi.ProcessorType switch
            {
                ProcessorType.Default => _httpClientDefault,
                ProcessorType.Fallback => _httpClientFallback,
                _ => throw new NotImplementedException()
            };

            await Task.Delay(TimeSpan.FromMilliseconds(paymentApi.DelayInMilliseconds), cancellationToken);

            var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(paymentApi.DelayInMilliseconds + 500));

            paymentRequest.RequestedAt = DateTimeOffset.UtcNow;

            var json = JsonSerializer.Serialize(new
            {
                correlationId = paymentRequest.CorrelationId,
                amount = paymentRequest.Amount,
                requestedAt = paymentRequest.RequestedAt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")
            });

            var stringContent = new StringContent(json, Encoding.UTF8, "application/json");

            var httpResponseMessage = await httpClient.PostAsync("/payments", stringContent, cancellationTokenSource.Token);

            if (httpResponseMessage.IsSuccessStatusCode || httpResponseMessage.StatusCode == HttpStatusCode.UnprocessableEntity)
            {
                var paymentDb = new PaymentDb
                {
                    CorrelationId = paymentRequest.CorrelationId,
                    Amount = paymentRequest.Amount,
                    RequestedAt = paymentRequest.RequestedAt.ToUnixTimeMilliseconds(),
                    ProcessorType = paymentApi.ProcessorType
                };

                using var scope = _serviceProvider.CreateScope();

                switch (BACKEND_ID)
                {
                    case "1":
                        var _paymentDbContext1 = scope.ServiceProvider.GetRequiredService<PaymentDbContext1>();
                        _paymentDbContext1.Payments.Add(paymentDb);
                        await _paymentDbContext1.SaveChangesAsync(cancellationToken);
                        break;

                    case "2":
                        var _paymentDbContext2 = scope.ServiceProvider.GetRequiredService<PaymentDbContext2>();
                        _paymentDbContext2.Payments.Add(paymentDb);
                        await _paymentDbContext2.SaveChangesAsync(cancellationToken);
                        break;

                    default:
                        throw new NotImplementedException();
                }

                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send/save payment for {CorrelationId} using {ProcessorType}: {Message}", paymentRequest.CorrelationId, paymentApi.ProcessorType, ex.Message);
        }

        return false;
    }
}
