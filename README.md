# Material Allocation API

A production-quality inventory allocation backend built with ASP.NET Core 8, PostgreSQL, EF Core 8, and Dapper. The domain models a constrained-inventory problem where competing orders must be filled from a shared pool of abstract SKUs under strict transactional guarantees — a pattern that directly mirrors semiconductor supply chain allocation (Micron-style).

## Domain Model — How It Maps to a Real Supply Chain

In a memory/storage supply chain, a fixed quantity of finished goods (DIMMs, NAND modules) is allocated to customers in priority order. Multiple orders arrive simultaneously, referencing the same SKU. The core problem is: under concurrent demand, no order should receive more units than are available, and no unit should be counted twice.

```
Sku ──────────────────────────── one SKU = one product variant (e.g. DDR5-16G, NAND-1T)
 └── InventoryAdjustment         signed audit record every time on_hand changes outside allocation
Order ────────────────────────── one order = one allocation request from one customer
 └── OrderLine                   one line per SKU requested — qty requested vs. qty allocated
      ├── Reservation            optional soft hold on available units for a TTL window
      └── AllocationEvent        immutable ledger entry written every time units move
```

### Sku

A `Sku` represents a single product variant. `SkuCode` is the natural key (e.g. `MEM-DDR5-16G`). `OnHand` is the mutable available-to-allocate count: it decrements atomically when units are allocated and increments when an allocation is cancelled. `Version` is an EF Core concurrency token — it increments on every write and is used to detect concurrent modifications on the adjust endpoint.

### InventoryAdjustment

Each `POST /skus/{id}/adjust` call writes an `InventoryAdjustment` row alongside the `on_hand` update, providing a full audit trail of every stock movement that happened outside the allocation flow (receipts, write-offs, corrections).

### Order

An `Order` carries a `ReferenceCode` (caller-assigned natural key), a `Priority` (`standard`, `high`, `critical`), and a `Status` that tracks its lifecycle: `open` → `partially_allocated` → `fully_allocated`, or `open` → `cancelled`. Status is recomputed from line quantities after every allocation run.

### OrderLine

An `OrderLine` is the allocation unit: one line per SKU per order, with `RequestedQty` (what was asked for) and `AllocatedQty` (what has been committed so far). The database enforces `allocated_qty <= requested_qty` via a CHECK constraint.

### Reservation

A `Reservation` places a soft hold on units for a specific order line until `ExpiresAt`. The availability formula becomes `available = on_hand - reserved` (where `on_hand` already reflects committed allocations). A background job expires stale reservations. Calling reserve again for the same order replaces the existing reservation (TTL refresh).

### AllocationEvent

Every time units move — committed, released, or soft-held — an `AllocationEvent` row is written inside the same transaction. The table is append-only (all FKs use `RESTRICT` delete behaviour so no event can be silently removed when an order or line is deleted). `EventType` is one of five values:

| Value | When written |
|---|---|
| `allocation_committed` | `AllocateAsync` — units moved from `on_hand` to an order line |
| `allocation_released` | `CancelAsync` — allocated units returned to `on_hand` |
| `reservation_created` | `ReserveAsync` — units soft-held under a TTL |
| `reservation_released` | `ReleaseAsync` — reservation explicitly removed before TTL |
| `reservation_expired` | `ExpireAsync` (background job) — TTL elapsed, inserted via CTE alongside the `DELETE` |

`GET /orders/{id}/events` returns the full chronological event history for an order.

---

## Features

- **SKU Catalog** — create SKUs with initial on-hand quantity; paginated list ordered by SKU code; get by ID
- **Inventory Adjustments** — signed delta adjustments with mandatory reason text; full audit trail in `inventory_adjustments`; optimistic concurrency on adjust with 409 on version conflict
- **Availability Query** — `GET /skus/{id}/availability` returns `on_hand`, `reserved`, and `available = on_hand - reserved` in one read
- **Customer Management** — register customers with a unique code, name, and tier (`tier1` contracted, `tier2` partial contract, `tier3` spot/priority-only); paginated list ordered by customer code; `customer_id` nullable FK on orders links every order to a customer for Phase 17 contract enforcement
- **Order Management** — create orders with one or more lines referencing existing SKUs; optional `customerId` FK validated at write time; paginated list with optional `status` and `customerId` filters; `GET /orders/{id}` returns `customerCode` and `customerName` via LEFT JOIN; unique `reference_code` enforced at DB level
- **Allocation Run** — `POST /orders/{id}/allocate` fills open lines against current stock in a single transaction; pessimistic `SELECT … FOR UPDATE` on SKU rows in deterministic (sku_id ascending) order prevents deadlocks; partial allocation allowed — unfulfilled lines remain open; response states partial vs. full explicitly
- **Async Priority Allocation Run** — `POST /allocations/run` returns `202 Accepted` immediately with a `runId`; a background worker (`AllocationRunWorker`) claims the run via `SELECT … FOR UPDATE SKIP LOCKED`, executes the priority-ordered allocation loop, and persists the outcome; `GET /allocations/runs/{id}` polls status (`pending` → `running` → `completed` | `failed`) and returns aggregated stats (`ordersProcessed`, `ordersFullyAllocated`, `ordersPartiallyAllocated`) and a per-order result list once complete; `GET /allocations/runs` lists the 20 most recent runs newest-first; `409` is returned if a run is already `pending` or `running` — the response body includes the in-progress run ID so the caller can poll it instead of retrying blindly; `AllocationRunHealthCheck` degrades when any run has been in `running` state for more than 15 minutes (worker may be stuck)
- **Cancellation & Release** — `POST /orders/{id}/cancel` transitions the order to `cancelled` and atomically restores `on_hand` from each line's `allocated_qty`; cancel uses the same lock order as allocate to prevent concurrent allocation/cancel deadlocks
- **Reservations** — `POST /orders/{id}/reserve` places a TTL-bounded hold per line against available stock; own-order reservation does not block own allocation; calling reserve again replaces the existing hold (idempotent TTL refresh)
- **Reservation Release** — `POST /reservations/{id}/release` explicitly removes a reservation before it expires
- **Reservation Expiry Job** — `ReservationExpiryJob` runs on a configurable interval (default 60 s) and deletes all rows where `expires_at <= NOW()`, restoring their quantity to availability automatically
- **Shortage Rollup** — `GET /rollup/sku-shortages` returns a paged, shortage-descending list of SKUs where open unfulfilled demand exceeds available stock; open demand accounts for active reservations (a line covered by a reservation does not count as unmet demand); pure Dapper read with a single CTE shared between the COUNT and the paged SELECT
- **Allocation Event Ledger** — every unit movement writes an immutable `AllocationEvent` row inside the same transaction: `AllocationCommitted` (allocate), `AllocationReleased` (cancel), `ReservationCreated` (reserve), `ReservationReleased` (explicit release), `ReservationExpired` (background job via CTE); FKs to `orders`, `order_lines`, and `skus` all use `RESTRICT` so history survives order lifecycle changes; `GET /orders/{id}/events` returns the full chronological event list
- **Transactional Outbox** — every write that produces a domain event appends an `OutboxMessage` row (event type + JSON payload) inside the same EF Core `SaveChangesAsync` call as the business write, guaranteeing the two are atomic; `OutboxRelayJob` (`BackgroundService`) polls the table on a configurable interval, delivers each message via `IEventPublisher`, and marks it `processed_at`; failures are recorded in `error` (leaving `processed_at` null) so the row stays eligible for the next relay pass; `OutboxLagHealthCheck` surfaces degraded status when the oldest unprocessed message is older than two minutes; current publisher implementation is `LoggingEventPublisher` — drop-in replacement for any real transport (Kafka, SNS, etc.)
- **Idempotency** — optional `Idempotency-Key` header (max 128 chars; UUID v4 recommended) on any `POST` endpoint (except `/api/v1/auth/token`); `IdempotencyMiddleware` inserts a `processing` record using a database unique constraint as the atomic lock — a duplicate-key violation on a concurrent request returns `409 IDEMPOTENCY_IN_FLIGHT`; once the upstream response is buffered, 2xx and 4xx outcomes are stored and replayed on subsequent requests with the same key (5xx outcomes are not stored so the client can safely retry); replayed responses carry `X-Idempotency-Replayed: true`; reusing a key for a different path returns `422 IDEMPOTENCY_KEY_MISMATCH`; `IdempotencyCleanupJob` (`BackgroundService`) purges expired `complete` records and stuck `processing` records on a configurable interval; Swagger UI exposes the header on all qualifying `POST` operations via `IdempotencyHeaderOperationFilter`
- **JWT Authentication** — `POST /api/v1/auth/token` issues a signed JWT for a requested role (development-use endpoint; in production, tokens come from your identity provider); JWT Bearer validation is wired in `Program.cs`; all controllers carry `[Authorize]` at the class level; unauthenticated requests return a standard-envelope `401`; forbidden role combinations return `403`
- **Role-Based Access Control (RBAC)** — four roles enforced via `[Authorize(Roles = "...")]` at the action level: `warehouse-ops` (create SKU, adjust inventory), `sales-ops` (create order, cancel order), `allocation-manager` (allocate, reserve, release reservation, run global allocation), `read-only` (all GET endpoints — no specific role required beyond authentication); `POST /api/v1/auth/token` is `[AllowAnonymous]`
- **Standard Envelope** — all responses use `{ success, statusCode, data, error }`; validation errors use the same shape; `[ApiController]` model-validation is overridden to produce the standard envelope instead of `ValidationProblemDetails`
- **Structured Logging** — every write operation logs its outcome via `ILogger<T>` after the transaction commits (order created/cancelled/allocated, SKU created/adjusted, reservation reserved/released/expired); Serilog enriches every entry with `MachineName`, `ThreadId`, and `CorrelationId`
- **Correlation ID** — `CorrelationIdMiddleware` accepts an inbound `X-Correlation-ID` header (or generates a 12-char random ID); echoes it on the response; pushes it into Serilog's `LogContext` so every log entry within the request carries it automatically
- **Health Checks** — `/health` (all checks), `/health/live` (process-up, no dependencies), `/health/ready` (PostgreSQL probe via Npgsql); returns `503` when the database is unreachable
- **Swagger UI** — OpenAPI spec (Swashbuckle) with XML doc comments and a full API description, available at `/swagger` in Development
- **Two-Role DB** — migrations run under a privileged migrator role; the app role (`dotnetter`) holds DML-only grants, so a compromised app process cannot alter schema
- **SKU Seed Data** — 5 representative memory/NAND SKUs seeded on first startup (idempotent)

