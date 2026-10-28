# LocalStay — Microservices Event-Driven Hotel Booking Platform

LocalStay is a high-performance, event-driven hotel booking platform built with **.NET 10**, **PostgreSQL**, **Redis**, **RabbitMQ**, **YARP Reverse Proxy**, **Angular**, and **Local AI (Ollama llama3.1:8b)**.

---

## 🏗️ Architecture Diagram

```mermaid
flowchart TD
    Client["Client / Angular UI (Port 4200)"] -->|HTTP / JSON| Gateway["YARP API Gateway (Port 5000)"]

    subgraph "LocalStay Microservices"
        Gateway -->|"/api/catalog/**"| Catalog["CatalogService (Port 5101)"]
        Gateway -->|"/api/bookings/**"| Booking["BookingService (Port 5102)"]
        Gateway -->|"/api/notifications/**"| Notification["NotificationService (Port 5103)"]
        Gateway -->|"/api/concierge/**"| Concierge["ConciergeService (Port 5104)"]

        Catalog -->|Cache-Aside (5m TTL)| Redis[("Redis (Port 6379)")]
        Catalog -->|EF Core Migrations| CatalogDB[("catalog_db (PostgreSQL)")]

        Booking -->|Polly Retry v7.2.3 (GET)| Catalog
        Booking -->|Advisory Lock & Outbox| BookingDB[("booking_db (PostgreSQL)")]

        BookingDB -.->|Outbox Relay (5s Polling)| Relay["Outbox Background Relay"]
        Relay -->|Exchange: localstay.events| RabbitMQ[("RabbitMQ (Port 5672)")]
        RabbitMQ -->|Queue: localstay.notifications| Notification
        Notification -->|Idempotent Consumer| NotifDB[("notification_db (PostgreSQL)")]
        Notification -->|SMTP Email| MailHog[("MailHog (Port 8025 / 1025)")]

        Concierge -->|Pre-fetch Real Catalog| Catalog
        Concierge -->|Pre-fetch Real Availability| Booking
        Concierge -->|Grounded In-Context Prompt| Ollama["Local Ollama (Port 11434 / llama3.1:8b)"]
    end
```

---

## 🌐 Port & Service Mapping

| Service | Host Port | Internal Docker Port | Database / Dependency | Health / Ready Endpoint |
| :--- | :--- | :--- | :--- | :--- |
| **API Gateway (YARP)** | `5000` | `8080` | Downstream services | `GET /health` |
| **CatalogService** | `5101` | `8080` | `catalog_db`, Redis | `GET /health/ready` (Probes DB + Redis) |
| **BookingService** | `5102` | `8080` | `booking_db`, RabbitMQ | `GET /health/ready` (Probes DB) |
| **NotificationService**| `5103` | `8080` | `notification_db`, RabbitMQ | `GET /health/ready` (Probes DB) |
| **ConciergeService** | `5104` | `8080` | Catalog, Booking, Ollama | `GET /health/ready` (Probes Catalog + Ollama) |
| **Frontend (Angular)** | `4200` | `80` | API Gateway | Root UI `http://localhost:4200` |
| **PostgreSQL** | `5433` (Host) | `5432` | System databases | Port `5433` avoids conflicts with host PG |
| **RabbitMQ Management**| `15672` | `15672` | AMQP on 5672 | `http://localhost:15672` (localstay / localstay123) |
| **Redis** | `6379` | `6379` | In-memory cache | Cache-aside for Catalog properties |
| **MailHog Web UI** | `8025` | `8025` | SMTP on 1025 | `http://localhost:8025` |
| **Local Ollama** | `11434` | Host `11434` | llama3.1:8b | Reached via `host.docker.internal:11434` |

---

## 🚀 Quickstart & Prerequisites

### ⚠️ IMPORTANT: AI Model Setup
Before running the AI Concierge, download the model onto your host machine:
```bash
# Pull llama3.1:8b (approx 4.7 GB)
ollama pull llama3.1:8b
```
*(If Ollama is down or model is not pulled, Concierge returns a clear 503 explaining how to start Ollama without breaking the app).*

### Running via Docker Compose
To start the entire microservices stack (Infrastructure + Backend Services + Angular Frontend):
```bash
docker compose up -d --build
```

### Checking Stack Health
Run the automated PowerShell health verification probe:
```powershell
.\check-health.ps1
```

---

## 🛡️ Core Reliability & Architectural Highlights

### 1. Redis Cache-Aside with `X-Cache` Header
- `CatalogService` caches property listings for 5 minutes (`AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)`).
- Responses emit standard diagnostic headers:
  - First request: `X-Cache: MISS` (fetched from PostgreSQL, populated into Redis).
  - Subsequent requests: `X-Cache: HIT` (served from Redis memory in under 5ms).
- Dynamic room availability is **never** cached.
- Mutation endpoints (`POST`, `PUT`, `DELETE`) automatically invalidate the Redis cache key `catalog:properties`.

### 2. Polly Resilient Retries (GET-Only)
- Pinned to `Microsoft.Extensions.Http.Polly 7.2.3`.
- Exponential backoff policy (2s $\rightarrow$ 4s $\rightarrow$ 8s) attached **strictly** to idempotent Catalog GET requests via named `HttpClient("CatalogService")`.
- Non-idempotent operations (`POST /api/bookings`, cancels) do **not** use retry policies to prevent double-booking charges.

### 3. Concurrency Protection & Overlap Guard
- **Pre-Fix Race**: Under 50 concurrent VUs attempting to book the identical room on identical dates, a standard `AnyAsync` overlap check suffered from a check-then-save race condition, generating **49 duplicate bookings**.
- **Post-Fix Advisory Lock**:
  1. Room price fetched from Catalog before locking.
  2. Transaction begins with `IsolationLevel.Serializable`.
  3. Executes PostgreSQL transaction-scoped advisory lock: `SELECT pg_advisory_xact_lock(hashtext('room_' || @roomId));`.
  4. Re-checks date overlap inside the locked boundary.
  5. Inserts `Booking`, `Invoice`, and `OutboxMessage` atomically.
  6. Catches Postgres serialization failures (`40001` / `40P01`) and returns HTTP `409 Conflict`.
- **k6 Benchmark on Fresh Dates**: **1 created, 49 conflicts, 0 double bookings**.

### 4. Transactional Outbox & Idempotent Consumer
- Booking creation and event publishing are decoupled via a transactional outbox table in `booking_db`.
- Background relay polls unpublished messages every 5 seconds, publishes to RabbitMQ exchange `localstay.events`, and updates `PublishedAt`.
- `NotificationService` consumes messages idempotently using a unique database constraint on `(BookingId, Type)`.

### 5. Local AI Concierge (Port 5104)
- Exposes route `/api/concierge/ask` routed through YARP at `/api/concierge/**`.
- Grounded strictly in real Catalog and Booking availability data.
- Automatically refuses non-existent rooms or invented prices rather than hallucinating.
