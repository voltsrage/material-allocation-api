# PRD: Material Allocation API

## Overview

A .NET Web API for **allocating constrained inventory** (abstract SKUs) to competing **orders** under business rules: priority tiers, **partial allocations**, **reservations** with expiry, and **visibility** into remaining supply. The domain fits **electronics / memory ecosystem** supply chains (Micron-style allocation problems) without integrating to ERP.

The core interview signal is **transactional correctness**: concurrent requests must not oversell inventory; conflicts must surface as clear outcomes.

**Stack:** .NET 8 Web API, PostgreSQL, EF Core, OpenAPI/Swagger, Serilog → Seq, xUnit, Docker Compose, optional Redis for reservation TTL hints (source of truth remains PostgreSQL).

---

## Goals

- Implement **allocate** and **release** flows with **database transactions** and explicit **concurrency** strategy.
- Practice domain rules that are easy to get wrong without tests (two competing orders, same SKU, last units).
- Demonstrate mature API error modeling (`409`, `422`) for business conflicts.

## Non-Goals

- Full ATP (available-to-promise) across multiple warehouses.
- Payment or invoicing.
- SAP / Oracle integration.

---

## API Conventions

Same response envelope and pagination as **Fleet Telemetry API** (`docs/projects/fleet-telemetry-api-prd.md`). Prefix: `/api/v1`.

---

## Domain Model

An **SKU** has a code, description, and **on_hand** quantity (simplified single bucket). The `version` concurrency token is incremented on every write and is used to detect concurrent modifications on the adjust endpoint.

An **Order** has a reference, priority (`standard`, `high`, `critical`), status (`open`, `partially_allocated`, `fully_allocated`, `cancelled`), and multiple **order lines** (sku, requested qty, allocated qty). Orders optionally link to a **Customer** via a nullable FK.

An **AllocationEvent** records each unit movement immutably inside the same transaction as the write, providing a full audit trail.

A **Reservation** holds quantity for an order line until `expires_at`; a background job expires stale rows and restores availability.

A **Customer** has a unique `customerCode`, display `name`, and `tier` (`tier-1` contracted, `tier-2` partial, `tier-3` spot). The tier drives the order of the floor pass in the allocation run.

A **CustomerContract** is a per-SKU allocation guarantee for a customer during a date range: `floorQty` (minimum units the allocation run must honor before any priority ordering) and optional `ceilingQty` (total cap across the run; null = uncapped). The allocation run enforces both constraints in a two-pass loop.

---

## Features

### 1. SKU Catalog & Inventory

**Endpoints:**
- `POST /api/v1/skus` — create SKU with initial on_hand
- `GET /api/v1/skus/{id}`
- `GET /api/v1/skus?page=&pageSize=`
- `POST /api/v1/skus/{id}/adjust` — adjust on_hand with reason (audit row)

**Concepts practiced:** CRUD, non-negative quantity constraint, audit trail.

---

### 2. Orders & Lines

**Endpoints:**
- `POST /api/v1/orders` — create order with lines
- `GET /api/v1/orders/{id}`
- `GET /api/v1/orders?status=&page=&pageSize=`
- `POST /api/v1/orders/{id}/cancel` — cancel; release allocations

**Concepts practiced:** Aggregates, cascading rules, status transitions.

---

### 3. Allocation Run (core)

**Description:** For an order, attempt to allocate requested quantities across lines **by priority of lines or SKUs** (document algorithm: e.g. fill lines in array order; **critical** orders preempt only if you implement priority queue — junior: single order allocation only; mid: global fair share or priority across orders).

**Endpoints:**
- `POST /api/v1/orders/{id}/allocate` — allocate available stock to lines
- `GET /api/v1/skus/{id}/availability` — on_hand minus reserved minus allocated (document formula)

**Concurrency:**
- Use **serializable** or **repeatable read** with explicit handling, **or** optimistic concurrency with `row_version` on SKU rows and retry policy — **document the choice**.
- At minimum: `SELECT … FOR UPDATE` on SKU rows inside a transaction for the junior scope.

**Responses:**
- `200` partial or full allocation with updated line quantities
- `409` when order cancelled or concurrent conflict after retries exhausted
- `422` when request invalid (unknown SKU on line)

**Concepts practiced:** Transactions, isolation, locking, domain invariants.