---

## Architecture

```
HTTP request
  → CorrelationIdMiddleware    (assigns / echoes X-Correlation-ID; pushes CorrelationId into Serilog LogContext)
  → ExceptionHandlerMiddleware (maps domain exceptions to standard envelope; logs warnings/errors with request path)
  → Serilog request logging    (one structured log line per request: method, path, status, elapsed, correlation ID)
  → IdempotencyMiddleware      (POST-only; claims key via DB unique constraint; replays stored 2xx/4xx on duplicate;
                                 buffers response body; skips /api/v1/auth/token)
  → UseAuthentication          (JWT Bearer validation; 401 on missing/invalid token)
  → UseAuthorization           (role check; 403 on insufficient role)
  → Controllers
  → Services                   (all writes log outcome via ILogger<T> after commit;
                                 domain-event writes append OutboxMessage inside same SaveChangesAsync)
  → EF Core / Dapper
  → PostgreSQL

Background workers
  → ReservationExpiryJob     (expires stale reservations; inserts reservation_expired events via CTE)
  → OutboxRelayJob           (polls outbox_messages; delivers via IEventPublisher; marks processed_at or error)
  → IdempotencyCleanupJob    (deletes expired 'complete' records and stuck 'processing' records)
  → AllocationRunWorker      (polls allocation_runs for pending rows via SKIP LOCKED; executes
                               RunPriorityAllocationAsync; persists completed/failed outcome)
```

Services are the only layer that touches the database. Controllers translate HTTP concerns (query params, status codes, response envelope) and delegate all business logic to the service layer. EF Core handles writes, aggregate loads, and raw-SQL locking queries; Dapper is used for multi-result read queries (order details with lines, paginated lists) where the result shape doesn't map cleanly to aggregate roots.

## Tech Stack

| Layer | Technology |
|---|---|
| Server | ASP.NET Core 8 (.NET 8.0) |
| Database | PostgreSQL 15+ with EF Core 8 (code-first migrations) |
| Micro-ORM | Dapper 2.1 |
| Logging | Serilog — structured logs to Console + Seq; enriched with `MachineName`, `ThreadId`, `CorrelationId` |
| Background workers | `BackgroundService` (`ReservationExpiryJob`, `OutboxRelayJob`, `IdempotencyCleanupJob`, `AllocationRunWorker`) |
| Auth | JWT Bearer (`Microsoft.AspNetCore.Authentication.JwtBearer`); HMAC-SHA256 signed tokens |
| Docs | Swagger / OpenAPI (Swashbuckle) with XML doc comments and JWT Bearer security definition |
| Testing | xUnit + `WebApplicationFactory<Program>` — real Postgres database |

---

## Project Structure

