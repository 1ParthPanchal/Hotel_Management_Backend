# LocalStay — End-to-End Execution Trace: 1 Booking in 6 Steps

This document traces the lifecycle of a single hotel room booking through the LocalStay architecture, citing exact source files, classes, and methods.

---

## 🧭 Step-by-Step Execution Lifecycle

```
[Angular UI] 
   │ 1. POST /api/bookings
   ▼
[YARP ApiGateway] 
   │ 2. Forward to BookingService:8080
   ▼
[BookingService: BookingsController]
   ├──> Query CatalogService: GET /api/catalog/properties/{id}/rooms (Polly retry 2s/4s/8s)
   ├──> Begin Serializable Transaction & pg_advisory_xact_lock(hash(RoomId))
   ├──> Re-check overlap query inside lock
   ├──> Save Booking + Invoice + OutboxMessage atomically
   └──> Commit & Release Lock
   │
   ▼ 3. Outbox Table (booking_db)
[OutboxRelayService (Background)]
   │ 4. Poll every 5s -> Publish "booking.created" to RabbitMQ (localstay.events)
   ▼
[RabbitMQ Exchange & Queue]
   │ 5. Deliver to queue localstay.notifications
   ▼
[NotificationService: RabbitMqConsumer]
   ├──> Check unique index (BookingId, Type)
   ├──> Persist notification record in notification_db
   └──> 6. Send confirmation email to MailHog (localhost:8025)
```

---

### Step 1: Client Request & Reverse Proxy Routing
- **File**: `Hotel_Management_Backend/src/ApiGateway/appsettings.json`
- **Route**: `POST /api/bookings`
- **Description**: The Angular frontend (or API client) sends a JSON payload containing `roomId`, `guestName`, `guestEmail`, `checkIn`, and `checkOut`. YARP matches route `booking-route` and proxies the request to `http://bookingservice:8080/api/bookings`.

### Step 2: External Dependency Resolution & Price Verification
- **File**: `Hotel_Management_Backend/src/BookingService/Controllers/BookingsController.cs`
- **Method**: `CreateBooking([FromBody] CreateBookingRequest request)`
- **Line Reference**: Fetching room pricing prior to acquiring database locks.
- **Description**: `BookingsController` invokes the named `HttpClient("CatalogService")` to retrieve property and room metadata (`GET api/catalog/properties/{propId}/rooms`). If CatalogService experiences momentary network hiccups, Polly's exponential backoff policy (configured in `Program.cs`) retries after 2s, 4s, and 8s before failing.

### Step 3: Concurrency Guard & Advisory Lock Acquisition
- **File**: `Hotel_Management_Backend/src/BookingService/Controllers/BookingsController.cs`
- **Method**: `CreateBooking`
- **Code Block**:
  ```csharp
  await using var tx = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable);
  var lockKey = Math.Abs(request.RoomId.GetHashCode());
  await _context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0});", lockKey);
  ```
- **Description**: To prevent double-booking under concurrent load, an advisory transaction lock is acquired on the hashed `RoomId`. Once inside the exclusive lock, the database re-evaluates the overlap query:
  ```csharp
  var hasOverlap = await _context.Bookings.AnyAsync(b =>
      b.RoomId == request.RoomId &&
      b.Status != "Cancelled" &&
      b.CheckIn < request.CheckOut &&
      b.CheckOut > request.CheckIn);
  ```
  If overlap exists, or if Postgres throws `40001 serialization_failure`, HTTP `409 Conflict` is returned immediately.

### Step 4: Atomic Persistence & Transactional Outbox
- **File**: `Hotel_Management_Backend/src/BookingService/Data/BookingDbContext.cs`
- **Tables Modified**: `Bookings`, `Invoices`, `OutboxMessages`
- **Description**: Within the single `Serializable` transaction:
  1. `Booking` record is added (`Status = "Confirmed"`).
  2. `Invoice` record is created with total amount calculated as `PricePerNight * TotalNights`.
  3. `OutboxMessage` record is added (`Type = "booking.created"`, `PublishedAt = null`).
  4. `await _context.SaveChangesAsync()` and `await tx.CommitAsync()` write all three entities atomically.

### Step 5: Asynchronous Outbox Relay
- **File**: `Hotel_Management_Backend/src/BookingService/Services/OutboxRelayService.cs`
- **Method**: `ExecuteAsync(CancellationToken stoppingToken)`
- **Description**: The background worker polls `OutboxMessages` where `PublishedAt == null` every 5 seconds. For each pending message, it publishes the payload to RabbitMQ exchange `localstay.events` with routing key `booking.created`. Upon broker acknowledgment, `PublishedAt` is set to `DateTime.UtcNow`, ensuring at-least-once delivery without distributed two-phase commits.

### Step 6: Idempotent Consumer & Email Dispatch
- **File**: `Hotel_Management_Backend/src/NotificationService/Consumers/BookingEventConsumer.cs`
- **Method**: `HandleBookingCreated(string message)`
- **Constraint**: Unique database index on `(BookingId, Type)`.
- **Description**: `NotificationService` consumes the message from `localstay.notifications`. If the `(BookingId, "booking.created")` tuple already exists, the message is acknowledged and skipped (idempotency guard). Otherwise, the notification is recorded in `notification_db` and an email is dispatched via SMTP to MailHog (`localhost:8025`).