---

### 4. Reservations (mid)

**Endpoints:**
- `POST /api/v1/orders/{id}/reserve` — reserve up to N units per line for TTL
- `POST /api/v1/reservations/release` — explicit release
- Background job: expire reservations and restore availability

**Concepts practiced:** Time-based rules, background worker, no double-counting.

---

### 5. Reporting Reads

**Endpoints:**
- `GET /api/v1/rollup/sku-shortages` — SKUs where sum(open demand) > availability

**Concepts practiced:** Read-only queries; `AsNoTracking()`; optional Dapper if query grows — not required for this PRD.

---

### 6. Auth, Health, Logging

Authenticate mutating endpoints; log `order_id`, `sku_id`, `correlation_id`.

---

### 7. Priority Allocation Run (Phase 10)

**Endpoints:**
- `POST /api/v1/allocations/run` — process all open orders globally in priority order

Processes all open orders in `critical` → `high` → `standard` order (FIFO within tier). Sequential execution preserves Phase 4 locking invariants. Returns the allocation results for all orders touched in this run.

**Concepts practiced:** Global priority ordering, multi-order loop, reuse of single-order allocation inside a loop.

---

### 8. Allocation Event Ledger (Phase 11)

**Endpoints:**
- `GET /api/v1/orders/{id}/events` — full chronological event history for an order

Immutable `allocation_events` table; all FKs use RESTRICT. One event per unit movement inside the originating transaction.

**Concepts practiced:** Append-only audit tables, CTE for atomic DELETE + INSERT (expiry events).

---

### 9. Transactional Outbox (Phase 12)

Every domain write appends an `outbox_messages` row inside the same `SaveChangesAsync` call. `OutboxRelayJob` polls and delivers via `IEventPublisher` (currently `LoggingEventPublisher`). `OutboxLagHealthCheck` surfaces lag.

**Concepts practiced:** Dual-write atomicity, at-least-once delivery, swap-in transport (Kafka/SNS).

---

### 10. JWT Auth + RBAC (Phase 13)

`POST /api/v1/auth/token` issues HMAC-SHA256 JWTs (development use only). Four roles: `warehouse-ops`, `sales-ops`, `allocation-manager`, `read-only`. All controllers carry `[Authorize]`; action-level `[Authorize(Roles = "...")]` enforces fine-grained access.

**Concepts practiced:** JWT Bearer pipeline, role-based access control, test auth handler replacement.

---

### 11. Idempotency (Phase 14)

Optional `Idempotency-Key` header on all `POST` endpoints (except `/api/v1/auth/token`). DB unique index on `idempotency_key` acts as the atomic lock — concurrent duplicates receive `409 IDEMPOTENCY_IN_FLIGHT`. Stored 2xx/4xx responses are replayed with `X-Idempotency-Replayed: true`. 5xx outcomes are not stored (client-retry safe).

**Concepts practiced:** Idempotency via database unique constraint, response buffering, replay semantics.

---

### 12. Async Allocation Run (Phase 15)

`POST /allocations/run` returns `202 Accepted` immediately with a `runId`. `AllocationRunWorker` claims runs via `SELECT … FOR UPDATE SKIP LOCKED` (multi-replica safe). `GET /allocations/runs/{id}` polls status; `GET /allocations/runs` lists the 20 most recent. `409 RUN_IN_PROGRESS` when a run is already active. `AllocationRunHealthCheck` degrades when any run has been `running` > 15 min.

**Concepts practiced:** Async job queue, SKIP LOCKED worker pattern, health check probes for background jobs.

---

### 13. Customer Entity + Order Linkage (Phase 16)

**Endpoints:**
- `POST /api/v1/customers` — register a customer (`sales-ops`)
- `GET /api/v1/customers/{id}`, `GET /api/v1/customers` — any-auth
- `POST /api/v1/orders` — accepts optional `customerId` FK

`Customer` entity with `customerCode` (unique), `name`, and `tier`. Nullable `customer_id` FK on `orders` validated at write time. `GET /orders/{id}` LEFT JOINs customers to populate `customerCode`/`customerName`. `GET /orders` accepts optional `customerId` filter.

**Concepts practiced:** Optional FK linkage, nullable foreign keys, query extension for filters.

---

### 14. Customer Contracts + Contract-Aware Allocation (Phase 17)