```
MaterialAllocationApi/
├── Program.cs                              # Service registration, middleware, migration on startup, SKU seed
├── appsettings.json                        # Connection strings, Serilog, ReservationExpiry, OutboxRelay, Authentication
├── Controllers/
│   ├── SkusController.cs                   # [warehouse-ops] create, adjust; any-auth get, list, availability
│   ├── OrdersController.cs                 # [sales-ops] create, cancel; [allocation-manager] allocate, reserve; any-auth get, list, events
│   ├── CustomersController.cs              # [sales-ops] create; any-auth get, list
│   ├── AllocationController.cs             # [allocation-manager] POST /allocations/run — global priority run
│   ├── ReservationsController.cs           # [allocation-manager] release
│   ├── RollupController.cs                 # Any-auth GET sku-shortages
│   └── AuthController.cs                   # [AllowAnonymous] POST /api/v1/auth/token — issues JWT for a role
├── Domain/Entities/
│   ├── Sku.cs                              # AllocateUnits(), ReleaseUnits(); version concurrency token
│   ├── InventoryAdjustment.cs
│   ├── Customer.cs                         # customerCode (unique), name, tier; private ctor for EF
│   ├── Order.cs                            # Cancel(), RecomputeStatus() from line quantities; nullable CustomerId FK
│   ├── OrderLine.cs                        # Allocate(), ReleasedAllocation()
│   ├── Reservation.cs
│   ├── AllocationEvent.cs                  # Immutable ledger row — EventType, OrderId, OrderLineId, SkuId, Quantity, OccurredAt
│   ├── OutboxMessage.cs                    # processing / complete state; MarkProcessed(), MarkFailed(error)
│   ├── IdempotencyRecord.cs               # IdempotencyKey, RequestPath, Status (processing|complete), ResponseStatus, ResponseBody, ExpiresAt; Complete()
│   └── AllocationRun.cs                   # pending→running→completed|failed state machine; MarkRunning(), Complete(response, json), Fail(error)
├── Domain/Enums/
│   ├── OrderPriority.cs
│   ├── OrderStatus.cs
│   ├── CustomerTier.cs                     # Tier1 | Tier2 | Tier3; ToDbString() / FromDbString() extensions
│   └── AllocationEventType.cs              # AllocationCommitted | AllocationReleased | ReservationCreated | ReservationReleased | ReservationExpired
├── Domain/
│   └── AllocationEventTypeExtensions.cs    # ToDbString() / FromDbString() for AllocationEventType
├── Models/Records/
│   ├── SkuRecords.cs                       # CreateSkuRequest / AdjustSkuRequest / SkuResponse
│   ├── OrderRecords.cs                     # CreateOrderRequest (+ optional CustomerId) / OrderResponse (+ CustomerId, CustomerCode, CustomerName) / OrderSummaryResponse (+ CustomerId, CustomerCode)
│   ├── CustomerRecords.cs                  # CreateCustomerRequest / CustomerResponse
│   ├── AllocationRecords.cs                # AllocationResponse / AllocationLineResult / AvailabilityResponse / AllocationRunResponse / AllocationRunResult / AllocationEventResponse / AllocationRunAcceptedResponse / AllocationRunStatusResponse / AllocationRunSummary / EnqueueResult (discriminated union)
│   ├── ReservationRecords.cs               # ReserveRequest / ReservationResponse / ReservationLineResult
│   ├── RollupRecords.cs                    # SkuShortageResponse
│   └── AuthRecords.cs                      # TokenRequest / TokenResponse
├── Services/
│   ├── Interfaces/
│   │   ├── ISkuService.cs
│   │   ├── ICustomerService.cs             # CreateAsync, GetByIdAsync, ListAsync
│   │   ├── IOrderService.cs
│   │   ├── IAllocationService.cs
│   │   ├── IAllocationRunService.cs        # EnqueueAsync → EnqueueResult; GetByIdAsync; ListRecentAsync
│   │   ├── IReservationService.cs
│   │   ├── IRollupService.cs
│   │   ├── IEventPublisher.cs              # PublishAsync(OutboxMessage) — implemented by LoggingEventPublisher
│   │   └── ITokenService.cs                # IssueToken(role) — implemented by JwtTokenService
│   ├── SkuService.cs                       # Create, GetById, List, AdjustAsync (optimistic concurrency)
│   ├── CustomerService.cs                  # CreateAsync (EF write + unique-violation guard); GetByIdAsync, ListAsync (Dapper reads)
│   ├── OrderService.cs                     # Create (+ customer FK validation), GetById (LEFT JOIN customers), List (+ customerId filter), CancelAsync (TX + FOR UPDATE); emits AllocationReleased events + outbox
│   ├── AllocationService.cs                # AllocateAsync (TX + FOR UPDATE), GetAvailabilityAsync, RunPriorityAllocationAsync, GetEventsAsync; emits AllocationCommitted events + outbox
│   ├── AllocationRunService.cs             # EnqueueAsync (EF Core write); GetByIdAsync / ListRecentAsync (Dapper reads); JSON deserialise results
│   ├── ReservationService.cs               # ReserveAsync (TX + FOR UPDATE), ReleaseAsync, ExpireAsync (CTE); emits Reservation* events + outbox
│   ├── RollupService.cs                    # GetSkuShortageAsync — Dapper CTE, no writes
│   ├── EventPublisher.cs                   # LoggingEventPublisher: logs event type + payload; swap for Kafka/SNS in production
│   ├── JwtTokenService.cs                  # IssueToken — HMAC-SHA256 signed JWT with role claim; expiry from AuthSettings
│   └── IdempotencyCleanupJob.cs            # BackgroundService — deletes expired 'complete' records and stuck 'processing' records
├── Jobs/
│   ├── AllocationRunWorker.cs              # BackgroundService — polls pending runs via FOR UPDATE SKIP LOCKED; calls RunPriorityAllocationAsync; persists completed/failed; configurable PollIntervalSeconds
│   ├── ReservationExpiryJob.cs             # BackgroundService — CTE: DELETE + INSERT allocation_events in one query
│   ├── OutboxRelayJob.cs                   # BackgroundService — polls outbox_messages; delivers via IEventPublisher; marks processed_at or error
│   └── IdempotencyCleanupJob.cs            # BackgroundService — deletes expired 'complete' records and stuck 'processing' records
├── Data/
│   ├── AllocationDbContext.cs              # EF Core context — entity configs, indexes, CHECK constraints; AllocationRuns DbSet added
│   ├── IDbConnectionFactory.cs / NpgsqlConnectionFactory.cs
│   ├── TransactionHelper.cs                # RollbackAsync — safe rollback that swallows secondary exceptions
│   └── Seed/SkuSeeder.cs                   # Seeds 5 memory/NAND SKUs on first startup (idempotent)
├── Common/
│   ├── ApiResponse.cs                      # Generic response envelope + ApiError
│   ├── PagedResult.cs
│   ├── Helper.cs                           # Helpers.Serialize(obj) — camelCase JSON serializer used by outbox payload builders
│   ├── Config/
│   │   ├── AuthSettings.cs                 # JwtSecret, Issuer, Audience, TokenExpiryMinutes
│   │   ├── OutboxRelaySettings.cs          # IntervalSeconds, BatchSize
│   │   └── IdempotencySettings.cs          # ExpiryHours, CleanupIntervalSeconds, StuckProcessingAgeMinutes
│   ├── Health/
│   │   ├── OutboxLagHealthCheck.cs         # Degraded when oldest unprocessed outbox message > 2 minutes old
│   │   └── AllocationRunHealthCheck.cs     # Degraded when any run has been in 'running' state > 15 minutes
│   ├── Swagger/
│   │   └── IdempotencyHeaderOperationFilter.cs  # Adds optional Idempotency-Key header to all POST operations in Swagger UI
│   └── Exceptions/
│       ├── NotFoundException.cs
│       ├── ConflictException.cs
│       └── ValidationException.cs
├── Middleware/
│   ├── CorrelationIdMiddleware.cs          # Assigns/echoes X-Correlation-ID; pushes into Serilog LogContext
│   ├── ExceptionHandlerMiddleware.cs       # Maps domain exceptions to standard envelope + status codes; logs with request path
│   └── IdempotencyMiddleware.cs            # Atomic key claim via DB unique constraint; response buffering; replay on duplicate
└── Migrations/                             # EF Core migration history (includes AddIdempotencyKeys)

MaterialAllocationApi.Tests/
├── Fixtures/
│   └── ApiFixture.cs                       # WebApplicationFactory<Program>; MigrateAsync on init; ResetDatabaseAsync between tests
│                                           # (including allocation_runs); removes OutboxRelayJob + AllocationRunWorker from test host;
│                                           # replaces JWT Bearer with TestAuthHandler
├── Helpers/
│   └── AllocationTestBase.cs               # HTTP helpers (CreateSkuAsync, CreateOrderAsync, AllocateAsync, SubmitAllocationRunAsync,
│                                           # PollRunUntilCompleteAsync, TriggerAllocationWorkerAsync); DB assertion helpers;
│                                           # AuthorizeAs(roles) / AuthorizeAsAll() role-header helpers
├── CustomerTests.cs                        # 11 tests: customer CRUD (create, duplicate 422, get by ID, 404, paginated list); order linkage (valid/null/invalid customerId, list filter, summary customerCode, allocate→cancel cycle with customerId preserved)
├── Allocation/
│   ├── AllocationFlowTests.cs              # Partial allocation, sequential top-up, over-demand, priority run (critical before standard)
│   ├── AllocationRunTests.cs               # 9 async run lifecycle tests: 202 + poll, state transitions, per-order results, 409 conflict,
│   │                                       # 202-after-complete, 404 unknown ID, list newest-first, zero-orders run,
│   │                                       # fault injection (IAllocationService override via WithWebHostBuilder)
│   ├── ConcurrentAllocationTests.cs        # Two parallel allocates on the same SKU — exactly one wins
│   ├── CancelTests.cs                      # Cancel + inventory restoration; cancel with no allocations
│   ├── ReservationTests.cs                 # Reserve, block, own-order exception, TTL refresh, release, expiry, cancel-deletes
│   └── AllocationAuditTests.cs             # 7 event-ledger tests: committed, released, reservation lifecycle, zero-stock guard, partial-stock guard
├── Auth/
│   ├── TestAuthHandler.cs                  # Fake auth scheme: reads X-Test-Role header; returns NoResult() (→ 401) when absent
│   └── RbacTests.cs                        # 19 RBAC tests: anonymous token endpoint, 401 on no auth, 403 on wrong role, 2xx on correct role
├── Idempotency/
│   └── IdempotencyTests.cs                 # Idempotency middleware integration tests
├── Outbox/
│   └── OutboxPatternTests.cs               # 8 outbox tests: written on allocation/cancel/reserve/release/expiry, relay marks processed, relay records error, atomicity
└── Rollup/
    └── RollupTests.cs                      # 6 shortage tests: empty, fully allocated, open, partial, reservations, ordering
```

