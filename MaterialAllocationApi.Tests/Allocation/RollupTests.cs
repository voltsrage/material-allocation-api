using System.Net;
using System.Net.Http.Json;
using MaterialAllocationApi.Tests.Fixtures;
using MaterialAllocationApi.Tests.Helpers;

namespace MaterialAllocationApi.Tests.Rollup;

[Collection("Allocation")]
public class RollupTests(ApiFixture fixture) : AllocationTestBase(fixture)
{
    // ── Test 1: No open demand — endpoint returns empty ───────────────────────

    [Fact]
    public async Task GetSkuShortages_NoOpenDemand_ReturnsEmpty()
    {
        // Stock exists but no orders — nothing to be short.
        await CreateSkuAsync("ROLL-EMPTY-01", onHand: 100);

        var result = await GetShortagesAsync();

        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Items);
    }

    // ── Test 2: Fully allocated order — does NOT appear as shortage ───────────

    [Fact]
    public async Task GetSkuShortages_FullyAllocatedOrder_NotShort()
    {
        var skuId   = await CreateSkuAsync("ROLL-FULL-01", onHand: 5);
        var orderId = await CreateOrderAsync("ORD-ROLL-FULL-01", skuId, requestedQty: 5);

        // Allocate fully — allocated_qty == requested_qty on every line.
        await AllocateAsync(orderId);

        var result = await GetShortagesAsync();

        // A fully allocated order contributes 0 to open_demand, so no shortage.
        Assert.DoesNotContain(result.Items, r => r.SkuCode == "ROLL-FULL-01");
    }

    // ── Test 3: Open order with insufficient stock — shortage detected ─────────

    [Fact]
    public async Task GetSkuShortages_OpenOrderInsufficientStock_ReturnsShortage()
    {
        // 3 units on hand, order wants 10 — shortage of 7.
        var skuId   = await CreateSkuAsync("ROLL-SHORT-01", onHand: 3);
        var orderId = await CreateOrderAsync("ORD-ROLL-SHORT-01", skuId, requestedQty: 10);

        // Do NOT allocate — leaves the order in 'open' status.
        // open_demand = 10 - 0 = 10; available = 3; shortage = 7.

        var result = await GetShortagesAsync();

        var row = Assert.Single(result.Items, r => r.SkuCode == "ROLL-SHORT-01");
        Assert.Equal(3,  row.OnHand);
        Assert.Equal(0,  row.Reserved);
        Assert.Equal(3,  row.Available);
        Assert.Equal(10, row.OpenDemand);
        Assert.Equal(7,  row.Shortage);
    }

    // ── Test 4: Partially allocated order — remaining counts as open demand ───

    [Fact]
    public async Task GetSkuShortages_PartiallyAllocated_RemainingIsOpenDemand()
    {
        // 4 units on hand, order wants 10.
        // Partial allocation gives 4 units. Remaining = 6. No more stock → shortage of 6.
        var skuId   = await CreateSkuAsync("ROLL-PARTIAL-01", onHand: 4);
        var orderId = await CreateOrderAsync("ORD-ROLL-PARTIAL-01", skuId, requestedQty: 10);

        // Allocate: takes all 4 units; order becomes 'partially_allocated'.
        await AllocateAsync(orderId);

        // on_hand = 0 after allocation. open_demand = 10 - 4 = 6. shortage = 6 - 0 = 6.
        var result = await GetShortagesAsync();

        var row = Assert.Single(result.Items, r => r.SkuCode == "ROLL-PARTIAL-01");
        Assert.Equal(0, row.OnHand);
        Assert.Equal(0, row.Reserved);
        Assert.Equal(0, row.Available);
        Assert.Equal(6, row.OpenDemand);
        Assert.Equal(6, row.Shortage);
    }

    // ── Test 5: Reservations reduce available — shortage triggered ────────────

    [Fact]
    public async Task GetSkuShortages_WithReservations_ReservationsReduceAvailableTriggerShortage()
    {
        // 8 units on hand.
        // Order A reserves 6 units (soft hold — on_hand stays 8, reserved = 6, available = 2).
        // Order B has open demand for 5 units.
        // Without reservations: 5 demand vs 8 available — not short.
        // With reservations:    5 demand vs 2 available — shortage of 3.
        var skuId    = await CreateSkuAsync("ROLL-RES-01", onHand: 8);
        var orderAId = await CreateOrderAsync("ORD-ROLL-RES-A", skuId, requestedQty: 6);
        var orderBId = await CreateOrderAsync("ORD-ROLL-RES-B", skuId, requestedQty: 5);

        // Reserve for Order A (TTL = 60 min).
        var reserveResponse = await Client.PostAsJsonAsync(
            $"/api/v1/orders/{orderAId}/reserve",
            new { ttlMinutes = 60 });
        Assert.Equal(HttpStatusCode.OK, reserveResponse.StatusCode);

        // Do NOT allocate Order B — leaves it in 'open'.
        var result = await GetShortagesAsync();

        var row = Assert.Single(result.Items, r => r.SkuCode == "ROLL-RES-01");
        Assert.Equal(8, row.OnHand);
        Assert.Equal(6, row.Reserved);
        Assert.Equal(2, row.Available);
        Assert.Equal(5, row.OpenDemand);
        Assert.Equal(3, row.Shortage);  // 5 - 2
    }

    // ── Test 6: Ordering — worst shortage appears first ───────────────────────

    [Fact]
    public async Task GetSkuShortages_MultipleShortSkus_OrderedByShortageDescending()
    {
        // Two short SKUs. SKU-LARGE has shortage 10; SKU-SMALL has shortage 2.
        // The endpoint must return SKU-LARGE first.
        var skuLargeId = await CreateSkuAsync("ROLL-ORDER-LARGE", onHand: 0);
        var skuSmallId = await CreateSkuAsync("ROLL-ORDER-SMALL", onHand: 3);

        await CreateOrderAsync("ORD-ROLL-ORDER-L", skuLargeId, requestedQty: 10);
        await CreateOrderAsync("ORD-ROLL-ORDER-S", skuSmallId, requestedQty: 5);

        var result = await GetShortagesAsync();

        var rows = result.Items
            .Where(r => r.SkuCode is "ROLL-ORDER-LARGE" or "ROLL-ORDER-SMALL")
            .ToList();

        Assert.Equal(2, rows.Count);
        Assert.Equal("ROLL-ORDER-LARGE", rows[0].SkuCode);  // shortage = 10
        Assert.Equal("ROLL-ORDER-SMALL", rows[1].SkuCode);  // shortage = 2
        Assert.Equal(10, rows[0].Shortage);
        Assert.Equal(2,  rows[1].Shortage);
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private async Task<PagedResult<SkuShortageResponse>> GetShortagesAsync(
        int page = 1, int pageSize = 100)
    {
        var response = await Client.GetAsync(
            $"/api/v1/rollup/sku-shortages?page={page}&pageSize={pageSize}");
        response.EnsureSuccessStatusCode();
        return await ReadAsync<PagedResult<SkuShortageResponse>>(response);
    }
}