**Endpoints:**
- `POST /api/v1/customers/{id}/contracts` — create a per-SKU contract (`sales-ops`)
- `GET /api/v1/customers/{id}/contracts` — list all contracts for a customer (any-auth)
- `GET /api/v1/customers/{id}/contracts/utilization` — live floor/ceiling vs allocated snapshot (any-auth)

`CustomerContract` stores `floorQty`, optional `ceilingQty` (null = uncapped), and an effective date range. The overlap guard in `ContractService.CreateAsync` rejects any new contract that shares a `(customerId, skuId)` interval with an existing one.

`RunPriorityAllocationAsync` is rewritten as a two-pass loop:

1. **Floor pass** — iterates active contracts sorted Tier1 → Tier2 → Tier3, `floorQty` descending within tier. Allocates up to `floorQty` units from the customer's open orders before any priority ordering; records `ceilingSpent[(customerId, skuId)]`.
2. **Priority pass** — processes all remaining open orders `critical` → `high` → `standard` then `createdAt`. Contracted customers are capped at `ceilingQty − ceilingSpent` per SKU; uncontracted orders are fully uncapped.

All passes remain serial to preserve Phase 4 locking invariants.

`DateOnlyTypeHandler` (Dapper `SqlMapper.TypeHandler<DateOnly>`) bridges `timestamp without time zone` columns returned by Npgsql as `DateTime` into `DateOnly` for response records.

**Concepts practiced:** Date-range overlap validation, two-pass allocation loop with stateful accumulators, Dapper type handler, nested route design.

---

## Database Schema & Indexing Plan

```sql
CREATE TABLE skus (
  id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  sku_code    VARCHAR(64) NOT NULL UNIQUE,
  description VARCHAR(500) NOT NULL,
  on_hand     INT NOT NULL CHECK (on_hand >= 0),
  version     INT NOT NULL DEFAULT 0,
  updated_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE orders (
  id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  reference_code VARCHAR(64) NOT NULL UNIQUE,
  priority       VARCHAR(32) NOT NULL,
  status         VARCHAR(32) NOT NULL,
  created_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE order_lines (
  id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  order_id       UUID NOT NULL REFERENCES orders(id) ON DELETE CASCADE,
  sku_id         UUID NOT NULL REFERENCES skus(id),
  requested_qty  INT NOT NULL CHECK (requested_qty > 0),
  allocated_qty  INT NOT NULL DEFAULT 0 CHECK (allocated_qty >= 0),
  CHECK (allocated_qty <= requested_qty),
  UNIQUE (order_id, sku_id)
);

CREATE TABLE inventory_adjustments (
  id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  sku_id      UUID NOT NULL REFERENCES skus(id),
  delta       INT NOT NULL,
  reason      VARCHAR(500) NOT NULL,
  created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Mid: reservations
CREATE TABLE reservations (
  id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  order_line_id  UUID NOT NULL REFERENCES order_lines(id) ON DELETE CASCADE,
  quantity       INT NOT NULL CHECK (quantity > 0),
  expires_at     TIMESTAMPTZ NOT NULL,
  created_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_reservations_expiry ON reservations (expires_at);

-- Phase 11: allocation event ledger (append-only; all FKs RESTRICT)
CREATE TABLE allocation_events (
  id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  event_type    VARCHAR(64) NOT NULL,
  order_id      UUID NOT NULL REFERENCES orders(id) ON DELETE RESTRICT,
  order_line_id UUID NOT NULL REFERENCES order_lines(id) ON DELETE RESTRICT,
  sku_id        UUID NOT NULL REFERENCES skus(id) ON DELETE RESTRICT,
  quantity      INT NOT NULL,
  occurred_at   TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_allocation_events_order ON allocation_events (order_id);
CREATE INDEX idx_allocation_events_sku   ON allocation_events (sku_id);
CREATE INDEX idx_allocation_events_time  ON allocation_events (occurred_at);

-- Phase 12: transactional outbox
CREATE TABLE outbox_messages (
  id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  event_type   VARCHAR(128) NOT NULL,
  payload      JSONB NOT NULL,
  created_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  processed_at TIMESTAMPTZ,
  error        TEXT
);

-- Phase 14: idempotency keys
CREATE TABLE idempotency_keys (
  id               UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  idempotency_key  VARCHAR(128) NOT NULL UNIQUE,
  request_path     VARCHAR(500) NOT NULL,
  request_method   VARCHAR(10)  NOT NULL,
  status           VARCHAR(32)  NOT NULL,
  response_status  INT,
  response_body    JSONB,
  created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  expires_at       TIMESTAMPTZ NOT NULL
);
CREATE INDEX idx_idempotency_keys_expires ON idempotency_keys (expires_at);

-- Phase 15: async allocation runs
CREATE TABLE allocation_runs (
  id                        UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  status                    VARCHAR(32) NOT NULL,
  requested_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  started_at                TIMESTAMPTZ,
  completed_at              TIMESTAMPTZ,
  requested_by              VARCHAR(256),
  error                     TEXT,
  orders_processed          INT,
  orders_fully_allocated    INT,
  orders_partially_allocated INT,
  results                   JSONB
);
CREATE INDEX idx_allocation_runs_status       ON allocation_runs (status);
CREATE INDEX idx_allocation_runs_requested_at ON allocation_runs (requested_at DESC);

-- Phase 16: customers
CREATE TABLE customers (
  id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  customer_code VARCHAR(64) NOT NULL UNIQUE,
  name          VARCHAR(256) NOT NULL,
  tier          VARCHAR(32) NOT NULL,
  created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE UNIQUE INDEX idx_customers_code ON customers (customer_code);

-- Phase 16: nullable customer FK on orders
ALTER TABLE orders ADD COLUMN customer_id UUID REFERENCES customers(id) ON DELETE RESTRICT;
CREATE INDEX idx_orders_customer_id ON orders (customer_id);

-- Phase 17: customer contracts
CREATE TABLE customer_contracts (
  id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  customer_id    UUID NOT NULL REFERENCES customers(id) ON DELETE RESTRICT,
  sku_id         UUID NOT NULL REFERENCES skus(id) ON DELETE RESTRICT,
  floor_qty      INT NOT NULL CHECK (floor_qty >= 0),
  ceiling_qty    INT,
  effective_from TIMESTAMP NOT NULL,
  effective_to   TIMESTAMP,
  created_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_customer_contracts_customer_sku ON customer_contracts (customer_id, sku_id);
```