---

## Architecture Decisions

### Pessimistic locking with deterministic lock order

Allocation and cancellation both run inside explicit transactions and lock SKU rows via `SELECT * FROM skus WHERE id = ANY(@ids) ORDER BY id FOR UPDATE`. Sorting by `sku_id` ascending before acquiring locks is the key invariant: any two concurrent transactions that share SKUs will acquire their locks in the same order, eliminating circular waits.

Optimistic concurrency (`version` token on `Sku`) is used only on the adjust endpoint, where contention is low and a retry is cheap. Allocation and cancel use pessimistic locking because they read and immediately write multiple rows — a lost-update under optimistic concurrency would require re-reading and re-running the entire allocation loop, which is more complex for no real benefit at expected concurrency levels.

### Mutable `on_hand` vs. ledger

`on_hand` is a mutable integer that is decremented atomically at allocation commit time. This keeps availability queries simple: `available = on_hand - reserved`. A ledger-based model (sum of event rows) would be more auditable but adds query complexity without changing the core invariants. The `inventory_adjustments` table provides an audit trail for manual stock movements.

### Availability formula

```
available = on_hand - reserved
```

`on_hand` already excludes committed allocations (allocation decrements it at commit). `reserved` is the sum of non-expired reservation quantities across all orders' lines for this SKU. Allocated units are not subtracted again — they were already removed from `on_hand` at the time they were committed.

### Reservations inside the allocation lock

`ReserveAsync` acquires the same `FOR UPDATE` lock on SKU rows before reading reservation totals from other orders. This guarantees that the reservation count read inside the transaction sees the latest committed state — no concurrent reserve or allocate can insert a new reservation for these SKUs between the lock acquisition and the commit.

### Sequential priority ordering in the allocation run

`RunPriorityAllocationAsync` fetches all non-terminal orders sorted by priority (`critical` = 0, `high` = 1, `standard` = 2) then `CreatedAt`, and calls `AllocateAsync` for each sequentially. Running allocations in parallel would re-introduce the circular-wait risk that the deterministic lock order prevents within a single allocation — and would make priority ordering meaningless, since a `standard` order could grab stock before a `critical` order's transaction commits. Sequential processing is the simplest correct model at expected batch sizes; each call acquires and releases its own `FOR UPDATE` locks independently, so all Phase 4 invariants hold without modification.

### EF Core + Dapper together

EF Core owns writes (aggregate mutations, reservation inserts) and raw-SQL locking queries (`FromSqlRaw`). Dapper is used for multi-result read queries (order detail with lines, paginated order list) where mapping a tuple result to a domain aggregate is unnecessary overhead and raw SQL is clearer.

### Two-role database

Migrations run under `material_allocation_migrator` (DDL privileges). The app runs as `dotnetter` (DML-only: `SELECT`, `INSERT`, `UPDATE`, `DELETE`). A compromised app process cannot drop or alter tables. `ALTER DEFAULT PRIVILEGES FOR ROLE material_allocation_migrator` ensures new tables automatically grant DML to `dotnetter`.

### Idempotency via database unique constraint

`IdempotencyMiddleware` uses the `idempotency_keys` table's unique index on `idempotency_key` as the atomic lock rather than application-level compare-and-set. On the first request, the middleware inserts a `processing` record. If a second request with the same key races in before the first completes, the `INSERT` fails with a unique-constraint violation, which is caught and converted to `409 IDEMPOTENCY_IN_FLIGHT`. This approach is correct under any level of horizontal scaling without requiring a distributed lock.

The response is buffered in memory (`MemoryStream`) so it can be both written to the client and stored in the database in a single pass. Only `2xx` and `4xx` outcomes are persisted — `5xx` responses may be transient, so the record stays in `processing` state and is cleaned up by `IdempotencyCleanupJob` after the configured `StuckProcessingAgeMinutes` threshold, leaving the client free to retry. The middleware is inserted between `ExceptionHandlerMiddleware` and authentication so that the idempotency check happens on all authenticated mutations without being bypassed by the exception handler.

---

## Getting Started

### Prerequisites

- .NET 8 SDK
- PostgreSQL 15+

### Install

```bash
cd MaterialAllocationApi
dotnet restore
```

### Configure

