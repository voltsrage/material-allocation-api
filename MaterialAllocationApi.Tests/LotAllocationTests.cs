using System.Net;
using Dapper;
using MaterialAllocationApi.Tests.Fixtures;
using MaterialAllocationApi.Tests.Helpers;
using Npgsql;

namespace MaterialAllocationApi.Tests;

[Collection("Allocation")]
public class LotAllocationTests(ApiFixture fixture) : AllocationTestBase(fixture)
{
    // ── 8a-i — Single lot, partial consumption ────────────────────────────────

    [Fact]
    public async Task Allocate_SingleLot_PartialConsumption()
    {
        var skuId  = await CreateSkuAsync("LALLOC-A1", onHand: 0);
        var lotA   = await CreateLotAsync(skuId, "LA-LOT-001", 100);

        var orderId = await CreateOrderAsync("LALLOC-ORD-A1", skuId, requestedQty: 60);
        var (_, body) = await AllocateAsync(orderId);

        Assert.NotNull(body);
        Assert.Equal("fully_allocated", body.Status);
        Assert.True(body.IsFullyAllocated);

        var line = body.Lines.Single();
        Assert.Equal(60, line.AllocatedQty);
        Assert.Equal(0,  line.RemainingQty);
        Assert.NotNull(line.LotAllocations);
        Assert.Single(line.LotAllocations!);

        var lotDetail = line.LotAllocations!.Single();
        Assert.Equal(lotA.Id,       lotDetail.LotId);
        Assert.Equal("LA-LOT-001",  lotDetail.LotCode);
        Assert.Equal(60,            lotDetail.QuantityConsumed);

        Assert.Equal(40, await GetLotAvailableQtyAsync(lotA.Id));
        Assert.Equal("available",   await GetLotStatusAsync(lotA.Id));
        Assert.Equal(40, await GetOnHandAsync(skuId));
    }

    // ── 8a-ii — Multi-lot FIFO span ───────────────────────────────────────────

    [Fact]
    public async Task Allocate_MultiLot_FifoSpan()
    {
        var skuId = await CreateSkuAsync("LALLOC-A2", onHand: 0);
        var t     = DateTimeOffset.UtcNow.AddHours(-2);
        var lotA  = await CreateLotAsync(skuId, "LA-LOT-A", 50, receivedAt: t);
        var lotB  = await CreateLotAsync(skuId, "LA-LOT-B", 80, receivedAt: t.AddMinutes(1));

        var orderId = await CreateOrderAsync("LALLOC-ORD-A2", skuId, requestedQty: 70);
        var (_, body) = await AllocateAsync(orderId);

        Assert.NotNull(body);
        Assert.Equal("fully_allocated", body.Status);

        var line = body.Lines.Single();
        Assert.NotNull(line.LotAllocations);
        Assert.Equal(2, line.LotAllocations!.Count);

        var detailA = line.LotAllocations.Single(d => d.LotId == lotA.Id);
        Assert.Equal(50, detailA.QuantityConsumed);

        var detailB = line.LotAllocations.Single(d => d.LotId == lotB.Id);
        Assert.Equal(20, detailB.QuantityConsumed);

        Assert.Equal(0,          await GetLotAvailableQtyAsync(lotA.Id));
        Assert.Equal("depleted", await GetLotStatusAsync(lotA.Id));
        Assert.Equal(60,         await GetLotAvailableQtyAsync(lotB.Id));
        Assert.Equal("available",await GetLotStatusAsync(lotB.Id));
    }

    // ── 8a-iii — FIFO order is by received_at, not by insert order ────────────

    [Fact]
    public async Task Allocate_FifoOrderByReceivedAt_NotInsertOrder()
    {
        var skuId    = await CreateSkuAsync("LALLOC-A3", onHand: 0);
        var today    = DateTimeOffset.UtcNow;
        var yesterday = today.AddDays(-1);

        // Insert Lot-B first (later received_at), then Lot-A (older received_at).
        var lotB = await CreateLotAsync(skuId, "LA-LOT-B-NEWER", 50, receivedAt: today);
        var lotA = await CreateLotAsync(skuId, "LA-LOT-A-OLDER", 50, receivedAt: yesterday);

        var orderId = await CreateOrderAsync("LALLOC-ORD-A3", skuId, requestedQty: 30);
        var (_, body) = await AllocateAsync(orderId);

        Assert.NotNull(body);
        var line = body.Lines.Single();
        Assert.NotNull(line.LotAllocations);

        // Lot-A (older, inserted second) must be consumed first due to FIFO on received_at.
        Assert.Single(line.LotAllocations!);
        var detail = line.LotAllocations!.Single();
        Assert.Equal(lotA.Id, detail.LotId);
        Assert.Equal(30, detail.QuantityConsumed);

        Assert.Equal(20, await GetLotAvailableQtyAsync(lotA.Id));
        Assert.Equal(50, await GetLotAvailableQtyAsync(lotB.Id));
    }