**Concurrency note:** Map `version` as an EF Core concurrency token (`Property(e => e.Version).IsConcurrencyToken()`) and increment it on each successful SKU update; handle `DbUpdateConcurrencyException` in the allocation service.

---

## Design Decisions

### Pessimistic vs optimistic locking

Document trade-offs in README or PRD appendix: pessimistic is easier to reason about for interviews; optimistic scales better under contention.

### Single SKU row vs ledger

This PRD uses **mutable `on_hand`** for clarity. A ledger-based inventory (sum of movements) is a senior extension.

---

## Non-Functional Requirements

| Concern | Target |
|---|---|
| Safety | No negative inventory; no line over-allocation |
| Tests | Concurrent integration test: two parallel allocates cannot exceed stock |
| Clarity | Every 409/422 returns machine-readable `code` |

---

## Build Order

| Phase | Focus |
|---|---|
| 1 | SKU schema, adjustments audit, EF migrations |
| 2 | SKU API + concurrency token on `skus.version` |
| 3 | Orders and lines CRUD + validation |
| 4 | Allocate: transaction + pessimistic SKU lock (or optimistic retry) |
| 5 | Integration tests: concurrent allocations |
| 6 | Cancel order + release allocations atomically |
| 7 | Reservations + expiry background job (mid) |
| 8 | Shortage rollup read model |
| 9 | Swagger, health, Seq, structured logging |
| 10 | Priority allocation run — global loop across all open orders |
| 11 | Allocation event ledger — immutable audit trail for every unit movement |
| 12 | Transactional outbox — guaranteed event delivery with `OutboxRelayJob` |
| 13 | JWT auth + RBAC — four roles, `TestAuthHandler`, 19 RBAC tests |
| 14 | Idempotency — DB-unique-constraint lock, replay, `IdempotencyCleanupJob` |
| 15 | Async allocation run — `202 Accepted`, `AllocationRunWorker`, SKIP LOCKED |
| 16 | Customer entity + order linkage — tier, nullable FK on orders, list filter |
| 17 | Customer contracts + two-pass allocation — floor/ceiling enforcement |

