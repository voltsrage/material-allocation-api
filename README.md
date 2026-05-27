# Material Allocation API

A production-quality inventory allocation backend built with ASP.NET Core 8, PostgreSQL, EF Core 8, and Dapper. The domain models a constrained-inventory problem where competing orders must be filled from a shared pool of abstract SKUs under strict transactional guarantees — a pattern that directly mirrors semiconductor supply chain allocation (Micron-style).

## Domain Model — How It Maps to a Real Supply Chain

In a memory/storage supply chain, a fixed quantity of finished goods (DIMMs, NAND modules) is allocated to customers in priority order. Multiple orders arrive simultaneously, referencing the same SKU. The core problem is: under concurrent demand, no order should receive more units than are available, and no unit should be counted twice.

```
Sku ──────────────────────────── one SKU = one product variant (e.g. DDR5-16G, NAND-1T)
 └── InventoryAdjustment         signed audit record every time on_hand changes outside allocation
Order ────────────────────────── one order = one allocation request from one customer
 └── OrderLine                   one line per SKU requested — qty requested vs. qty allocated
      └── Reservation            optional soft hold on available units for a TTL window
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

---

## Features

- **SKU Catalog** — create SKUs with initial on-hand quantity; paginated list ordered by SKU code; get by ID
- **Inventory Adjustments** — signed delta adjustments with mandatory reason text; full audit trail in `inventory_adjustments`; optimistic concurrency on adjust with 409 on version conflict
- **Availability Query** — `GET /skus/{id}/availability` returns `on_hand`, `reserved`, and `available = on_hand - reserved` in one read
- **Order Management** — create orders with one or more lines referencing existing SKUs; paginated list with optional `status` filter; unique `reference_code` enforced at DB level
- **Allocation Run** — `POST /orders/{id}/allocate` fills open lines against current stock in a single transaction; pessimistic `SELECT … FOR UPDATE` on SKU rows in deterministic (sku_id ascending) order prevents deadlocks; partial allocation allowed — unfulfilled lines remain open; response states partial vs. full explicitly
- **Priority Allocation Run** — `POST /allocations/run` processes every non-terminal order in a single call, in priority order (`critical` → `high` → `standard`); within a tier, older orders are served first (FIFO); each order allocates in its own transaction so all Phase 4 locking invariants hold; returns aggregated stats (`ordersProcessed`, `ordersFullyAllocated`, `ordersPartiallyAllocated`) plus a per-order result list
- **Cancellation & Release** — `POST /orders/{id}/cancel` transitions the order to `cancelled` and atomically restores `on_hand` from each line's `allocated_qty`; cancel uses the same lock order as allocate to prevent concurrent allocation/cancel deadlocks
- **Reservations** — `POST /orders/{id}/reserve` places a TTL-bounded hold per line against available stock; own-order reservation does not block own allocation; calling reserve again replaces the existing hold (idempotent TTL refresh)
- **Reservation Release** — `POST /reservations/{id}/release` explicitly removes a reservation before it expires
- **Reservation Expiry Job** — `ReservationExpiryJob` runs on a configurable interval (default 60 s) and deletes all rows where `expires_at <= NOW()`, restoring their quantity to availability automatically
- **Shortage Rollup** — `GET /rollup/sku-shortages` returns a paged, shortage-descending list of SKUs where open unfulfilled demand exceeds available stock; open demand accounts for active reservations (a line covered by a reservation does not count as unmet demand); pure Dapper read with a single CTE shared between the COUNT and the paged SELECT
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
  → CorrelationIdMiddleware   (assigns / echoes X-Correlation-ID; pushes CorrelationId into Serilog LogContext)
  → ExceptionHandlerMiddleware (maps domain exceptions to standard envelope; logs warnings/errors with request path)
  → Serilog request logging   (one structured log line per request: method, path, status, elapsed, correlation ID)
  → Controllers
  → Services                  (all writes log outcome via ILogger<T> after commit)
  → EF Core / Dapper
  → PostgreSQL
```