    // ── 8a-iv — Lot marked Depleted when fully consumed ───────────────────────

    [Fact]
    public async Task Allocate_FullyConsumesLot_StatusBecomesDepleted()
    {
        var skuId   = await CreateSkuAsync("LALLOC-A4", onHand: 0);
        var lotA    = await CreateLotAsync(skuId, "LA-LOT-DEP-001", 40);

        var orderId = await CreateOrderAsync("LALLOC-ORD-A4", skuId, requestedQty: 40);
        var (_, body) = await AllocateAsync(orderId);

        Assert.NotNull(body);
        Assert.Equal("fully_allocated", body.Status);
        Assert.True(body.IsFullyAllocated);

        Assert.Equal(0,          await GetLotAvailableQtyAsync(lotA.Id));
        Assert.Equal("depleted", await GetLotStatusAsync(lotA.Id));
        Assert.Equal(0,          await GetOnHandAsync(skuId));
    }

    // ── 8a-v — Partial allocation when lots are insufficient ─────────────────

    [Fact]
    public async Task Allocate_LotInsufficientForOrder_PartiallyAllocates()
    {
        var skuId   = await CreateSkuAsync("LALLOC-A5", onHand: 0);
        var lotA    = await CreateLotAsync(skuId, "LA-LOT-PART-001", 30);

        var orderId = await CreateOrderAsync("LALLOC-ORD-A5", skuId, requestedQty: 60);
        var (_, body) = await AllocateAsync(orderId);

        Assert.NotNull(body);
        Assert.Equal("partially_allocated", body.Status);
        Assert.False(body.IsFullyAllocated);

        var line = body.Lines.Single();
        Assert.Equal(30, line.AllocatedQty);
        Assert.Equal(30, line.RemainingQty);

        Assert.Equal(0,          await GetLotAvailableQtyAsync(lotA.Id));
        Assert.Equal("depleted", await GetLotStatusAsync(lotA.Id));
    }

    // ── 8a-vi — LotAllocations reflects actual breakdown ─────────────────────

    [Fact]
    public async Task Allocate_TwoLots_LotAllocationsBreakdownSumsCorrectly()
    {
        var skuId = await CreateSkuAsync("LALLOC-A6", onHand: 0);
        var t     = DateTimeOffset.UtcNow.AddHours(-1);
        var lotA  = await CreateLotAsync(skuId, "LA-LOT-BRK-A", 25, receivedAt: t);
        var lotB  = await CreateLotAsync(skuId, "LA-LOT-BRK-B", 35, receivedAt: t.AddMinutes(5));

        var orderId = await CreateOrderAsync("LALLOC-ORD-A6", skuId, requestedQty: 50);
        var (_, body) = await AllocateAsync(orderId);

        Assert.NotNull(body);
        var line = body.Lines.Single();
        Assert.NotNull(line.LotAllocations);
        Assert.Equal(2, line.LotAllocations!.Count);

        var totalConsumed = line.LotAllocations.Sum(d => d.QuantityConsumed);
        Assert.Equal(50, totalConsumed);

        var detailA = line.LotAllocations.Single(d => d.LotId == lotA.Id);
        Assert.Equal(25, detailA.QuantityConsumed);

        var detailB = line.LotAllocations.Single(d => d.LotId == lotB.Id);
        Assert.Equal(25, detailB.QuantityConsumed);
    }

    // ── 8b-i — SKU with no lots uses fallthrough on_hand behavior ────────────