Edit `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "Postgres": "Host=localhost;Port=5432;Database=material_allocation;Username=dotnetter;Password=<pass>",
    "PostgresMigrator": "Host=localhost;Port=5432;Database=material_allocation;Username=material_allocation_migrator;Password=<pass>"
  },
  "ReservationExpiry": {
    "IntervalSeconds": 60
  },
  "OutboxRelay": {
    "IntervalSeconds": 5,
    "BatchSize": 50
  },
  "Authentication": {
    "JwtSecret": "change-this-to-a-random-256-bit-secret-in-production",
    "Issuer": "material-allocation-api",
    "Audience": "material-allocation-clients",
    "TokenExpiryMinutes": 60
  },
  "Idempotency": {
    "ExpiryHours": 24,
    "CleanupIntervalSeconds": 3600,
    "StuckProcessingAgeMinutes": 5
  },
  "AllocationRunWorker": {
    "PollIntervalSeconds": 5
  }
}
```

Two connection strings are required: `Postgres` for the app role (DML only) and `PostgresMigrator` for the migration role (DDL). Create both roles in Postgres before running:

```sql
CREATE ROLE dotnetter LOGIN PASSWORD '<pass>';
CREATE ROLE material_allocation_migrator LOGIN PASSWORD '<pass>';
GRANT ALL ON DATABASE material_allocation TO material_allocation_migrator;
ALTER DEFAULT PRIVILEGES FOR ROLE material_allocation_migrator IN SCHEMA public
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO dotnetter;
```

### Run

```bash
# Migrations and SKU seed run automatically on startup — no separate step needed.
dotnet run
```

Swagger UI: `http://localhost:<port>/swagger` (Development only).

---

## API Reference

All endpoints are prefixed `/api/v1`. Responses follow the standard envelope:

```json
{ "success": true, "statusCode": 200, "data": {}, "error": null }
```

Error response:

```json
{
  "success": false,
  "statusCode": 409,
  "data": null,
  "error": { "message": "Order is already cancelled.", "code": "ORDER_ALREADY_CANCELLED" }
}
```

### SKUs

| Method | Path | Description |
|---|---|---|
| POST | `/skus` | Create a SKU with initial on-hand quantity |
| GET | `/skus/{id}` | Get a SKU by ID |
| GET | `/skus` | List SKUs (paginated, ordered by `sku_code`) |
| POST | `/skus/{id}/adjust` | Adjust on-hand with a signed delta and reason |
| GET | `/skus/{id}/availability` | Current availability for a SKU |

**POST `/skus` body:**

| Field | Type | Required | Description |
|---|---|---|---|
| `skuCode` | string | yes | Unique product code (max 64) |
| `description` | string | yes | Human-readable description (max 500) |
| `initialOnHand` | int | no | Starting quantity (default 0; must be ≥ 0) |

**POST `/skus/{id}/adjust` body:**

| Field | Type | Required | Description |
|---|---|---|---|
| `delta` | int | yes | Signed stock change — positive to add, negative to remove |
| `reason` | string | yes | Audit reason (max 500) |

Returns 409 if a concurrent modification races this request (`version` mismatch). Returns 422 if the adjustment would drive `on_hand` negative.

**GET `/skus/{id}/availability` response:**

| Field | Type | Description |
|---|---|---|
| `skuId` | Guid | SKU ID |
| `skuCode` | string | SKU code |
| `onHand` | int | Current on-hand quantity (excludes committed allocations) |
| `reserved` | int | Sum of active (non-expired) reservation quantities for this SKU |
| `available` | int | `on_hand - reserved` |

**Status codes:**

| Code | Meaning |
|---|---|
| 201 | SKU created |
| 200 | OK |
| 404 | SKU not found |
| 409 | Concurrent modification on adjust — re-read and retry |
| 422 | Validation error (missing fields, negative quantity, duplicate `skuCode`, delta drives `on_hand` negative) |

---

### Customers

| Method | Path | Role | Description |
|---|---|---|---|
| POST | `/customers` | `sales-ops` | Register a new customer account |
| GET | `/customers/{id}` | any-auth | Get a customer by ID |
| GET | `/customers` | any-auth | List customers (paginated, ordered by customer code) |

**POST `/customers` body:**

| Field | Type | Required | Description |
|---|---|---|---|
| `customerCode` | string | yes | Unique customer code (max 64) |
| `name` | string | yes | Customer display name (max 256) |
| `tier` | string | yes | `tier1` (contracted), `tier2` (partial contract), `tier3` (spot/priority-only) |

**`CustomerResponse` fields:**

| Field | Type | Description |
|---|---|---|
| `id` | Guid | Customer ID |
| `customerCode` | string | Unique customer code |
| `name` | string | Customer name |
| `tier` | string | `tier-1`, `tier-2`, or `tier-3` (hyphen-separated DB format) |
| `createdAt` | DateTime | UTC creation timestamp |

**Status codes:**

| Code | Meaning |
|---|---|
| 201 | Customer created |
| 200 | OK |
| 404 | Customer not found |
| 422 | Validation error or duplicate `customerCode` |

---

### Orders

| Method | Path | Description |
|---|---|---|
| POST | `/orders` | Create an order with one or more lines |
| GET | `/orders/{id}` | Get a full order including all lines |
| GET | `/orders` | List orders (paginated; optional `status` filter) |
| POST | `/orders/{id}/cancel` | Cancel an order and release any allocations |
| POST | `/orders/{id}/allocate` | Allocate available stock to open order lines |
| POST | `/orders/{id}/reserve` | Reserve available stock for each open line for a TTL |
| GET | `/orders/{id}/events` | Full chronological allocation event history for an order |

**POST `/orders` body:**

| Field | Type | Required | Description |
|---|---|---|---|
| `referenceCode` | string | yes | Caller-assigned natural key (max 64; must be unique) |
| `priority` | string | yes | `standard`, `high`, or `critical` |
| `lines` | array | yes | One or more line objects (see below); must have at least 1 |
| `customerId` | Guid? | no | ID of an existing customer; returns 422 if the ID is unknown |

Line object:

| Field | Type | Required | Description |
|---|---|---|---|
| `skuId` | Guid | yes | ID of an existing SKU |
| `requestedQty` | int | yes | Units requested (must be > 0) |

Returns 422 if any `skuId` is unknown, duplicate SKUs appear in the same order, or `requestedQty ≤ 0`.

**GET `/orders` query params:**

| Param | Default | Description |
|---|---|---|
| `status` | — | Filter: `open`, `partially_allocated`, `fully_allocated`, `cancelled` |
| `customerId` | — | Filter by customer ID; returns only orders linked to that customer |
| `page` | 1 | Page number (1-based) |
| `pageSize` | 20 | Items per page (max 100) |

**POST `/orders/{id}/cancel` response:**

Returns the updated order. Returns 409 if the order is already cancelled.

**POST `/orders/{id}/allocate` response:**

Fills open lines from current `on_hand`. Respects active reservations held by other orders — their reserved quantities are excluded from available stock. An order's own reservations do not block its own allocation.

Returns the allocation result:

| Field | Type | Description |
|---|---|---|
| `orderId` | Guid | Order ID |
| `status` | string | Order status after this run |
| `isFullyAllocated` | bool | Whether all lines are now satisfied |
| `lines` | array | Per-line result (see below) |

Per-line result:

| Field | Type | Description |
|---|---|---|
| `skuId` | Guid | SKU ID |
| `skuCode` | string | SKU code |
| `requestedQty` | int | Original request |
| `allocatedQty` | int | Total allocated to date (cumulative, not just this run) |
| `remainingQty` | int | `requestedQty - allocatedQty` after this run |

Returns 409 if the order is cancelled or already fully allocated.

