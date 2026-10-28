# Eliminating Double Bookings in Microservices: How We Fixed a 50-VU Concurrency Race with PostgreSQL Advisory Locks

Imagine running a flash sale or hotel booking app. 50 users click "Confirm Booking" at the exact same fraction of a second for the only Deluxe King Suite available for Easter weekend.

What happens in a conventional microservices backend?

We simulated this exact scenario on our platform, **LocalStay** (built with .NET 10, PostgreSQL, and RabbitMQ), using **k6** load testing. The initial results were alarming: **out of 50 concurrent requests, 49 duplicate bookings were created for the same room on the same dates.**

In this post, we explore why standard EF Core overlap checks fail under high concurrency and how we fixed it using **PostgreSQL Transaction-Scoped Advisory Locks (`pg_advisory_xact_lock`)** paired with `Serializable` transactions, achieving **1 created, 49 clean 409 conflicts, and zero double bookings**.

---

## The Illusion of Safety: Why `AnyAsync()` Fails

In most booking tutorials, you see code like this:

```csharp
// 1. Check overlap
var overlap = await context.Bookings.AnyAsync(b =>
    b.RoomId == roomId &&
    b.CheckIn < newCheckOut &&
    b.CheckOut > newCheckIn);

if (overlap) return Conflict("Room unavailable");

// 2. Insert booking
context.Bookings.Add(newBooking);
await context.SaveChangesAsync();
```

In single-user testing, this works flawlessly. But under concurrent load, this constitutes a classic **Check-Then-Act (Time-of-Check to Time-of-Use)** race condition:
1. VU 1 through VU 50 all execute Step 1 concurrently. Because no booking has been committed yet, `AnyAsync()` returns `false` for all 50 threads.
2. All 50 threads proceed to Step 2 and insert their bookings.
3. PostgreSQL in default `Read Committed` isolation level commits all 50 rows.

When we ran our k6 race test script against this endpoint:
```
k6 run load-tests/booking-race.js (50 VUs, same room, same dates)
```
**Pre-Fix Result:**
- `created_bookings`: 49
- `conflict_bookings`: 0
- `other_responses`: 1

We had 49 confirmed bookings for the exact same physical room on the exact same nights.

---

## Why Not Just Use Serializable Isolation?

If you simply elevate the transaction to `IsolationLevel.Serializable`:
- PostgreSQL uses Serializable Snapshot Isolation (SSI). When 50 concurrent transactions attempt to write overlapping predicate reads, PostgreSQL aborts 48–49 of them with `SqlState 40001 (serialization_failure)`.
- Without catching and classifying these errors, your API returns unhandled `500 Internal Server Errors` rather than business-meaningful `409 Conflict` responses.
- Furthermore, under intense contention, SSI can lead to retry storms and deadlocks.

---

## The Fix: PostgreSQL Transaction-Scoped Advisory Locks

To guarantee mutual exclusion without locking the entire `Bookings` table or blocking unrelated rooms, we used PostgreSQL's application-level locking primitives: **Advisory Locks**.

Specifically, `pg_advisory_xact_lock(key)`:
- Takes a 64-bit integer or 32-bit hash key.
- Obtains an exclusive lock scoped strictly to the current transaction.
- Is **automatically released** when the transaction commits or rolls back, avoiding orphaned locks.

### The Implementation in .NET 10

```csharp
// 1. Fetch external dependencies (e.g., Catalog price) BEFORE acquiring DB locks
var room = await _catalogClient.GetRoomAsync(request.RoomId);

// 2. Open a transaction
await using var tx = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable);

try
{
    // 3. Acquire exclusive advisory lock hashed on the Room ID
    var lockKey = Math.Abs(request.RoomId.GetHashCode());
    await _context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0});", lockKey);

    // 4. Re-check overlap INSIDE the lock boundary
    var hasOverlap = await _context.Bookings.AnyAsync(b =>
        b.RoomId == request.RoomId &&
        b.Status != "Cancelled" &&
        b.CheckIn < request.CheckOut &&
        b.CheckOut > request.CheckIn);

    if (hasOverlap)
    {
        await tx.RollbackAsync();
        return Conflict(new { error = "Room is already booked for these dates." });
    }

    // 5. Persist Booking + Invoice + OutboxMessage atomically
    _context.Bookings.Add(booking);
    _context.Invoices.Add(invoice);
    _context.OutboxMessages.Add(outbox);

    await _context.SaveChangesAsync();
    await tx.CommitAsync();

    return CreatedAtAction(nameof(GetById), new { id = booking.Id }, booking);
}
catch (Exception ex) when (IsSerializationFailure(ex))
{
    await tx.RollbackAsync();
    return Conflict(new { error = "Concurrent booking detected. Room unavailable." });
}
```

---

## The Verification: Before vs After k6 Benchmark

We executed the k6 50-VU race test on fresh dates (`2027-04-01` to `2027-04-03`) to prove the gap was closed:

| Metric | Pre-Fix (Default Check-Then-Save) | Post-Fix (Advisory Lock + Recheck) |
| :--- | :--- | :--- |
| **Virtual Users (VUs)** | 50 concurrent | 50 concurrent |
| **Created Bookings (201)** | **49** (Double bookings) | **1** (Exclusive Winner) |
| **Conflicts (409)** | **0** | **49** (Safely Handled) |
| **Errors (500)** | 1 | **0** |
| **Integrity Violation** | ❌ 49 duplicate rows in DB | ✅ Exactly 1 row in DB |

### Read Performance
Under a separate k6 read stress test ramped from 50 to 200 VUs:
- **Throughput**: 1,001 requests/second.
- **Total Requests**: 40,116 requests over 40 seconds.
- **Failures**: 0.00%.
- **Latency (p95)**: 165.7 ms (threshold was <1000 ms).

---

## Key Takeaways
1. **Never rely on un-isolated check-then-save logic** for scarce resources (hotel rooms, seats, tickets).
2. **`pg_advisory_xact_lock`** provides lightweight, fine-grained mutual exclusion at the entity level without bottlenecking other rows or tables.
3. **Always fetch slow external dependencies** (HTTP APIs, pricing engines) *before* acquiring transactional database locks to keep lock hold times minimal.
4. **Pair with Transactional Outbox** to ensure reliable downstream event publishing to RabbitMQ without distributed transactions.
