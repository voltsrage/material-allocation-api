
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

public class AllocationService : IAllocationService
{
    private readonly AllocationDbContext _db;
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<AllocationService> _logger;

    public AllocationService(AllocationDbContext db, IDbConnectionFactory connectionFactory, ILogger<AllocationService> logger)
    {
        _db = db;
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<AllocationResponse> AllocateAsync(Guid orderId, CancellationToken ct = default)
    {
        return await AllocateCappedAsync(orderId, null, ct);
    }

    private async Task<AllocationResponse> AllocateCappedAsync(
        Guid orderId, IReadOnlyDictionary<Guid,int>? skuCapOverrides, CancellationToken ct = default)
    {
        // 1. Load the order and lines outside the transaction
        // Status check here is a fast pre-flight - the real guard is the locked state
        // inside the transaction, but we avoid acquiring locks for obviously invalid orders.
        var order = await _db.Orders
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct)
            ?? throw new NotFoundException($"Order {orderId} not found.");

        if(order.Status == OrderStatus.Cancelled)
            throw new ConflictException(
                "Cannot allocate a cancelled order", "ORDER_CANCELLED");

        if(order.Status == OrderStatus.FullyAllocated)
            throw new ConflictException(
                "Order is already fully allocated.",
                "ORDER_FULLY_ALLOCATED"
            );

        // 2/ Determine SKU IDs to lock. Sort ascending to guarantee all transactions
        // acquire locks in the same order - prevents circular waits between concurrent
        // allocations that share multiple SKUs
        var skuIds = order.Lines
            .Select(l => l.SkuId)
            .OrderBy(id => id)
            .ToArray();

        // 3. Begin explicit transaction. The FOR UPDATE lock must be held until the
        // SaveChanges commit - an implicit EF transaction would release too early.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        try
        {
            // 4. Lock SKU rows in deterministic order.
            // SELECT ... FOR UPDATE prevents any other transaction from reading or modifying
            // these rows until this transaction commits or rolls back.
            // ORDER BY id ensures the lock acquisition order matches our sorted skuIds

            var skus = await _db.Skus
                .FromSqlRaw(
                    "SELECT * FROM skus where Id = ANY(@ids) ORDER BY id FOR UPDATE",
                    new NpgsqlParameter("ids", skuIds)
                ).ToListAsync();

            var skuMap = skus.ToDictionary(s => s.Id);

            // Read reservations held by other orders for these SKUs.
            // Must run after the FOR UPDATE lock is acquired
            var conn = (NpgsqlConnection)_db.Database.GetDbConnection();
            var dbTx = (NpgsqlTransaction)tx.GetDbTransaction();

            var reservedRows = await conn.QueryAsync<(Guid SkuId, int Reserved)>
            (
                @"
                SELECT ol.sku_id as SkuId, COALESCE(SUM(r.quantity), 0) AS Reserved
                FROM reservations r
                JOIN order_lines ol ON ol.id = r.order_line_id
                WHERE ol.sku_id = ANY(@ids)
                    AND ol.order_id != @orderId
                    AND r.expires_at > NOW()
                GROUP BY ol.sku_id
                ",
                new {ids = skuIds, orderId},
                transaction: dbTx
            );

            var reservedByOthers = reservedRows.ToDictionary(r => r.SkuId, r => r.Reserved);

            // 5. Allocate each line in sku_id order (same as lock order - belt and suspenders)
            var results = new List<AllocationLineResult>();

            foreach(var line in order.Lines.OrderBy(l => l.SkuId))
            {
                if(!skuMap.TryGetValue(line.SkuId, out var sku))
                    throw new ValidationException(
                        $"SKU {line.SkuId} on order line was not found during allocation"
                    );

                var remaining = line.RequestedQty - line.AllocatedQty;

                var othersHeld = reservedByOthers.GetValueOrDefault(sku.Id);
                var available = Math.Max(0, sku.OnHand - othersHeld);
                var canAllocate = Math.Min(available, remaining);

                if(skuCapOverrides is not null && skuCapOverrides.TryGetValue(line.SkuId, out var cap))
                    canAllocate = Math.Min(canAllocate, cap);

                if(canAllocate > 0)
                {
                    // AllocateUnits decrements on_hand; Allocate increments allocated_qty.
                    // Both entities are tracked - SaveChangesAsync persists both atomically
                    sku.AllocateUnits(canAllocate);
                    line.Allocate(canAllocate);

                    _db.AllocationEvents.Add(new AllocationEvent(
                        AllocationEventType.AllocationCommitted, orderId, line.Id, line.SkuId, canAllocate
                    ));
                }

                results.Add(new AllocationLineResult(
                    line.SkuId,
                    sku.SkuCode,
                    line.RequestedQty,
                    line.AllocatedQty,
                    line.RequestedQty - line.AllocatedQty,
                    canAllocate // ThisRunQty - canAllocate is exactly what was allocated this call
                ));
            }

            // 6. Recompute order status from updated line quantities
            // RecomputeStatus reads Lines - they are loaded (Include above) and updated in-memory
            order.RecomputeStatus();

            _db.OutboxMessages.Add(new OutboxMessage("order.allocated", Helpers.Serialize(new
            {
                orderId = order.Id,
                referenceCode = order.ReferenceCode,
                priority = order.Priority.ToDbString(),
                status = order.Status.ToDbString(),
                isFullyAllocated = order.Status == OrderStatus.FullyAllocated,
                lines = results.Select(r => new
                {
                    skuId = r.SkuId,
                    skuCode = r.SkuCode,
                    allocatedQty = r.AllocatedQty,
                    remainingQty = r.RemainingQty
                })
            })));

            // 7. One SaveChangesAsync = one implicit transaction batch. Commits
            // - skus.on_hand decrements (one UPDATE per SKU)
            // - order_lines.allocated_qty increments (one UPDATE PER Line)
            // - order.status update
            // All or nothing. If any write fails, the catch block rolls back.

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            _logger.LogInformation(
                "Order {OrderId} allocated: status={Status}, lines={LineCount}, capped={IsCapped}.",
                orderId, order.Status.ToDbString(), results.Count, skuCapOverrides is not null);

            return new AllocationResponse(
                order.Id,
                order.Status.ToDbString(),
                order.Status == OrderStatus.FullyAllocated,
                results
            );
        }       
        catch (Exception)
        {
            await TransactionHelper.RollbackAsync(tx);
            throw;
        }
    }    

