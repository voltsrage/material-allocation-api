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
| 9 | Swagger, health, Seq, checklist |

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
1. Swagger completeness; health checks; structured logging.
2. Self-review checklist in this PRD.

**Why:**
Allocation APIs fail in production under ambiguity — docs and logs narrow incident time.

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