Services are the only layer that touches the database. Controllers translate HTTP concerns (query params, status codes, response envelope) and delegate all business logic to the service layer. EF Core handles writes, aggregate loads, and raw-SQL locking queries; Dapper is used for multi-result read queries (order details with lines, paginated lists) where the result shape doesn't map cleanly to aggregate roots.

## Tech Stack

| Layer | Technology |
|---|---|
| Server | ASP.NET Core 8 (.NET 8.0) |
| Database | PostgreSQL 15+ with EF Core 8 (code-first migrations) |
| Micro-ORM | Dapper 2.1 |
| Logging | Serilog — structured logs to Console + Seq; enriched with `MachineName`, `ThreadId`, `CorrelationId` |
| Background worker | `BackgroundService` (`ReservationExpiryJob`) |
| Docs | Swagger / OpenAPI (Swashbuckle) with XML doc comments |
| Testing | xUnit + `WebApplicationFactory<Program>` — real Postgres database |

---

## Project Structure

```
MaterialAllocationApi/
├── Program.cs                              # Service registration, middleware, migration on startup, SKU seed
├── appsettings.json                        # Connection strings (app + migrator roles), Serilog, ReservationExpiry interval
├── Controllers/
│   ├── SkusController.cs                   # Create, get, list, adjust, availability
│   ├── OrdersController.cs                 # Create, get, list, cancel, allocate, reserve
│   ├── AllocationController.cs             # POST /allocations/run — global priority run
│   ├── ReservationsController.cs           # Release
│   └── RollupController.cs                 # GET sku-shortages
├── Domain/Entities/
│   ├── Sku.cs                              # AllocateUnits(), ReleaseUnits(); version concurrency token
│   ├── InventoryAdjustment.cs
│   ├── Order.cs                            # Cancel(), RecomputeStatus() from line quantities
│   ├── OrderLine.cs                        # Allocate(), ReleasedAllocation()
│   └── Reservation.cs
├── Domain/Enums/
│   ├── OrderPriority.cs
│   └── OrderStatus.cs
├── Models/Records/
│   ├── SkuRecords.cs                       # CreateSkuRequest / AdjustSkuRequest / SkuResponse
│   ├── OrderRecords.cs                     # CreateOrderRequest / OrderResponse / OrderSummaryResponse
│   ├── AllocationRecords.cs                # AllocationResponse / AllocationLineResult / AvailabilityResponse / AllocationRunResponse / AllocationRunResult
│   ├── ReservationRecords.cs               # ReserveRequest / ReservationResponse / ReservationLineResult
│   └── RollupRecords.cs                    # SkuShortageResponse
├── Services/
│   ├── Interfaces/
│   │   ├── ISkuService.cs
│   │   ├── IOrderService.cs
│   │   ├── IAllocationService.cs
│   │   ├── IReservationService.cs
│   │   └── IRollupService.cs
│   ├── SkuService.cs                       # Create, GetById, List, AdjustAsync (optimistic concurrency)
│   ├── OrderService.cs                     # Create, GetById, List, CancelAsync (TX + FOR UPDATE)
│   ├── AllocationService.cs                # AllocateAsync (TX + FOR UPDATE), GetAvailabilityAsync, RunPriorityAllocationAsync
│   ├── ReservationService.cs               # ReserveAsync (TX + FOR UPDATE), ReleaseAsync, ExpireAsync
│   ├── ReservationExpiryJob.cs             # BackgroundService — DELETE WHERE expires_at <= NOW()
│   └── RollupService.cs                    # GetSkuShortageAsync — Dapper CTE, no writes
├── Data/
│   ├── AllocationDbContext.cs              # EF Core context — entity configs, indexes, CHECK constraints
│   ├── IDbConnectionFactory.cs / NpgsqlConnectionFactory.cs
│   ├── TransactionHelper.cs                # RollbackAsync — safe rollback that swallows secondary exceptions
│   └── Seed/SkuSeeder.cs                   # Seeds 5 memory/NAND SKUs on first startup (idempotent)
├── Common/
│   ├── ApiResponse.cs                      # Generic response envelope + ApiError
│   ├── PagedResult.cs
│   └── Exceptions/
│       ├── NotFoundException.cs
│       ├── ConflictException.cs
│       └── ValidationException.cs
├── Middleware/
│   ├── CorrelationIdMiddleware.cs          # Assigns/echoes X-Correlation-ID; pushes into Serilog LogContext
│   └── ExceptionHandlerMiddleware.cs       # Maps domain exceptions to standard envelope + status codes; logs with request path
└── Migrations/                             # EF Core migration history

MaterialAllocationApi.Tests/
├── Fixtures/
│   └── ApiFixture.cs                       # WebApplicationFactory<Program>; MigrateAsync on init; ResetDatabaseAsync between tests
├── Helpers/
│   └── AllocationTestBase.cs               # HTTP helpers (CreateSkuAsync, CreateOrderAsync, AllocateAsync, …); DB assertion helpers
├── Allocation/
│   ├── AllocationFlowTests.cs              # Partial allocation, sequential top-up, over-demand, priority run (critical before standard)
│   ├── ConcurrentAllocationTests.cs        # Two parallel allocates on the same SKU — exactly one wins
│   ├── CancelTests.cs                      # Cancel + inventory restoration; cancel with no allocations
│   └── ReservationTests.cs                 # Reserve, block, own-order exception, TTL refresh, release, expiry, cancel-deletes
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

### Orders

| Method | Path | Description |
|---|---|---|
| POST | `/orders` | Create an order with one or more lines |
| GET | `/orders/{id}` | Get a full order including all lines |
| GET | `/orders` | List orders (paginated; optional `status` filter) |
| POST | `/orders/{id}/cancel` | Cancel an order and release any allocations |
| POST | `/orders/{id}/allocate` | Allocate available stock to open order lines |
| POST | `/orders/{id}/reserve` | Reserve available stock for each open line for a TTL |

**POST `/orders` body:**

| Field | Type | Required | Description |
|---|---|---|---|
| `referenceCode` | string | yes | Caller-assigned natural key (max 64; must be unique) |
| `priority` | string | yes | `standard`, `high`, or `critical` |
| `lines` | array | yes | One or more line objects (see below); must have at least 1 |

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

| Method | Path | Description |
|---|---|---|
| POST | `/allocations/run` | Run priority-aware allocation across all open orders |

**POST `/allocations/run`**

No request body required. Processes all orders with status `open` or `partially_allocated` in priority order (`critical` → `high` → `standard`); within a tier, older orders are served first. Each order runs through its own transaction using the same `SELECT … FOR UPDATE` locking as `POST /orders/{id}/allocate`. Orders that become terminal (cancelled or fully allocated by a concurrent request) between the initial query and the lock are skipped gracefully.

Response:

| Field | Type | Description |
|---|---|---|
| `ordersProcessed` | int | Total orders evaluated in this run |
| `ordersFullyAllocated` | int | Orders whose status is `fully_allocated` after this run |
| `ordersPartiallyAllocated` | int | Orders that received some stock but still have open lines |
| `results` | array | Per-order result (see below) |

Per-order result:

| Field | Type | Description |
|---|---|---|
| `orderId` | Guid | Order ID |
| `referenceCode` | string | Order reference code |
| `priority` | string | `standard`, `high`, or `critical` |
| `status` | string | Order status after this run |
| `isFullyAllocated` | bool | Whether all lines are now satisfied |

**Status codes:**

| Code | Meaning |
|---|---|
| 200 | Run complete — inspect per-order `results` for individual outcomes |

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

## Data Models

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
```

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