    public async Task<AvailabilityResponse> GetAvailabilityAsync(Guid skuId, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateAsync(ct);

        const string sql = @"
            SELECT 
                id as Id, 
                sku_code as SkuCode, 
                on_hand as OnHand
            FROM skus
            WHERE id = @SkuId
        ";

        var row = await connection.QueryFirstOrDefaultAsync<SkuAvailRow>(sql, new {SkuId = skuId});

        if(row is null)
            throw new NotFoundException($"SKU {skuId} not found.");

        // Formula: available = on_hand - reserved - allocated
        // on_hand is mutable: AllocateUnits() decrements it at commit time, so on_hand already
        // excludes committed allocations. Reserved = 0 until Phase 7 adds the reservations table.
        // Phase 7 will query: SELECT COALESCE(SUM(quantity), 0) FROM reservations
        // WHERE order_line_id in (...) AND expires_at > NOW()
        // and subtract that from on_hand here.
        var reserved = await connection.ExecuteScalarAsync<int>(
            @"
            SELECT COALESCE(SUM(r.quantity), 0)
            FROM reservations r
            JOIN order_lines ol ON ol.id = r.order_line_id
            WHERE ol.sku_id = @SkuId
                AND r.expires_at > NOW()
            ",
            new {SkuId = skuId}
        );
        return new AvailabilityResponse(row.Id, row.SkuCode, row.OnHand, reserved, Math.Max(0, row.OnHand - reserved));
    }