**POST `/orders/{id}/reserve` body:**

| Field | Type | Required | Description |
|---|---|---|---|
| `ttlMinutes` | int | yes | Reservation lifetime in minutes (1–10,080) |

Reserves up to `requested_qty - allocated_qty` units per open line against current available stock. Calling reserve again replaces existing reservations for this order (TTL refresh — the count is not doubled). Returns 409 if the order is cancelled or fully allocated.

Reserve response:

| Field | Type | Description |
|---|---|---|
| `orderId` | Guid | Order ID |
| `referenceCode` | string | Order reference code |
| `lines` | array | Lines for which a reservation was created |
| `expiresAt` | DateTime | Shared expiry time for all lines in this reserve call |

Per-line result includes `orderLineId`, `skuId`, `skuCode`, `quantityReserved`, and `expiresAt`.

**GET `/orders/{id}/events` response:**

Returns a list of `AllocationEventResponse` objects in chronological order (`occurred_at` ascending):

| Field | Type | Description |
|---|---|---|
| `id` | Guid | Event ID |
| `eventType` | string | `allocation_committed`, `allocation_released`, `reservation_created`, `reservation_released`, or `reservation_expired` |
| `orderLineId` | Guid | The order line the event applies to |
| `skuId` | Guid | The SKU involved |
| `quantity` | int | Units affected by this event |
| `occurredAt` | DateTime | UTC timestamp of the event |

Returns 200 with an empty list for a newly created order that has no events yet. Returns 404 if the order does not exist.

**Order status codes:**

| Code | Meaning |
|---|---|
| 201 | Order created |
| 200 | OK |
| 404 | Order not found |
| 409 | Conflict — already cancelled, already fully allocated, or concurrent conflict |
| 422 | Validation error — unknown SKUs, duplicate lines, invalid priority |

---

### Reservations

| Method | Path | Description |
|---|---|---|
| POST | `/reservations/{id}/release` | Explicitly release a reservation before it expires |

Returns 204 on success, 404 if the reservation does not exist.

---

### Allocations

| Method | Path | Role | Description |
|---|---|---|---|
| POST | `/allocations/run` | `allocation-manager` | Enqueue a priority-aware allocation run |
| GET | `/allocations/runs/{id}` | any-auth | Poll status and results of a run by ID |
| GET | `/allocations/runs` | any-auth | List the 20 most recent runs, newest first |

**POST `/allocations/run`**

No request body required. Returns `202 Accepted` immediately with a `runId`. A background worker (`AllocationRunWorker`) picks up the run within its configured poll interval (`AllocationRunWorker:PollIntervalSeconds`, default 5 s), claims it with `SELECT … FOR UPDATE SKIP LOCKED` (safe for multi-replica deployments), executes the priority-ordered allocation loop, and persists the outcome.

Returns `409 RUN_IN_PROGRESS` if a run is already `pending` or `running`; the response body contains the in-progress run ID.

202 response:

| Field | Type | Description |
|---|---|---|
| `runId` | Guid | ID to poll via `GET /allocations/runs/{runId}` |

**GET `/allocations/runs/{id}`**

Poll until `status` is `completed` or `failed`. `startedAt` and `completedAt` are populated as the run progresses.

Response:

| Field | Type | Description |
|---|---|---|
| `runId` | Guid | Run ID |
| `status` | string | `pending` \| `running` \| `completed` \| `failed` |
| `requestedAt` | DateTime | UTC — when the run was enqueued |
| `startedAt` | DateTime? | UTC — when the worker claimed the run; null while `pending` |
| `completedAt` | DateTime? | UTC — when the run finished; null until terminal |
| `requestedBy` | string? | Identity of the caller who submitted the run |
| `error` | string? | Error message when `status = 'failed'`; null otherwise |
| `ordersProcessed` | int? | Total orders evaluated; null until `completed` |
| `ordersFullyAllocated` | int? | Orders reaching `fully_allocated`; null until `completed` |
| `ordersPartiallyAllocated` | int? | Orders that received partial stock; null until `completed` |
| `results` | array? | Per-order result list; null until `completed` (see below) |

Per-order result:

| Field | Type | Description |
|---|---|---|
| `orderId` | Guid | Order ID |
| `referenceCode` | string | Order reference code |
| `priority` | string | `standard`, `high`, or `critical` |
| `status` | string | Order status after this run |
| `isFullyAllocated` | bool | Whether all lines are now satisfied |

**GET `/allocations/runs`**

Returns the 20 most recent runs, newest first (`requestedAt DESC`).

Per-item:

| Field | Type | Description |
|---|---|---|
| `runId` | Guid | Run ID |
| `status` | string | `pending` \| `running` \| `completed` \| `failed` |
| `requestedAt` | DateTime | UTC |
| `completedAt` | DateTime? | UTC; null while in-progress |
| `ordersProcessed` | int? | null until `completed` |

**Status codes:**

| Code | Meaning |
|---|---|
| 202 | Run accepted — poll `GET /allocations/runs/{runId}` for status |
| 200 | OK (GET endpoints) |
| 404 | No run with the given ID |
| 409 | `RUN_IN_PROGRESS` — a run is already pending or running; response body contains the in-progress run ID |

---

### Rollup

| Method | Path | Description |
|---|---|---|
| GET | `/rollup/sku-shortages` | Paged list of SKUs where open demand exceeds available stock |

**GET `/rollup/sku-shortages` query params:**

| Param | Default | Description |
|---|---|---|
| `page` | 1 | Page number (1-based) |
| `pageSize` | 25 | Items per page (max 100) |

Response items:

| Field | Type | Description |
|---|---|---|
| `id` | Guid | SKU ID |
| `skuCode` | string | SKU code |
| `description` | string | SKU description |
| `onHand` | int | Current on-hand quantity |
| `reserved` | int | Sum of active (non-expired) reservation quantities for this SKU |
| `available` | int | `onHand - reserved` |
| `openDemand` | int | Sum of unfulfilled line quantities not covered by active reservations, across `open` and `partially_allocated` orders |
| `shortage` | int | `openDemand - available`; always > 0 for rows returned by this endpoint |

Results are ordered by `shortage` descending (worst shortages first), then `skuCode`. Returns an empty list when no SKU is short.

---

### Auth

| Method | Path | Description |
|---|---|---|
| POST | `/api/v1/auth/token` | Issue a signed JWT for the given role (development use only) |

**POST `/api/v1/auth/token`** is `[AllowAnonymous]` — no `Authorization` header is required.

**Request body:**

| Field | Type | Required | Description |
|---|---|---|---|
| `role` | string | yes | One of `warehouse-ops`, `sales-ops`, `allocation-manager`, `read-only` |

**Response:**

| Field | Type | Description |
|---|---|---|
| `token` | string | Signed JWT — pass as `Authorization: Bearer <token>` on subsequent requests |

**Status codes:**

| Code | Meaning |
|---|---|
| 200 | Token issued |
| 422 | Unknown role |

The token is signed with HMAC-SHA256 using the `Authentication:JwtSecret` from configuration. Swagger UI includes a "Authorize" button — paste the token there to authenticate all subsequent requests.

In production, replace this endpoint with tokens issued by your identity provider. The role claim in the JWT is what the `[Authorize(Roles = "...")]` attributes check.

