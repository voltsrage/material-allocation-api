using System.Net;
using MaterialAllocationApi.Tests.Fixtures;
using MaterialAllocationApi.Tests.Helpers;

namespace MaterialAllocationApi.Tests.Allocation;

public class AllocationFlowTests(ApiFixture fixture) : AllocationTestBase(fixture)
{
    // ── Test 1: The PRD-required partial → full completion scenario ────────────

    [Fact]
    public async Task PartialAllocation_ThenRestock_SecondRunFullyAllocates()
    {
        // Arrange: 5 units available, order wants 10.
        var skuId   = await CreateSkuAsync("FLOW-PARTIAL-01", onHand: 5);
        var orderId = await CreateOrderAsync("ORD-FLOW-01", skuId, requestedQty: 10);

        // Act 1: partial allocation.
        var (_, partial) = await AllocateAsync(orderId);

        Assert.Equal("partially_allocated", partial!.Status);
        Assert.False(partial.IsFullyAllocated);
        var firstLine = partial.Lines.Single();
        Assert.Equal(5,  firstLine.AllocatedQty);
        Assert.Equal(5,  firstLine.RemainingQty);

        // Verify on_hand dropped to 0 after partial allocation.
        Assert.Equal(0, await GetOnHandAsync(skuId));

        // Arrange for second run: restock 5 more units.
        await AdjustSkuAsync(skuId, delta: 5, reason: "Restocked for test");

        // Act 2: second allocation run completes the order.
        var (_, full) = await AllocateAsync(orderId);

        Assert.Equal("fully_allocated", full!.Status);
        Assert.True(full.IsFullyAllocated);
        var secondLine = full.Lines.Single();
        Assert.Equal(10, secondLine.AllocatedQty);   // cumulative total
        Assert.Equal(0,  secondLine.RemainingQty);

        // DB invariant after both runs: on_hand = 0, total allocated = 10.
        var onHand     = await GetOnHandAsync(skuId);
        var totalAlloc = await GetTotalAllocatedAsync(skuId);

        Assert.Equal(0,  onHand);
        Assert.Equal(10, totalAlloc);
    }

    // ── Test 2: Zero stock — allocation is a no-op, status unchanged ──────────

    [Fact]
    public async Task Allocate_ZeroStock_ReturnsOpenStatusWithZeroAllocated()
    {
        var skuId   = await CreateSkuAsync("FLOW-ZERO-01", onHand: 0);
        var orderId = await CreateOrderAsync("ORD-ZERO-01", skuId, requestedQty: 5);

        var (status, body) = await AllocateAsync(orderId);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("open",  body!.Status);
        Assert.False(body.IsFullyAllocated);

        var line = body.Lines.Single();
        Assert.Equal(0, line.AllocatedQty);
        Assert.Equal(5, line.RemainingQty);

        // DB: on_hand stays 0, no units moved.
        Assert.Equal(0, await GetOnHandAsync(skuId));
        Assert.Equal(0, await GetTotalAllocatedAsync(skuId));
    }

    // ── Test 3: Multi-line order — all lines allocated in one transaction ──────

    [Fact]
    public async Task Allocate_MultiLineSufficientStock_AllLinesFullyAllocated()
    {
        var skuAId = await CreateSkuAsync("FLOW-MULTI-A", onHand: 20);
        var skuBId = await CreateSkuAsync("FLOW-MULTI-B", onHand: 30);

        var orderId = await CreateOrderMultiLineAsync(
            "ORD-MULTI-01",
            [(skuAId, 10), (skuBId, 15)]);

        var (_, body) = await AllocateAsync(orderId);

        Assert.Equal("fully_allocated", body!.Status);
        Assert.True(body.IsFullyAllocated);
        Assert.Equal(2, body.Lines.Count);

        // Each line must show the correct allocated quantity.
        var lineA = body.Lines.Single(l => l.SkuId == skuAId);
        var lineB = body.Lines.Single(l => l.SkuId == skuBId);

        Assert.Equal(10, lineA.AllocatedQty);
        Assert.Equal(0,  lineA.RemainingQty);
        Assert.Equal(15, lineB.AllocatedQty);
        Assert.Equal(0,  lineB.RemainingQty);

        // DB invariant for both SKUs.
        Assert.Equal(10, await GetOnHandAsync(skuAId));   // 20 - 10
        Assert.Equal(15, await GetOnHandAsync(skuBId));   // 30 - 15
        Assert.Equal(10, await GetTotalAllocatedAsync(skuAId));
        Assert.Equal(15, await GetTotalAllocatedAsync(skuBId));
    }

    // ── Test 4: Cancelled order — 409, no stock consumed ─────────────────────

    [Fact]
    public async Task Allocate_CancelledOrder_Returns409AndLeavesStockUntouched()
    {
        var skuId   = await CreateSkuAsync("FLOW-CANCEL-01", onHand: 10);
        var orderId = await CreateOrderAsync("ORD-CANCEL-01", skuId, requestedQty: 5);

        // Cancel the order first.
        var cancelResponse = await Client.PostAsync($"/api/v1/orders/{orderId}/cancel", null);
        cancelResponse.EnsureSuccessStatusCode();

        // Try to allocate a cancelled order.
        var (status, _) = await AllocateAsync(orderId);

        Assert.Equal(HttpStatusCode.Conflict, status);

        // Stock must be untouched — cancel did not restore anything (Phase 6 adds that).
        Assert.Equal(10, await GetOnHandAsync(skuId));
        Assert.Equal(0,  await GetTotalAllocatedAsync(skuId));
    }
}