---

## Step-by-Step Guide

Order matters: **allocation safety** (Phases 4–5) before **reservations** (Phase 7), because reservations stack another moving part on top of quantity math.

---

### Phase 1 — Inventory schema

**What to do:**
1. Create `skus`, `inventory_adjustments` per schema; enforce `on_hand >= 0`.
2. Seed a few SKUs with known quantities.

**Why:**
Adjustments give you an audit story without full ledger accounting — useful for interviews and debugging allocation bugs.

---

### Phase 2 — SKU API and optimistic concurrency

**What to do:**
1. Map `version` as EF Core concurrency token; increment on updates that touch inventory-related fields.
2. Implement adjust endpoint; handle `DbUpdateConcurrencyException` with a clear `409` or retry policy — document the behavior.

**Why:**
Concurrency tokens prepare you for **optimistic** allocation retries even if Phase 4 starts with `FOR UPDATE`.

---

### Phase 3 — Orders and lines

**What to do:**
1. Create orders with lines referencing SKUs; enforce unique `(order_id, sku_id)`.
2. List and get endpoints with pagination; validate **requested_qty** and status transitions on create/cancel.

**Why:**
Lines are the allocation unit. Bad line modeling makes allocation logic unreadable.

---

### Phase 4 — Allocate (single transaction)

**What to do:**
1. Implement `POST /orders/{id}/allocate`: load lines and SKUs inside a transaction.
2. Lock SKU rows in **deterministic order** (e.g. sort `sku_id`) when using pessimistic locking to reduce deadlocks.
3. Increase `allocated_qty` on lines and decrease `on_hand` on SKUs; reject over-allocation.
4. Return a response that states **partial vs full** allocation explicitly.

**Why:**
This is the heart of the project: **invariants** that must hold under concurrency. Interviewers probe overselling and isolation.

---

### Phase 5 — Concurrency tests

**What to do:**
1. Write an integration test that runs **two parallel allocate** operations against the same SKU with stock = 1; exactly one should win the unit; total `on_hand` + sum(`allocated_qty`) across DB must stay consistent with your rules.
2. Add a test for partial fulfillment then second allocate completes the line.

**Why:**
Claims about transactions mean nothing without a failing test that proved a bug existed before the fix.

---

### Phase 6 — Cancel and release

**What to do:**
1. Implement cancel: only allowed from certain statuses; restore `on_hand` from line `allocated_qty` and zero allocations.
2. Keep cancel in **one transaction** with row locks consistent with allocate.

**Why:**
Cancel is the second-most-common source of inventory bugs after concurrent allocate.

---

### Phase 7 — Reservations (mid)

**What to do:**
1. Add `reservations` table; `reserve` subtracts from **available** (define: `on_hand - reserved - allocated` or your formula — document it).
2. Background job expires reservations past `expires_at` and returns quantity to availability.
3. Ensure allocate respects reservations.

**Why:**
Reservations introduce **time** and **TTL** — typical extension question after basic allocate works.

---

### Phase 8 — Shortage rollup

**What to do:**
1. Implement `GET /rollup/sku-shortages`: SKUs where open demand exceeds availability (define “open demand” from order lines).

**Why:**
Operations-facing endpoints show you can translate business questions into SQL or LINQ without corrupting core writes.

---

### Phase 9 — Polish

**What to do:**
1. Swagger completeness; health checks; structured logging with Serilog.
2. `CorrelationIdMiddleware` — accept `X-Correlation-ID` or generate a random ID; push into Serilog `LogContext`; echo on response.
3. Health checks: `/health/live` (always healthy), `/health/ready` (Npgsql probe).

**Why:**
Allocation APIs fail in production under ambiguity — docs and logs narrow incident time.

---

### Phase 10 — Priority allocation run

**What to do:**
1. Add `POST /allocations/run` that iterates all open orders globally in `critical` → `high` → `standard` order (FIFO within tier) and calls `AllocateAsync` for each.
2. Run serially — parallel execution would re-introduce circular-wait risk from Phase 4.
3. Return an aggregate result: orders processed, fully/partially allocated counts, per-order line results.

**Why:**
The per-order allocate endpoint can only honor supply for one order at a time; a global run is needed to distribute constrained stock according to business priority rules across the full order book.