---

### Idempotency

Idempotency is opt-in via the `Idempotency-Key` request header. It applies to all `POST` endpoints except `/api/v1/auth/token`.

**Header:**

| Header | Required | Description |
|---|---|---|
| `Idempotency-Key` | no | Client-generated unique key (max 128 chars; UUID v4 recommended) |

**Behaviour:**

| Scenario | Status | Body |
|---|---|---|
| First request with this key | — | Normal response |
| Same key, same path, request still in flight | 409 | `IDEMPOTENCY_IN_FLIGHT` |
| Same key, same path, already completed | `<original status>` | Original response body; `X-Idempotency-Replayed: true` header added |
| Same key, different path | 422 | `IDEMPOTENCY_KEY_MISMATCH` |
| Key present but empty or > 128 chars | 422 | `VALIDATION_ERROR` |
| No header | — | Request passes through; no idempotency applied |

Only `2xx` and `4xx` responses are stored. `5xx` responses are **not** stored — the `processing` record is left in place and cleaned up by `IdempotencyCleanupJob` after `StuckProcessingAgeMinutes` (default 5 min), allowing the client to retry safely.

**Stored records expire after `Idempotency:ExpiryHours` (default 24 h).** `IdempotencyCleanupJob` deletes expired `complete` records and stuck `processing` records on each `CleanupIntervalSeconds` tick (default 3600 s / 1 h).

---

## Data Models

### Customer

```
id            Guid    PK
customerCode  string  required, unique (max 64) — unique index idx_customers_code
name          string  required (max 256)
tier          string  tier-1 | tier-2 | tier-3
createdAt     DateTimeOffset
```

### Sku

```
id           Guid    PK
skuCode      string  required, unique (max 64)
description  string  required (max 500)
onHand       int     non-negative (CHECK on_hand >= 0)
version      int     EF Core concurrency token (incremented on each update)
updatedAt    DateTimeOffset
```

### InventoryAdjustment

```
id         Guid    PK
skuId      Guid    FK → Sku (restrict)
delta      int     signed change applied to on_hand
reason     string  required (max 500)
createdAt  DateTimeOffset
```

### Order

```
id             Guid    PK
referenceCode  string  required, unique (max 64)
priority       string  standard | high | critical
status         string  open | partially_allocated | fully_allocated | cancelled
createdAt      DateTimeOffset
customerId     Guid?   FK → Customer (restrict) — nullable; null for orders created before Phase 16
```

Index: `idx_orders_customer_id` on `customer_id` for customer filter queries.

### OrderLine

```
id            Guid    PK
orderId       Guid    FK → Order (cascade delete)
skuId         Guid    FK → Sku (restrict)
requestedQty  int     > 0 (CHECK)
allocatedQty  int     >= 0 and <= requestedQty (CHECK)
UNIQUE (orderId, skuId)
```

Indexes: `(orderId, skuId)` unique, `orderId`

### Reservation

```
id            Guid    PK
orderLineId   Guid    FK → OrderLine (cascade delete)
quantity      int     > 0 (CHECK)
expiresAt     DateTimeOffset
createdAt     DateTimeOffset
```

Indexes: `expiresAt` (for expiry job), `orderLineId` (for reserve/release lookups)

### AllocationEvent

```
id            Guid    PK
eventType     string  allocation_committed | allocation_released | reservation_created | reservation_released | reservation_expired (max 64)
orderId       Guid    FK → Order (restrict)
orderLineId   Guid    FK → OrderLine (restrict)
skuId         Guid    FK → Sku (restrict)
quantity      int     units affected
occurredAt    DateTimeOffset
```

All three FKs use `RESTRICT` — rows cannot be deleted if they have event history. The table is append-only; there is no update or delete path in the application.

Indexes: `orderId` (primary query: all events for one order), `skuId` (cross-order audit queries by SKU), `occurredAt` (chronological range queries)

### OutboxMessage

```
id           Guid              PK
eventType    string            e.g. order.allocated, order.cancelled, reservation.created, reservation.released, reservation.expired
payload      jsonb             event-specific JSON (camelCase); e.g. { orderId, status, isFullyAllocated } for order.allocated
createdAt    DateTimeOffset    written inside the originating write transaction
processedAt  DateTimeOffset?   set by OutboxRelayJob on successful delivery; null while pending or after failure
error        string?           last relay error message; set on publisher failure; processedAt stays null (retry-eligible)
```

`OutboxRelayJob` polls `WHERE processed_at IS NULL ORDER BY created_at` in batches of `OutboxRelaySettings.BatchSize` (default 50). The relay writes both `processed_at` and `error` updates in a single `SaveChangesAsync` call after processing the batch.

### IdempotencyRecord

```
id              Guid            PK (gen_random_uuid())
idempotencyKey  string          required, unique (max 128) — unique index used as atomic lock
requestPath     string          path the key was first used on (max 500)
requestMethod   string          HTTP method (max 10)
status          string          processing | complete
responseStatus  int?            HTTP status code of the stored response (null while processing)
responseBody    jsonb?          full response JSON body (null while processing)
createdAt       DateTimeOffset  inserted when the key is first claimed
expiresAt       DateTimeOffset  createdAt + IdempotencySettings.ExpiryHours (default 24 h)
```

Indexes: `idempotencyKey` unique (primary concurrency guard), `expiresAt` (cleanup job range scan)

### AllocationRun

```
id                      Guid     PK
status                  string   pending | running | completed | failed
requestedAt             DateTime UTC — when the run was enqueued
startedAt               DateTime? UTC — when the worker claimed the run; null while pending
completedAt             DateTime? UTC — when the run finished; null until terminal
requestedBy             string?  identity of the caller who submitted the run
error                   string?  error message when status = 'failed'; null otherwise
ordersProcessed         int?     total orders evaluated; null until completed
ordersFullyAllocated    int?     orders reaching fully_allocated; null until completed
ordersPartiallyAllocated int?   orders that received partial stock; null until completed
results                 jsonb?   serialised AllocationRunResult[] per-order outcome list; null until completed
```

`AllocationRunWorker` uses `SELECT … FOR UPDATE SKIP LOCKED` to claim `pending` rows atomically — safe for multi-replica deployments. `AllocationRunHealthCheck` degrades when any row has been in `running` state for more than 15 minutes.

Indexes: `status` (worker poll query), `requestedAt DESC` (list endpoint)

---

## Pagination

All list endpoints accept:

| Param | Default | Description |
|---|---|---|
| `page` | 1 | Page number (1-based) |
| `pageSize` | 20 | Items per page (max 100) |

Response shape:

```json
{
  "items": [],
  "page": 1,
  "pageSize": 20,
  "totalCount": 42,
  "totalPages": 3
}
```

---

## SKU Seed Data

On first startup, 5 representative memory and NAND SKUs are seeded automatically (idempotent — skipped if any SKU already exists):

| Code | Description | Initial On-Hand |
|---|---|---|
| MEM-DDR5-16G | DDR5 16 GB DIMM — 4800 MHz | 100 |
| MEM-DDR5-32G | DDR5 32 GB DIMM — 4800 MHz | 50 |
| MEM-DDR4-8G | DDR4 8 GB DIMM — 3200 MHz | 200 |
| NAND-512G-MLC | 512 GB MLC NAND Flash Module | 1 |
| NAND-1T-TLC | 1 TB TLC NAND Flash Module | 10 |

