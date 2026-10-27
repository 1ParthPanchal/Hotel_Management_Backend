using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using BookingService.Data;
using BookingService.Models;

namespace BookingService.Messaging;

/// <summary>
/// Outbox Relay — runs as a background service, polling the OutboxMessages table every 5 seconds.
///
/// HOW THE TRANSACTIONAL OUTBOX WORKS (plain English):
/// 1. When a booking is created/cancelled, the BookingController saves BOTH the booking
///    AND an OutboxMessage row inside ONE database transaction.
/// 2. If the app crashes before publishing to RabbitMQ, the DB transaction is still committed.
///    The outbox row remains unpublished.
/// 3. This relay wakes up every 5 seconds, finds unpublished rows, publishes them to RabbitMQ,
///    then marks them as published (PublishedAt = now).
/// 4. This guarantees at-least-once delivery: the message will be delivered even after a crash.
///    NotificationService handles duplicates by checking for an existing notification first.
/// </summary>
public class OutboxRelayService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<OutboxRelayService> _logger;
    private IConnection? _rabbitConnection;
    private IModel? _channel;

    private const string ExchangeName = "localstay.events";
    private const int PollIntervalSeconds = 5;

    public OutboxRelayService(IServiceProvider services, ILogger<OutboxRelayService> logger)
    {
        _services = services;
        _logger = logger;
    }

    public override async Task StartAsync(CancellationToken ct)
    {
        // Keep retrying until RabbitMQ is ready (Docker startup race)
        await ConnectWithRetryAsync(ct);
        await base.StartAsync(ct);
    }

    private async Task ConnectWithRetryAsync(CancellationToken ct)
    {
        var rabbitHost = Environment.GetEnvironmentVariable("RabbitMQ__Host") ?? "localhost";
        var rabbitUser = Environment.GetEnvironmentVariable("RabbitMQ__User") ?? "localstay";
        var rabbitPass = Environment.GetEnvironmentVariable("RabbitMQ__Password") ?? "localstay123";

        var factory = new ConnectionFactory
        {
            HostName = rabbitHost,
            UserName = rabbitUser,
            Password = rabbitPass,
            DispatchConsumersAsync = true
        };

        for (int attempt = 1; attempt <= 10; attempt++)
        {
            try
            {
                _rabbitConnection = factory.CreateConnection();
                _channel = _rabbitConnection.CreateModel();

                // Declare a topic exchange. Durable = survives RabbitMQ restart.
                _channel.ExchangeDeclare(ExchangeName, ExchangeType.Topic, durable: true);
                _logger.LogInformation("Connected to RabbitMQ on {Host}", rabbitHost);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("RabbitMQ not ready (attempt {Attempt}/10): {Msg}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
        _logger.LogError("Could not connect to RabbitMQ after 10 attempts. Outbox relay will not function.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await PublishPendingMessagesAsync();
            await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), stoppingToken);
        }
    }

    private async Task PublishPendingMessagesAsync()
    {
        if (_channel is null || !_channel.IsOpen) return;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BookingDbContext>();

        // Load all unprocessed outbox messages ordered by creation time
        var pending = await db.OutboxMessages
            .Where(m => m.PublishedAt == null)
            .OrderBy(m => m.CreatedAt)
            .Take(50)
            .ToListAsync();

        foreach (var message in pending)
        {
            try
            {
                var body = Encoding.UTF8.GetBytes(message.Payload);
                var props = _channel.CreateBasicProperties();
                props.Persistent = true; // Survive RabbitMQ restart
                props.ContentType = "application/json";

                // Routing key: "booking.created" or "booking.cancelled"
                // The notification queue is bound to "booking.*" so it receives both
                _channel.BasicPublish(ExchangeName, message.EventType, props, body);

                message.PublishedAt = DateTime.UtcNow;
                message.Attempts++;
                _logger.LogInformation("Published outbox message {Id} with key {Key}", message.Id, message.EventType);
            }
            catch (Exception ex)
            {
                message.Attempts++;
                _logger.LogError(ex, "Failed to publish outbox message {Id}", message.Id);
            }
        }

        if (pending.Any())
            await db.SaveChangesAsync();
    }

    public override void Dispose()
    {
        _channel?.Close();
        _rabbitConnection?.Close();
        base.Dispose();
    }
}