---

### Phase 11 — Allocation event ledger

**What to do:**
1. Add `allocation_events` table with `event_type`, FKs to `orders`, `order_lines`, `skus`, `quantity`, `occurred_at`.
2. All FKs use RESTRICT — no event may be silently removed when an order or line is deleted.
3. Write events inside every existing write transaction: `allocation_committed` (allocate), `allocation_released` (cancel), `reservation_created`, `reservation_released`, `reservation_expired`.
4. For reservation expiry: use a single CTE that DELETEs the reservation row and INSERTs the expiry event atomically.
5. Add `GET /orders/{id}/events`.

**Why:**
Immutable ledgers provide explainability — you can reconstruct the lifecycle of every unit without querying mutable state.

---

### Phase 12 — Transactional outbox

**What to do:**
1. Add `outbox_messages` table (`event_type`, `payload jsonb`, `processed_at`, `error`).
2. Append an `OutboxMessage` row inside the same `SaveChangesAsync` call as each domain write. The two writes are atomic.
3. Build `OutboxRelayJob` (`BackgroundService`): poll unprocessed rows in batches → deliver via `IEventPublisher` → mark `processed_at` on success or record `error` on failure (leaving the row retry-eligible).
4. Implement `LoggingEventPublisher` as the initial publisher (logs event type + payload). The interface allows swap-in for Kafka or SNS.
5. Add `OutboxLagHealthCheck`: degraded when the oldest unprocessed message is older than two minutes.

**Why:**
Without an outbox, a crash between "write committed" and "event published" causes silent data loss. The outbox trades that lost event for a guaranteed at-least-once delivery.

---

### Phase 13 — JWT auth + RBAC

**What to do:**
1. Add `POST /api/v1/auth/token` (`[AllowAnonymous]`) that issues HMAC-SHA256 signed JWTs for a requested role. In production, replace with tokens from your identity provider.
2. Wire JWT Bearer validation in `Program.cs`; add `[Authorize]` at the controller class level.
3. Add action-level `[Authorize(Roles = "...")]` for four roles: `warehouse-ops` (SKU writes), `sales-ops` (order create/cancel), `allocation-manager` (allocate, reserve, release, run), read-only (any-auth GET).
4. Return standard-envelope 401/403 from `JwtBearerEvents.OnChallenge` / `OnForbidden` instead of default plain-text.
5. In the test project, implement `TestAuthHandler` (reads `X-Test-Role` header; returns `NoResult()` when absent → 401). Add `AuthorizeAs(role)` / `AuthorizeAsAll()` helpers to `AllocationTestBase`.

**Why:**
Test auth handlers allow integration tests to exercise the auth middleware without real JWTs, keeping tests deterministic and fast.

---

### Phase 14 — Idempotency

**What to do:**
1. Add `idempotency_keys` table with a unique index on `idempotency_key`; use the constraint as the atomic lock rather than application-level compare-and-set.
2. Implement `IdempotencyMiddleware` (between `ExceptionHandlerMiddleware` and authentication): on first request, INSERT a `processing` record; on concurrent duplicate, catch unique-violation → `409 IDEMPOTENCY_IN_FLIGHT`; buffer the response body with `MemoryStream`; store 2xx/4xx outcomes (not 5xx) in the `complete` record.
3. On replay: return the stored response body with `X-Idempotency-Replayed: true`.
4. Add `IdempotencyCleanupJob` to purge expired `complete` records and stuck `processing` records.
5. Add `IdempotencyHeaderOperationFilter` to expose the optional header on all qualifying `POST` operations in Swagger UI.

**Why:**
The database unique constraint is safe under any level of horizontal scaling without a distributed lock. Using it as the lock also avoids a two-step check-then-insert race condition.

---

### Phase 15 — Async allocation run

**What to do:**
1. Change `POST /allocations/run` to return `202 Accepted` with a `runId` immediately rather than blocking.
2. Add `allocation_runs` table with `pending → running → completed | failed` state machine.
3. Implement `AllocationRunWorker` (`BackgroundService`): poll `pending` rows via `SELECT … FOR UPDATE SKIP LOCKED` (safe for multi-replica deployments), call `RunPriorityAllocationAsync`, persist aggregated stats and per-order JSON results, mark `completed` or `failed`.
4. Add `GET /allocations/runs/{id}` (poll) and `GET /allocations/runs` (list 20 newest).
5. Return `409 RUN_IN_PROGRESS` when a run is already `pending` or `running`; include the in-progress run ID in the response body so callers can poll it instead of retrying blindly.
6. Add `AllocationRunHealthCheck`: degraded when any run has been in `running` state for more than 15 minutes (stuck worker detection).