    public async Task<IReadOnlyList<AllocationEventResponse>> GetEventsAsync(Guid orderId, CancellationToken ct = default)
    {
        _ = await _db.Orders.FirstOrDefaultAsync(o => o.Id == orderId, ct)
            ?? throw new NotFoundException($"Order {orderId} not found!");

        using var conn = await _connectionFactory.CreateAsync(ct);

        var rows = await conn.QueryAsync<AllocationEventResponse>(
            @"
            SELECT
                id as Id,
                event_type as EventType,
                order_line_id as OrderLineId,
                sku_id as SkuId,
                quantity as Quantity,
                occurred_at as OccurredAt
            FROM allocation_events
            WHERE order_id = @orderId
            ORDER BY occurred_at ASC
            ",
            new {orderId}
        );

        return rows.ToList();
    }

    /*
    Running allocations in parallel would re-introduce the deadlock risk that Phase 4's deterministic lock order was designed to prevent. Priority ordering is meaningless if a `Standard` order and a `Critical` order race concurrently for the same SKU — the `Critical` order must complete first so its allocation is committed before `Standard` sees the remaining quantity. Sequential processing in sorted order is the correct model here.
    */
    public async Task<AllocationRunResponse> RunPriorityAllocationAsync(CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Load active contracts. Tier1 first (contractual obligations are highest priority).
        // then tier 2, the floor_qty descending with each tier(largest commitments first).
        var activeContracts = await _db.CustomerContracts
            .Include(c => c.Customer)
            .Where(c => 
                c.EffectiveFrom <= today &&
                (c.EffectiveTo == null || c.EffectiveTo >= today)
            )
            .OrderBy(c => c.Customer.Tier == CustomerTier.Tier3 ?  0:
                c.Customer.Tier == CustomerTier.Tier2 ? 1 : 2)
            .ThenByDescending(c => c.FloorQty)
            .ToListAsync(ct);

        // Load all open orders in priority order. This list is the input for both passes
        var openOrders = await _db.Orders
            .Include(o => o.Lines)
            .Where(o => o.Status != OrderStatus.FullyAllocated && o.Status != OrderStatus.Cancelled)
            .OrderBy(o => o.Priority == OrderPriority.Critical ? 0 :
                o.Priority == OrderPriority.High ? 1 : 2)
            .ThenBy(o => o.CreatedAt)
            .ToListAsync();

        // resultMap collects one entry per order, updated on each AllocatedCappedAsync call
        var resultMap = new Dictionary<Guid, AllocationRunResult>();

        // ceilingSpent[customerId, skuId] = units allocated on each AllocationCappedAsync call
        // across the entire run (floor pass + priority pass combined)
        var ceilingSpent = new Dictionary<(Guid, Guid), int>();

        var ordersByCustomer = openOrders
            .Where(o => o.CustomerId.HasValue)
            .GroupBy(o => o.CustomerId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());
        /*
        FLOOR PASS: honor contractual minimums before any priority ordering
        For reach active contract, allocate up to floor_qty across the customer's
        open orders for that SKU, in order priority order
        */
        foreach(var contract in activeContracts)
        {
            var remainingFloor = contract.FloorQty;

            if(!ordersByCustomer.TryGetValue(contract.CustomerId, out var customerOrders))
                continue;

            var eligibleOrders = customerOrders
                .Where(o => 
                    o.Status != OrderStatus.FullyAllocated &&
                    o.Status != OrderStatus.Cancelled &&
                    o.Lines.Any(l => l.SkuId == contract.SkuId)
                ); // priority + created_at order already inherited from openOrders

            foreach(var order in eligibleOrders)
            {
                if(remainingFloor <= 0) break;

                var skuCap = new Dictionary<Guid,int>{[contract.SkuId] = remainingFloor};

                try
                {
                    var result = await AllocateCappedAsync(order.Id, skuCap, ct);

                    foreach(var line in result.Lines)
                    {
                        if(line.ThisRunQty <= 0) continue;

                        var key = (contract.CustomerId, line.SkuId);

                        ceilingSpent[key] = ceilingSpent.GetValueOrDefault(key) + line.ThisRunQty;

                        if(line.SkuId == contract.SkuId)
                            remainingFloor -= line.ThisRunQty;
                    }

                    resultMap[order.Id] = new AllocationRunResult(
                        order.Id,
                        order.ReferenceCode,
                        order.Priority.ToDbString(),
                        result.Status,
                        result.IsFullyAllocated
                    );
                }
                catch (ConflictException)
                {
                    resultMap[order.Id] = new AllocationRunResult(
                        order.Id,
                        order.ReferenceCode,
                        order.Priority.ToDbString(),
                        order.Status.ToDbString(),
                        order.Status ==  OrderStatus.FullyAllocated
                    );
                }
            }
        }

        /*
        PRIORITY PASS: allocate remaining supply in priority order.
        For orders belonging to a customer with a ceiling, compute the remaining
        headroom as (ceiling - ceilingSpent) and pass it as a per-SKU cap.
        Orders with no customer or no ceiling are uncapped
        */

        // Build a lookup of active contracts by customerId ID for fast ceiling checks
        var contractsByCustomer = activeContracts
            .Where(c => c.CeilingQty.HasValue)
            .GroupBy(c=> c.CustomerId)
            .ToDictionary(g => g.Key, g => g.ToList());   

        foreach(var order in openOrders)
        {
            // EF tracking: order.Status and order.Lines.AllocatedQty were updated in memory
            // by AllocateCappedAsync calls in the floor pass (same DbContext instance)
            if(order.Status == OrderStatus.FullyAllocated || order.Status == OrderStatus.Cancelled)
            {
                // Already terminal; ensure it appears in results even if floor pass missed it
                resultMap.TryAdd(order.Id, new AllocationRunResult(
                    order.Id,
                    order.ReferenceCode,
                    order.Priority.ToDbString(),
                    order.Status.ToDbString(),
                    order.Status == OrderStatus.FullyAllocated
                ));

                continue;
            }

            IReadOnlyDictionary<Guid, int>? skuCaps = null;

            if(order.CustomerId.HasValue &&
                contractsByCustomer.TryGetValue(order.CustomerId.Value, out var customerContracts)
            )
            {
                var caps = new Dictionary<Guid, int>();

                foreach(var contract in customerContracts)
                {
                    var spent = ceilingSpent.GetValueOrDefault((order.CustomerId.Value, contract.SkuId));
                    var headroom = contract.CeilingQty!.Value - spent;
                    caps[contract.SkuId] = Math.Max(0, headroom);
                }

                if(caps.Count > 0) skuCaps = caps;
            }

            try
            {
                var result = await AllocateCappedAsync(order.Id, skuCaps, ct);

                if (order.CustomerId.HasValue)
                {
                    foreach(var line in result.Lines.Where(l => l.ThisRunQty > 0))
                    {
                        var key = (order.CustomerId.Value, line.SkuId);
                        ceilingSpent[key] = ceilingSpent.GetValueOrDefault(key) + line.ThisRunQty; 
                    }
                }

                resultMap[order.Id] = new AllocationRunResult(
                    order.Id,
                    order.ReferenceCode,
                    order.Priority.ToDbString(),
                    order.Status.ToDbString(),
                    order.Status == OrderStatus.FullyAllocated
                );
            }
            catch (ConflictException)
            {
                
                resultMap.TryAdd(order.Id, new AllocationRunResult(
                    order.Id,
                    order.ReferenceCode,
                    order.Priority.ToDbString(),
                    order.Status.ToDbString(),
                    order.Status == OrderStatus.FullyAllocated
                ));
            }
        }

        var results = resultMap.Values.ToList();
        var fullyAllocated = results.Count(r => r.IsFullyAllocated);

        return new AllocationRunResponse(
            OrdersProcessed: results.Count,
            OrdersFullyAllocated: fullyAllocated,
            OrdersPartiallyAllocated: results.Count - fullyAllocated,
            Results: results
        );
    }

    private record SkuAvailRow(Guid Id, string SkuCode, int OnHand);
}