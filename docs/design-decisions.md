# Design Decisions and Scope Rationale

Answers to the questions a supply chain or platform engineer would ask when reviewing this system.

---

## 1. Why does `GET /orders/{id}/lots` expose both `lotStatusAtAllocation` and `lotStatusNow`?

**Phase 22 closed the containment-reporting gap described in the original version of this section.**

The original endpoint returned only the lot's current status (`lots.status`), which answered the most common operational question — "is this material still in circulation?" — but produced misleading results for containment reports. If a lot was `available` when an order shipped and was quarantined afterward, the endpoint showed `quarantined` for units that shipped clean. A regulator or quality engineer could not determine from the response alone whether material was shipped *before* or *during* a hold.

**The fix:** `allocation_events` now carries a `lot_status_snapshot VARCHAR(20)` column populated at write time inside the same `SaveChangesAsync` call that commits the allocation. The snapshot is immutable for the lifetime of the event row. `GET /orders/{id}/lots` returns both fields:

- `lotStatusAtAllocation` — from `MIN(ae.lot_status_snapshot)` across the grouped events; `null` for rows written before Phase 22 and for fallthrough-path events (no `lot_id`).
- `lotStatusNow` — from `lots.status`; always the current state.

When both fields are present and equal, the lot has not changed status since the order shipped. When they diverge — `lotStatusAtAllocation = "available"`, `lotStatusNow = "quarantined"` — the material was clean at ship time and the hold was placed afterward, which is the core containment-report distinction.

**Why the snapshot over a correlated subquery:** the alternative reconstruction approach would query `lot_events` at read time:

```sql
COALESCE(
    (SELECT le.event_type
     FROM lot_events le
     WHERE le.lot_id = ae.lot_id
       AND le.occurred_at <= ae.occurred_at
     ORDER BY le.occurred_at DESC
     LIMIT 1),
    'available'   -- no prior transition = was available at that point
)  AS LotStatusAtAllocation
```

This was rejected for three reasons:

1. **Scale.** At Micron volumes a correlated subquery firing once per lot per response row becomes a per-row sequential scan under load. The snapshot is a single column read in the same join that already happens.

2. **Audit fidelity.** The snapshot records what the system *knew* when it committed the allocation. The correlated subquery reconstructs what the status *should have been* from the event log — correct in theory, but dependent on the completeness and ordering of `lot_events` rows. If a lot was created before the `lot_events` audit trail existed, the reconstruction produces `available` as a default assumption, not as a recorded fact.

3. **Query simplicity.** The read query stays a flat `GROUP BY` with no correlated subqueries.

---

## 2. Why is the lot model flat — no parent-child lot lineage?

**The domain scope is finished goods allocation, not fab WIP management.**

In semiconductor manufacturing, lot lineage (wafer lot → die lots → package lots) lives in the Manufacturing Execution System (MES). By the time finished goods units reach a distribution or allocation system, they are identified as a single physical batch — a box, a pallet, a shipment receipt — with a lot code assigned at the goods-receipt point. The lineage upstream of that receipt is a fab concern.

This project models the allocation layer: the system that answers "I have 10,000 DDR5-16G DIMMs across N lots; how do I distribute them to competing customer orders under priority and contract constraints?" That layer sees flat lots. It does not perform yield analysis, track wafer splits, or manage in-process material.

**If the domain were extended to cover fab WIP**, a parent-child lot structure would require:

- A `parent_lot_id` nullable self-FK on `lots`
- Recursive CTE queries to roll up `available_qty` across a lot tree
- Allocation logic that could consume from any leaf of a parent tree
- Traceability queries that traverse the tree upward ("which parent wafer did this DIMM come from?")

That's a substantially different system. For a finished goods allocation service, the flat model is correct.

---

## 3. Why are reservations at the SKU level rather than the lot level?

**Reservations model a soft hold on allocatable units, not a hold on specific physical material.**

A reservation answers the question: "hold N units of SKU-X for Order-Y while the customer confirms their purchase." The specific lots those units will come from are not known at reservation time — lot selection happens at allocation commit, using FIFO by `received_at`. Binding a reservation to a specific lot would require pre-selecting lots during reserve, which duplicates the allocation algorithm.

The quarantine mechanism is the system's lot-specific hold. `POST /lots/{id}/quarantine` atomically removes a lot from the allocatable pool by decrementing `sku.on_hand` by `lot.available_qty`. It is a hard, warehouse-ops-controlled action, not a soft customer-facing hold.

**The genuine gap** is a scenario like: "I want to tentatively set aside Lot-A for a specific high-value order while waiting for a spot inspection to clear, without formally quarantining it (which would require a release action to undo and triggers an audit event)." That's a lot-level soft reservation distinct from both the SKU-level reservation and the quarantine. Addressing it would require:

- A `lot_reservations` table (lot_id, order_id, quantity, expires_at)
- Modifications to the allocation query to exclude lot-reserved quantities from FIFO selection
- A cleanup job analogous to `ReservationExpiryJob`

It is a valid extension but was out of scope. The quarantine mechanism covers the quality hold use case; the SKU-level reservation covers the customer commit use case.

---

## 4. Does `onHand` in the SKU snapshot equal the sum of `available_qty` across available lots?

**It equals that sum only for purely lot-tracked SKUs.**

`skus.on_hand` is the authoritative "units currently allocatable" counter. It is incremented by:
- `POST /skus/{skuId}/lots` (lot receipt) — adds `lot.quantity` to `on_hand`
- `POST /lots/{id}/release` (quarantine release) — restores `lot.available_qty` to `on_hand`
- `POST /orders/{id}/cancel` (cancellation) — restores allocated quantities to `on_hand`

It is decremented by:
- `POST /orders/{id}/allocate` (allocation commit) — subtracts consumed units
- `POST /lots/{id}/quarantine` — removes `lot.available_qty` from `on_hand`
- `POST /lots/{id}/scrap` (from available) — removes `lot.available_qty` from `on_hand`
- `POST /skus/{id}/adjust` with a negative delta — direct stock write-off

For a SKU where all inventory was received via lots and no adjustments were made, `on_hand` will exactly equal `SUM(available_qty) WHERE status = 'available'`. This is the common case for a warehouse that intakes everything as tracked lots.

For a SKU that had `initialOnHand > 0` at creation, or that received `adjust` calls, there is a non-lot inventory component in `on_hand` that does not appear in the `lots` table. In the snapshot response:

```
onHand = summary[status='available'].totalAvailableQty + non_lot_inventory
```

The `summary` and `lots` arrays only reflect the lot-tracked portion. The `onHand` field reflects the full allocatable pool including the non-lot component.

This is intentional: `on_hand` is the number the allocation algorithm actually uses. A planner reading the snapshot can use `onHand` as the authoritative allocatable quantity and use the lot breakdown for traceability and quality management purposes. The two numbers being different signals that some inventory entered the system outside the lot-intake path, which is a valid state (pre-existing stock, manual corrections).

A future extension could add a computed `nonLotInventory = onHand - summary[available].totalAvailableQty` field to the snapshot response to make this explicit.