**Why:**
A blocking allocation run on a large order book can take seconds. Async with SKIP LOCKED lets multiple replicas race to claim runs without a distributed coordinator, and the health check surfaces a stuck worker before it becomes an incident.

---

### Phase 16 — Customer entity + order linkage

**What to do:**
1. Add `customers` table with unique `customer_code` and `tier` (`tier-1` / `tier-2` / `tier-3` stored as strings).
2. Implement `CustomerTier` enum with `ToDbString()` / `FromDbString()` extension methods.
3. Add `CustomersController` (`POST [sales-ops]`, `GET /{id}`, `GET /`) with `ICustomerService` + `CustomerService`.
4. Add nullable `customer_id` FK on `orders` (RESTRICT). Validate at `OrderService.CreateAsync` — return 422 if the ID is unknown.
5. LEFT JOIN `customers` in `OrderService.GetByIdAsync` to populate `customerCode` / `customerName`.
6. Accept optional `customerId` query param in `OrderService.ListAsync`.

**Why:**
Orders without a customer are still valid (backward-compatible). The nullable FK is the right model rather than a required relationship — it allows the allocation run to treat unlinked orders the same as before.

---

### Phase 17 — Customer contracts + two-pass allocation

**What to do:**
1. Add `customer_contracts` table; `CustomerContract` entity with `IsActiveOn(date)` helper; `IContractService` + `ContractService`; `ContractsController` nested under `/customers/{id}/contracts`.
2. Implement overlap guard in `CreateAsync`: reject any new contract that shares a `(customerId, skuId)` period with an existing one (open-ended existing contracts always overlap).
3. Add `GET /customers/{id}/contracts/utilization`: live `allocatedQty` against `floorQty`/`ceilingQty` for currently-active contracts (SQL `SUM(ol.allocated_qty)::int` — cast to int; Dapper cannot downcast `bigint` implicitly).
4. Register `DateOnlyTypeHandler` globally in `Program.cs` (`Dapper.SqlMapper.AddTypeHandler`) so Dapper can map `timestamp without time zone` columns returned as `DateTime` by Npgsql to `DateOnly` response properties.
5. Rewrite `RunPriorityAllocationAsync` as a two-pass loop:
   - **Floor pass:** load active contracts sorted by tier (Tier1 first) then `floor_qty DESC`. For each contract, call `AllocateCappedAsync` with a per-SKU cap equal to the remaining floor headroom; accumulate `ceilingSpent[(customerId, skuId)]`.
   - **Priority pass:** iterate all open orders `critical` → `high` → `standard` then `createdAt`. For orders belonging to a customer with a ceiling contract, pass `ceilingQty − ceilingSpent` as the per-SKU cap. Orders with no customer or no ceiling are fully uncapped.
6. Implement `AllocateCappedAsync` as the private entry point that `AllocateAsync` now delegates to, accepting an optional `IReadOnlyDictionary<Guid, int>? skuCapOverrides`.
7. Update `ApiFixture.ResetDatabaseAsync` to delete `customer_contracts` before `customers` and `skus` (FK order).

**Why:**
The floor pass must run before priority ordering to ensure contractual minimums are met regardless of order priority. Keeping both passes serial preserves the Phase 4 lock-order invariant — concurrent passes on the same SKU pool would re-introduce the circular-wait risk the deterministic lock order was designed to prevent.

---

## Success Criteria

1. Concurrent allocation scenarios pass integration tests with correct final quantities.
2. Cancelled orders restore inventory atomically.
3. API documents allocation outcomes (partial vs full) in response body.
4. You can explain isolation level or locking choice in interview terms.

---

## Self-Review Checklist

- [ ] All allocation paths run inside explicit transactions.
- [ ] SKU row touched in deterministic order to reduce deadlock risk (document if multi-SKU order).
- [ ] Swagger describes conflict and validation errors.
- [ ] Health checks verify database connectivity.
