# How the Material Allocation API Works

This document walks through the full lifecycle of the system — from an empty warehouse on day one through customers placing orders and staff managing inventory. It is intended for anyone new to the project who wants to understand what the API does and why.

---

## Core concepts

Before walking through the steps, these are the four things the system revolves around:

| Concept | What it is |
|---|---|
| **SKU** | A product type (e.g. "M8 hex bolt"). Owns the authoritative `OnHand` count. |
| **Lot** | A physical batch of a SKU received at a specific time. Has its own `AvailableQty` and a status (`Available`, `Quarantined`, `Depleted`, `Scrapped`). |
| **Order** | A customer's request for one or more SKUs. Has lines, each requesting a quantity of one SKU. |
| **Customer Contract** | A per-customer, per-SKU agreement that guarantees a minimum (`FloorQty`) and optionally caps a maximum (`CeilingQty`) during each allocation run. |

The key invariant: `sku.OnHand` always equals the sum of all `Available` lots' `AvailableQty` for that SKU (plus any direct manual adjustments). Every operation that changes a lot's quantity has a matching call on the SKU to keep them in sync.

---

## Step 1 — Staff registers SKUs

`POST /skus`

Before any stock arrives, staff define the product catalog. Each SKU is given a unique code, a description, and starts with `OnHand = 0`.

```json
{
  "skuCode": "BOLT-M8",
  "description": "M8 hex bolt",
  "initialOnHand": 0
}
```

Nothing physical exists yet — this is just the catalog entry that everything else will be linked to.

---

## Step 2 — A shipment arrives; staff receives lots

`POST /skus/{skuId}/lots`

When physical stock arrives, staff register it as a **lot** — a traceable batch tied to the SKU with a unique lot code, a quantity, and the date it was received.

```json
{
  "lotCode": "LOT-2026-001",
  "quantity": 500,
  "receivedAt": "2026-05-29T08:00:00Z"
}
```

What happens:
- A `Lot` row is created: `AvailableQty = 500`, `Status = Available`
- The SKU's `OnHand` is incremented by `500`
- An event (`lot.received`) is published via the outbox for downstream systems

If three separate shipments arrive for the same SKU, there are three lots. The SKU's `OnHand` is the running sum of all their `AvailableQty`. The lot code must be globally unique — the API rejects duplicates.

---

## Step 3 — Staff registers customers

`POST /customers`

Customers are the entities that will place orders. They carry a **tier** (`tier1`, `tier2`, `tier3`) which determines their priority during automated allocation runs — Tier 1 customers have their contractual obligations honored first.

```json
{
  "customerCode": "CUST-001",
  "name": "Acme Corp",
  "tier": "tier1"
}
```

---

## Step 4 — (Optional) Staff sets a contract for a customer

`POST /contracts`

A contract is a per-customer, per-SKU agreement that governs how the allocation run treats that customer:

- **`floorQty`** — the minimum units guaranteed to this customer per allocation run, honored before any priority-ordering begins
- **`ceilingQty`** — the maximum units this customer can receive in a single run (optional; omit for uncapped)
- **`effectiveFrom` / `effectiveTo`** — the date range during which the contract is active

```json
{
  "customerId": "...",
  "skuId": "...",
  "floorQty": 100,
  "ceilingQty": 300,
  "effectiveFrom": "2026-05-01"
}
```

A customer with a `floorQty` of 100 is guaranteed those 100 units before any other customer's orders are processed — even lower-priority customers with contracts are served their floors before uncapped orders compete for the remainder.

---

## Step 5 — A customer places an order

`POST /orders`

An order is a request for one or more SKUs. Each line specifies a SKU and a quantity. Orders carry a **priority** (`critical`, `high`, `standard`) which governs their position in the allocation queue.

```json
{
  "referenceCode": "ORD-0001",
  "priority": "high",
  "customerId": "...",
  "lines": [
    { "skuId": "...", "requestedQty": 150 }
  ]
}
```

What happens:
- All referenced SKU IDs and the customer ID are validated
- The order is saved with `Status = Open` and `AllocatedQty = 0` on every line
- An event (`order.created`) is published
- No inventory is touched — the order is a demand record only

---

## Step 6 — (Optional) Customer reserves stock

`POST /orders/{orderId}/reserve`

A reservation is a **soft hold** on inventory. It does not decrement `OnHand`, but it tells the system "this order is actively intending to buy these units — don't give them to someone else right now."

```json
{ "ttlMinutes": 15 }
```

What happens:
- SKU rows are locked to prevent concurrent reservations from racing
- The system checks how much stock other orders currently hold in active reservations
- New `Reservation` rows are created for what is available after accounting for those holds
- Reservations expire automatically after the TTL; a background job hard-deletes them and writes audit events

Calling reserve again on the same order replaces any existing reservations and refreshes the TTL — the operation is idempotent.