---

## Implemented Phases

| Phase | Feature | Status |
|---|---|---|
| 1 | PostgreSQL schema — `skus`, `inventory_adjustments` with CHECK constraints, `version` concurrency token, EF Core migrations | Done |
| 2 | SKU API — create, get, list, adjust (optimistic concurrency with 409 on version conflict), Swagger setup, standard response envelope, `ExceptionHandlerMiddleware` | Done |
| 3 | Orders and lines — create with duplicate-SKU guard, get, list with `status` filter, cancel status transition | Done |
| 4 | Allocation run — `POST /orders/{id}/allocate` with explicit transaction, `SELECT … FOR UPDATE` on SKU rows in sku_id-ascending order, partial allocation, `RecomputeStatus()` after each run | Done |
| 5 | Concurrency tests — two parallel allocates on a single-unit SKU; exactly one wins; invariant verification (on_hand + allocated = original stock) | Done |
| 6 | Cancel + release — restores `on_hand` from line `allocated_qty` atomically; uses same lock order as allocate; fast path (no allocated units) skips transaction | Done |
| 7 | Reservations — `reservations` table, `POST /orders/{id}/reserve` (TX + FOR UPDATE, TTL refresh), `POST /reservations/{id}/release`, `ReservationExpiryJob` background worker; allocate and cancel respect reservations | Done |
| 8 | Shortage rollup — `GET /rollup/sku-shortages`: paged, shortage-descending list of SKUs where open demand (net of active reservations) exceeds available stock; pure Dapper CTE; 6 integration tests | Done |
| 9 | Observability — Serilog structured logging (Console + Seq sink) with `MachineName`/`ThreadId`/`CorrelationId` enrichment; `ILogger<T>` in all services with post-commit log entries; `CorrelationIdMiddleware`; health checks (`/health`, `/health/live`, `/health/ready`) with Npgsql probe; Swagger XML doc comments and full API description | Done |
| 10 | Priority allocation run — `POST /allocations/run` processes all open orders globally in `critical` → `high` → `standard` order (FIFO within tier); `AllocationController` + `RunPriorityAllocationAsync`; sequential execution preserves Phase 4 locking invariants; 1 integration test verifying critical order receives stock before standard | Done |
| 11 | Allocation event ledger — `allocation_events` table with `AllocationEventType` enum (`AllocationCommitted`, `AllocationReleased`, `ReservationCreated`, `ReservationReleased`, `ReservationExpired`); events written inside existing write transactions by `AllocationService`, `OrderService`, and `ReservationService`; expiry background job uses a single CTE to DELETE reservations and INSERT expiry events atomically; all FKs use RESTRICT (immutable history); `GET /orders/{id}/events` endpoint returns full chronological event list | Done |
| 12 | Transactional outbox — `outbox_messages` table (`event_type`, `payload jsonb`, `processed_at`, `error`); `OutboxMessage` entity with `MarkProcessed()` / `MarkFailed(error)`; outbox row written inside the same `SaveChangesAsync` as every domain write (allocation, cancel, reserve, release, expiry); `OutboxRelayJob` `BackgroundService` polls unprocessed rows in batches, publishes via `IEventPublisher`, marks `processed_at` on success or records `error` on failure; `LoggingEventPublisher` logs event + payload (drop-in for Kafka/SNS); `OutboxLagHealthCheck` reports degraded when oldest unprocessed message > 2 min; 8 integration tests covering write, relay, failure recording, and atomicity | Done |
| 13 | JWT auth + RBAC — `POST /api/v1/auth/token` (`[AllowAnonymous]`) issues HMAC-SHA256 signed JWTs for a requested role; JWT Bearer validation in `Program.cs`; `[Authorize]` on all controllers; four role-restricted scopes: `warehouse-ops` (SKU writes), `sales-ops` (order create/cancel), `allocation-manager` (allocate, reserve, release, run), read-only GET access requires only authentication; `TestAuthHandler` in test project replaces JWT validation with `X-Test-Role` header; `AuthorizeAs()` / `AuthorizeAsAll()` helpers on `AllocationTestBase`; 19 RBAC integration tests covering 401, 403, and 2xx for all roles | Done |
| 14 | Idempotency — `idempotency_keys` table with unique index on `idempotency_key`; `IdempotencyMiddleware` claims keys via atomic DB insert; buffers 2xx/4xx responses and replays them on duplicate requests with `X-Idempotency-Replayed: true`; `409 IDEMPOTENCY_IN_FLIGHT` on concurrent in-flight duplicates; `422 IDEMPOTENCY_KEY_MISMATCH` on path reuse; 5xx outcomes not stored (client-retry safe); `IdempotencyCleanupJob` purges expired `complete` records and stuck `processing` records; `IdempotencySettings` (`ExpiryHours`, `CleanupIntervalSeconds`, `StuckProcessingAgeMinutes`); `IdempotencyHeaderOperationFilter` exposes optional header on all qualifying `POST` operations in Swagger UI | Done |
| 15 | Async allocation run — `POST /allocations/run` returns `202 Accepted` immediately with a `runId` instead of blocking; `allocation_runs` table with `pending → running → completed \| failed` state machine; `AllocationRunWorker` (`BackgroundService`) polls via `SELECT … FOR UPDATE SKIP LOCKED` (multi-replica safe), calls `RunPriorityAllocationAsync`, persists aggregated stats (`ordersProcessed`, `ordersFullyAllocated`, `ordersPartiallyAllocated`) and per-order JSON results; `GET /allocations/runs/{id}` status poll endpoint; `GET /allocations/runs` lists 20 most recent runs newest-first; `409 RUN_IN_PROGRESS` when a run is already active (response body contains in-progress run ID); `AllocationRunHealthCheck` degrades when any run has been `running` > 15 min (stuck worker detection); `IAllocationRunService` with `EnqueueResult` discriminated union (`Accepted` / `Conflict`); 9 integration tests covering 202+poll, state transitions, per-order results, 409 conflict, 202-after-complete, 404 unknown ID, list newest-first, zero-orders run, and fault injection via `WithWebHostBuilder` service override | Done |
| 16 | Customer entity and order linkage — `customers` table with unique `customer_code` and `tier` (stored as `tier-1`/`tier-2`/`tier-3`); `Customer` entity with private EF constructor; `CustomerTier` enum with `ToDbString`/`FromDbString` extensions; `ICustomerService` + `CustomerService` (EF write, Dapper reads); `CustomersController` (`POST [sales-ops]`, `GET /{id}`, `GET /`); nullable `customer_id` FK on `orders` with RESTRICT and `idx_orders_customer_id` index; `OrderService.CreateAsync` validates `customerId` if present; `OrderService.GetByIdAsync` LEFT JOINs `customers` to populate `customerCode`/`customerName`; `OrderService.ListAsync` accepts optional `customerId` filter; `CreateOrderRequest` / `OrderResponse` / `OrderSummaryResponse` updated with nullable customer fields; 11 integration tests (CRUD, duplicate 422, paginated list, order linkage, backward compat, unknown customerId 422, list filter, summary fields, allocate→cancel cycle) | Done |