    [Fact]
    public async Task Allocate_SkuWithNoLots_UsesFallthroughPath_NullLotAllocations()
    {
        var skuId   = await CreateSkuAsync("LALLOC-B1", onHand: 50);
        var orderId = await CreateOrderAsync("LALLOC-ORD-B1", skuId, requestedQty: 30);

        var (_, body) = await AllocateAsync(orderId);

        Assert.NotNull(body);
        Assert.Equal("fully_allocated", body.Status);

        var line = body.Lines.Single();
        Assert.Equal(30, line.AllocatedQty);
        Assert.Equal(0,  line.RemainingQty);
        Assert.Null(line.LotAllocations);

        Assert.Equal(20, await GetOnHandAsync(skuId));
    }

    // ── 8c-i — Cancel restores lot's AvailableQty ────────────────────────────

    [Fact]
    public async Task Cancel_AfterLotAllocation_RestoresLotAvailableQty()
    {
        var skuId   = await CreateSkuAsync("LALLOC-C1", onHand: 0);
        var lotA    = await CreateLotAsync(skuId, "LC-LOT-001", 100);

        var orderId = await CreateOrderAsync("LALLOC-ORD-C1", skuId, requestedQty: 60);
        await AllocateAsync(orderId);

        Assert.Equal(40, await GetLotAvailableQtyAsync(lotA.Id));

        var response = await Client.PostAsync($"/api/v1/orders/{orderId}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(100,         await GetLotAvailableQtyAsync(lotA.Id));
        Assert.Equal("available", await GetLotStatusAsync(lotA.Id));
        Assert.Equal(100,         await GetOnHandAsync(skuId));
    }

    // ── 8c-ii — Cancel transitions Depleted lot back to Available ─────────────

    [Fact]
    public async Task Cancel_DepletedLot_TransitionsBackToAvailable()
    {
        var skuId   = await CreateSkuAsync("LALLOC-C2", onHand: 0);
        var lotA    = await CreateLotAsync(skuId, "LC-LOT-DEP-001", 50);

        var orderId = await CreateOrderAsync("LALLOC-ORD-C2", skuId, requestedQty: 50);
        await AllocateAsync(orderId);

        Assert.Equal(0,          await GetLotAvailableQtyAsync(lotA.Id));
        Assert.Equal("depleted", await GetLotStatusAsync(lotA.Id));

        var response = await Client.PostAsync($"/api/v1/orders/{orderId}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(50,          await GetLotAvailableQtyAsync(lotA.Id));
        Assert.Equal("available", await GetLotStatusAsync(lotA.Id));
        Assert.Equal(50,          await GetOnHandAsync(skuId));
    }

    // ── 8c-iii — Cancel spanning multiple lots restores all of them ───────────

    [Fact]
    public async Task Cancel_MultiLotOrder_RestoresAllLots()
    {
        var skuId = await CreateSkuAsync("LALLOC-C3", onHand: 0);
        var t     = DateTimeOffset.UtcNow.AddHours(-2);
        var lotA  = await CreateLotAsync(skuId, "LC-LOT-MULTI-A", 30, receivedAt: t);
        var lotB  = await CreateLotAsync(skuId, "LC-LOT-MULTI-B", 40, receivedAt: t.AddMinutes(1));

        var orderId = await CreateOrderAsync("LALLOC-ORD-C3", skuId, requestedQty: 60);
        await AllocateAsync(orderId);

        // After allocation: Lot-A depleted, Lot-B has 10 remaining.
        Assert.Equal(0,          await GetLotAvailableQtyAsync(lotA.Id));
        Assert.Equal("depleted", await GetLotStatusAsync(lotA.Id));
        Assert.Equal(10,         await GetLotAvailableQtyAsync(lotB.Id));

        var response = await Client.PostAsync($"/api/v1/orders/{orderId}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(30,          await GetLotAvailableQtyAsync(lotA.Id));
        Assert.Equal("available", await GetLotStatusAsync(lotA.Id));
        Assert.Equal(40,          await GetLotAvailableQtyAsync(lotB.Id));
        Assert.Equal("available", await GetLotStatusAsync(lotB.Id));
        Assert.Equal(70,          await GetOnHandAsync(skuId));
    }

    // ── 8d-i — FIFO preserved across orders in a priority run ─────────────────

    [Fact]
    public async Task PriorityRun_FifoPreservedAcrossOrders()
    {
        var skuId = await CreateSkuAsync("LALLOC-D1", onHand: 0);
        var t     = DateTimeOffset.UtcNow.AddHours(-2);
        var lotA  = await CreateLotAsync(skuId, "LD-LOT-A", 30, receivedAt: t);
        var lotB  = await CreateLotAsync(skuId, "LD-LOT-B", 30, receivedAt: t.AddMinutes(1));

        var criticalId = await CreateOrderAsync("LALLOC-ORD-D1-CRIT", skuId, requestedQty: 25, priority: "critical");
        var standardId = await CreateOrderAsync("LALLOC-ORD-D1-STD",  skuId, requestedQty: 35, priority: "standard");

        var runId = await SubmitAllocationRunAsync();
        await TriggerAllocationWorkerAsync();
        var run = await PollRunUntilCompleteAsync(runId);

        Assert.Equal("completed", run.Status);

        var critResult = run.Results!.Single(r => r.OrderId == criticalId);
        Assert.True(critResult.IsFullyAllocated);

        var stdResult = run.Results!.Single(r => r.OrderId == standardId);
        Assert.True(stdResult.IsFullyAllocated);

        // All 60 lot units consumed — both lots depleted.
        Assert.Equal(0,          await GetLotAvailableQtyAsync(lotA.Id));
        Assert.Equal("depleted", await GetLotStatusAsync(lotA.Id));
        Assert.Equal(0,          await GetLotAvailableQtyAsync(lotB.Id));
        Assert.Equal("depleted", await GetLotStatusAsync(lotB.Id));
        Assert.Equal(0,          await GetOnHandAsync(skuId));

        // Verify critical consumed from Lot-A (FIFO) via allocation_events.
        var critLotQty = await GetAllocatedQtyFromLotAsync(criticalId, lotA.Id);
        Assert.Equal(25, critLotQty);

        // Standard consumed the Lot-A remainder then all of Lot-B.
        var stdLotAQty = await GetAllocatedQtyFromLotAsync(standardId, lotA.Id);
        var stdLotBQty = await GetAllocatedQtyFromLotAsync(standardId, lotB.Id);
        Assert.Equal(5,  stdLotAQty);
        Assert.Equal(30, stdLotBQty);
    }

    // ── 8e-i — Two concurrent allocations for the same lot ───────────────────

    [Fact]
    public async Task ConcurrentAllocations_SameLot_ConservationHolds()
    {
        var skuId   = await CreateSkuAsync("LALLOC-E1", onHand: 0);
        var lotA    = await CreateLotAsync(skuId, "LE-LOT-001", 100);

        var orderAId = await CreateOrderAsync("LALLOC-ORD-E1-A", skuId, requestedQty: 80);
        var orderBId = await CreateOrderAsync("LALLOC-ORD-E1-B", skuId, requestedQty: 80);

        var taskA = AllocateAsync(orderAId);
        var taskB = AllocateAsync(orderBId);
        var results = await Task.WhenAll(taskA, taskB);

        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.Status));

        var fullyAllocated = results.Count(r => r.Body!.IsFullyAllocated);
        Assert.Equal(1, fullyAllocated);

        // Conservation: on_hand + total_allocated = 100 (original lot quantity).
        var onHand     = await GetOnHandAsync(skuId);
        var totalAlloc = await GetTotalAllocatedAsync(skuId);
        Assert.Equal(100, onHand + totalAlloc);

        // Lot invariant: lot.available_qty + total_allocated_from_lot = 100.
        var lotAvail      = await GetLotAvailableQtyAsync(lotA.Id);
        var totalFromLot  = await GetTotalAllocatedFromLotAsync(lotA.Id);
        Assert.Equal(100, lotAvail + totalFromLot);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<int> GetAllocatedQtyFromLotAsync(Guid orderId, Guid lotId)
    {
        await using var conn = new NpgsqlConnection(Fixture.GetConnectionString());
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<int>(
            """
            SELECT COALESCE(SUM(quantity), 0)
            FROM allocation_events
            WHERE order_id   = @orderId
              AND lot_id     = @lotId
              AND event_type = 'allocation_committed'
            """,
            new { orderId, lotId });
    }

    private async Task<int> GetTotalAllocatedFromLotAsync(Guid lotId)
    {
        await using var conn = new NpgsqlConnection(Fixture.GetConnectionString());
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<int>(
            """
            SELECT COALESCE(SUM(quantity), 0)
            FROM allocation_events
            WHERE lot_id     = @lotId
              AND event_type = 'allocation_committed'
            """,
            new { lotId });
    }
}
