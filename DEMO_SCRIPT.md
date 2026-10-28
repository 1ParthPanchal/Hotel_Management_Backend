# LocalStay — 3-Minute Live Demo Script

This script walks through a live demonstration of LocalStay's end-to-end functionality in 3 minutes, showcasing search, cache acceleration, booking, advisory lock conflict handling, async notification delivery, cancellation, and AI Concierge grounding.

---

## ⏱️ Timeline & Action Steps

### Minute 0:00 - 0:30 | Search & Cache Diagnostics
- **Action**: Open browser at `http://localhost:4200` or open PowerShell terminal.
- **Demo Command**:
  ```powershell
  # First GET: Cache MISS
  (Invoke-WebRequest -Uri "http://localhost:5000/api/catalog/properties").Headers["X-Cache"]
  # Returns: MISS

  # Second GET: Cache HIT (<5ms)
  (Invoke-WebRequest -Uri "http://localhost:5000/api/catalog/properties").Headers["X-Cache"]
  # Returns: HIT
  ```
- **Narration**: *"Notice how the catalog listings are accelerated using Redis cache-aside. The initial request fetches from PostgreSQL and emits X-Cache: MISS. The subsequent request is served directly from Redis memory in under 5 milliseconds with X-Cache: HIT."*

### Minute 0:30 - 1:15 | Room Availability & Booking Creation
- **Action**: In the Angular UI or via curl, select **Deluxe King Suite (ID 1)** for dates `2027-05-10` to `2027-05-12`.
- **Demo Command**:
  ```powershell
  # Check availability
  Invoke-RestMethod -Uri "http://localhost:5000/api/bookings/availability?roomId=1&checkIn=2027-05-10&checkOut=2027-05-12"
  # Returns: {"isAvailable": true}

  # Submit Booking
  $body = @{
    roomId = 1
    guestName = "Sarah Connor"
    guestEmail = "sarah@example.com"
    checkIn = "2027-05-10"
    checkOut = "2027-05-12"
  } | ConvertTo-Json
  Invoke-RestMethod -Uri "http://localhost:5000/api/bookings" -Method Post -ContentType "application/json" -Body $body
  # Returns: HTTP 201 Created with Booking ID
  ```
- **Narration**: *"The booking is persisted atomically with its invoice and an outbox record inside a PostgreSQL transaction."*

### Minute 1:15 - 1:45 | Concurrency Guard (409 Conflict Demo)
- **Action**: Attempt to book the exact same room and overlapping dates immediately.
- **Demo Command**:
  ```powershell
  try {
    Invoke-RestMethod -Uri "http://localhost:5000/api/bookings" -Method Post -ContentType "application/json" -Body $body
  } catch {
    $_.Exception.Response.StatusCode
    # Returns: 409 Conflict
  }
  ```
- **Narration**: *"PostgreSQL advisory locks (pg_advisory_xact_lock) serialize booking requests per room. Competing requests are safely rejected with HTTP 409 Conflict rather than allowing double bookings."*

### Minute 1:45 - 2:15 | Outbox Event Flow & Email Notification
- **Action**: Open MailHog web console at `http://localhost:8025`.
- **Narration**: *"Within 5 seconds, the OutboxRelay background service polls the outbox table, publishes 'booking.created' to RabbitMQ exchange localstay.events, and NotificationService consumes it idempotently to send the confirmation email visible here in MailHog."*

### Minute 2:15 - 2:40 | Cancellation & Lifecycle Completion
- **Action**: Cancel the booking via `POST /api/bookings/{id}/cancel`.
- **Demo Command**:
  ```powershell
  Invoke-RestMethod -Uri "http://localhost:5000/api/bookings/1/cancel" -Method Post
  ```
- **Narration**: *"The booking status transitions to 'Cancelled', emitting a 'booking.cancelled' event that updates the user's notification feed."*

### Minute 2:40 - 3:00 | Local AI Concierge & Anti-Hallucination Guard
- **Action**: Test the AI Concierge with both a valid room and a hallucinated room:
  ```powershell
  # Grounded query
  Invoke-RestMethod -Uri "http://localhost:5000/api/concierge/ask" -Method Post -ContentType "application/json" -Body '{"question":"What rooms are available in Riyadh?","roomId":1}'

  # Anti-Hallucination refusal
  Invoke-RestMethod -Uri "http://localhost:5000/api/concierge/ask" -Method Post -ContentType "application/json" -Body '{"question":"Can I book Room 999 in Paris?","roomId":999}'
  ```
- **Narration**: *"Our AI Concierge is pre-grounded with real database state. When asked about fake Room 999 or Paris, it strictly refuses rather than hallucinating prices."*

---

## 🛠️ Fallback Strategies During Demo

| Scenario | Immediate Fallback |
| :--- | :--- |
| **Redis down or restarted** | Request automatically falls through to PostgreSQL and emits `X-Cache: MISS`. Check via `docker compose ps redis`. |
| **RabbitMQ delay** | The Outbox relay polls every 5 seconds; wait 5s or inspect `OutboxMessages` table in `booking_db`. |
| **Ollama cold start or down** | Endpoint returns a clean HTTP 503 explaining how to start Ollama (`ollama run llama3.1:8b`) without breaking frontend navigation. |