During allocation, the engine subtracts active reservations held by *other* orders from the available supply before deciding how much this order can receive.

---

## Step 7 — Allocation commits inventory to orders

### Single order
`POST /allocation/allocate/{orderId}`

Allocates one specific order immediately against current supply.

### Priority run
`POST /allocation/run`

Processes all open orders in two passes. This is the normal operating mode.

**Pass 1 — Floor pass**

Active contracts are processed highest-tier first, largest floor first. For each contract, the customer's open orders for that SKU are allocated up to the contract's `floorQty`. This ensures contractual minimums are honored before priority ordering begins.

**Pass 2 — Priority pass**

All remaining open orders are processed in priority order: `Critical` → `High` → `Standard`, then by creation time within each priority. For customers with ceiling contracts, the system computes remaining headroom (`ceilingQty - unitsAlreadyAllocatedThisRun`) and uses it as a cap.

**Inside each allocation:**

1. SKU rows are locked with `SELECT FOR UPDATE` in ascending ID order — this deterministic order prevents deadlocks when two concurrent allocations share the same SKUs
2. Available lot rows for those SKUs are also locked in the same way
3. Lots are sorted **FIFO by received date** — the oldest batch is consumed first
4. Available supply is computed as `Min(sku.OnHand - reservedByOthers, sum of lot.AvailableQty)` — the lot sum cap prevents stale `OnHand` values from inflating what lots can actually provide
5. Lots are consumed in FIFO order — `lot.AvailableQty` decrements; a lot transitions to `Depleted` when it hits zero
6. `sku.OnHand` decrements by the same total
7. One `AllocationEvent` is written **per lot consumed**, each recording the `LotId` — this is the audit trail linking every allocated unit back to its physical batch
8. The order line's `AllocatedQty` increments; the order transitions to `PartiallyAllocated` or `FullyAllocated`
9. An event (`order.allocated`) is published

If a SKU has no lots (only manual `OnHand` adjustments), the system falls back to pure `OnHand` arithmetic with no per-lot tracking.

---

## Step 8 — Staff handles quality issues

### Quarantine a lot
`POST /lots/{lotId}/quarantine`

If a batch is found to be suspect after receipt, staff quarantine it. The lot's status moves to `Quarantined` and `OnHand` is decremented by the lot's `AvailableQty`. Quarantined units are invisible to allocation.

### Release a lot
`POST /lots/{lotId}/release`

Once the lot passes inspection, it is released. Status returns to `Available` and `OnHand` is restored.

### Scrap a lot
`POST /lots/{lotId}/scrap`

Permanently destroys the lot. If the lot was `Available`, `OnHand` is decremented. If it was already `Quarantined`, `OnHand` was decremented at quarantine time and is not touched again — no double-deduction.

Every transition writes a `LotEvent` (the lot's own audit log) and an outbox event.

---

## Step 9 — An order is cancelled

`DELETE /orders/{orderId}`

If a customer cancels an order that was already (partially) allocated:

1. The system reads `AllocationEvents` to find exactly which lots were consumed by this order and how many units came from each
2. `lot.Restore(qty)` is called on each — a `Depleted` lot that was emptied by this order becomes `Available` again
3. `sku.OnHand` is incremented by the total released
4. Any active reservations for the order are deleted
5. An event (`order.cancelled`) is published

If the order had never been allocated (all lines at zero), the fast path skips lock acquisition entirely — no inventory change is needed.

---

## What happens in the database per operation

| Operation | Lot change | SKU OnHand change |
|---|---|---|
| Receive lot | `AvailableQty = quantity`, `Status = Available` | +quantity |
| Quarantine lot | `Status = Quarantined` | -AvailableQty |
| Release lot | `Status = Available` | +AvailableQty |
| Scrap lot (Available) | `AvailableQty = 0`, `Status = Scrapped` | -AvailableQty |
| Scrap lot (Quarantined) | `AvailableQty = 0`, `Status = Scrapped` | no change |
| Allocate | `AvailableQty -= qty`; `Status = Depleted` if 0 | -qty |
| Cancel allocated order | `AvailableQty += qty`; `Status = Available` if was Depleted | +qty |

---

## Outbox events published

Every state-changing operation publishes a structured event to the `outbox_messages` table. A background relay job (`OutboxRelayJob`) picks these up and forwards them to external consumers. Events are guaranteed to be published at least once because they are written in the same database transaction as the state change.

| Event | Trigger |
|---|---|
| `lot.received` | Lot intake |
| `lot.quarantined` | Lot quarantined |
| `lot.released` | Lot released from quarantine |
| `lot.scrapped` | Lot scrapped |
| `order.created` | Order placed |
| `order.allocated` | Allocation committed |
| `order.cancelled` | Order cancelled |
| `reservation.created` | Reservation placed |
| `reservation.released` | Reservation manually released |
| `reservation.expired` | Reservation expired by background job |
