using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using NotificationService.Data;
using NotificationService.Models;

namespace NotificationService.Messaging;

/// <summary>
/// RabbitMQ consumer for booking events.
///
/// HOW THE EVENT FLOW WORKS (plain English):
/// 1. BookingService writes an outbox message when a booking is created/cancelled.
/// 2. The OutboxRelay publishes it to RabbitMQ exchange "localstay.events" with routing key "booking.created".
/// 3. This consumer is bound to the queue "localstay.notifications" with binding key "booking.*".
/// 4. On receiving a message, it checks if a Notification already exists for (BookingId, Type).
///    If it does, it ACKs the message and does nothing (idempotent consumer).
///    If it doesn't, it saves the notification and ACKs.
/// 5. The frontend polls GET /api/notifications every 10 seconds to show new notifications.
/// </summary>
public class BookingEventConsumer : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<BookingEventConsumer> _logger;
    private IConnection? _connection;
    private IModel? _channel;

    private const string ExchangeName = "localstay.events";
    private const string QueueName = "localstay.notifications";
    private const string BindingKey = "booking.*";

    public BookingEventConsumer(IServiceProvider services, ILogger<BookingEventConsumer> logger)
    {
        _services = services;
        _logger = logger;
    }

    public override async Task StartAsync(CancellationToken ct)
    {
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
                _connection = factory.CreateConnection();
                _channel = _connection.CreateModel();

                // Declare the exchange (must match what BookingService publishes to)
                _channel.ExchangeDeclare(ExchangeName, ExchangeType.Topic, durable: true);

                // Declare the queue and bind it to receive all "booking.*" events
                _channel.QueueDeclare(QueueName, durable: true, exclusive: false, autoDelete: false);
                _channel.QueueBind(QueueName, ExchangeName, BindingKey);

                // Only accept one message at a time (prefetch = 1)
                _channel.BasicQos(0, 1, false);

                _logger.LogInformation("Connected to RabbitMQ. Listening on queue: {Queue}", QueueName);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("RabbitMQ not ready (attempt {Attempt}/10): {Msg}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_channel is null) return Task.CompletedTask;

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.Received += async (_, ea) =>
        {
            try
            {
                var body = ea.Body.ToArray();
                var json = Encoding.UTF8.GetString(body);
                var routingKey = ea.RoutingKey; // "booking.created" or "booking.cancelled"

                _logger.LogInformation("Received event: {Key}", routingKey);
                await ProcessEventAsync(json, routingKey);

                _channel.BasicAck(ea.DeliveryTag, false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process message");
                // NACK without requeue to avoid infinite loop — dead letter in production
                _channel.BasicNack(ea.DeliveryTag, false, false);
            }
        };

        _channel.BasicConsume(QueueName, autoAck: false, consumer);
        return Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task ProcessEventAsync(string json, string routingKey)
    {
        var evt = JsonSerializer.Deserialize<BookingEvent>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        if (evt is null) return;

        var notificationType = routingKey == "booking.created"
            ? NotificationType.BookingCreated
            : NotificationType.BookingCancelled;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();

        // Idempotency check: if a notification already exists for this (BookingId, Type), skip
        var exists = await db.Notifications.AnyAsync(n =>
            n.BookingId == evt.Id && n.Type == notificationType);

        if (exists)
        {
            _logger.LogInformation("Duplicate event for Booking {Id} — skipping", evt.Id);
            return;
        }

        var message = routingKey == "booking.created"
            ? $"Booking #{evt.Id} confirmed for {evt.GuestName} (Room {evt.RoomId}). Total: {evt.TotalAmount:C}. Check-in: {evt.CheckIn:d}"
            : $"Booking #{evt.Id} for {evt.GuestName} was cancelled.";

        db.Notifications.Add(new Notification
        {
            BookingId = evt.Id,
            Type = notificationType,
            Message = message
        });

        await db.SaveChangesAsync();
        _logger.LogInformation("Saved notification for Booking {Id} ({Type})", evt.Id, notificationType);
    }

    public override void Dispose()
    {
        _channel?.Close();
        _connection?.Close();
        base.Dispose();
    }
}

// Matches the payload JSON written by BookingService's OutboxRelayService
public record BookingEvent(
    Guid EventId,
    int Id,           // BookingId (the "Id" field in the payload)
    int RoomId,
    string GuestName,
    DateTime CheckIn,
    DateTime CheckOut,
    decimal TotalAmount,
    DateTime OccurredAt
);
