using System.Net;
using System.Net.Http.Json;
using MaterialAllocationApi.Tests.Fixtures;
using MaterialAllocationApi.Tests.Helpers;

namespace MaterialAllocationApi.Tests.Allocation;

[Collection("Allocation")]
public class CancelTests(ApiFixture fixture) : AllocationTestBase(fixture)
{
    // ── Test 1: Cancel open order — no on_hand change ─────────────────────────

    [Fact]
    public async Task Cancel_OpenOrder_NoInventoryChange()
    {
        // Arrange: order created but never allocated.
        var skuId   = await CreateSkuAsync("CANCEL-OPEN-01", onHand: 10);
        var orderId = await CreateOrderAsync("ORD-CANCEL-OPEN-01", skuId, requestedQty: 5);

        // Act.
        var response = await Client.PostAsync($"/api/v1/orders/{orderId}/cancel", null);

        // Assert.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ReadAsync<OrderResponse>(response);
        Assert.Equal("cancelled", body.Status);

        // on_hand must be unchanged — cancel of an un-allocated order releases nothing.
        Assert.Equal(10, await GetOnHandAsync(skuId));
        Assert.Equal(0,  await GetTotalAllocatedAsync(skuId));
    }

    // ── Test 2: Cancel partially allocated order — partial restore ────────────

    [Fact]
    public async Task Cancel_PartiallyAllocatedOrder_RestoresOnHandByAllocatedAmount()
    {
        // Arrange: 5 units on hand, order requests 10 (will be partially allocated).
        var skuId   = await CreateSkuAsync("CANCEL-PARTIAL-01", onHand: 5);
        var orderId = await CreateOrderAsync("ORD-CANCEL-PARTIAL-01", skuId, requestedQty: 10);

        // Partially allocate: allocates 5, leaving the order in partially_allocated status.
        await AllocateAsync(orderId);
        Assert.Equal(0, await GetOnHandAsync(skuId));           // all 5 consumed
        Assert.Equal(5, await GetTotalAllocatedAsync(skuId));   // 5 held

        // Act.
        var response = await Client.PostAsync($"/api/v1/orders/{orderId}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ReadAsync<OrderResponse>(response);
        Assert.Equal("cancelled", body.Status);

        // on_hand must be restored by the 5 allocated units.
        Assert.Equal(5, await GetOnHandAsync(skuId));
        Assert.Equal(0, await GetTotalAllocatedAsync(skuId));

        // order_lines.allocated_qty must be zeroed.
        Assert.Equal(0, await GetAllocatedForOrderAsync(orderId, skuId));
    }

    // ── Test 3: Cancel fully allocated order — full restore ───────────────────

    [Fact]
    public async Task Cancel_FullyAllocatedOrder_RestoresEntireOnHand()
    {
        var skuId   = await CreateSkuAsync("CANCEL-FULL-01", onHand: 8);
        var orderId = await CreateOrderAsync("ORD-CANCEL-FULL-01", skuId, requestedQty: 8);

        await AllocateAsync(orderId);
        Assert.Equal(0, await GetOnHandAsync(skuId));
        Assert.Equal(8, await GetTotalAllocatedAsync(skuId));

        var response = await Client.PostAsync($"/api/v1/orders/{orderId}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(8, await GetOnHandAsync(skuId));    // fully restored
        Assert.Equal(0, await GetTotalAllocatedAsync(skuId));
        Assert.Equal(0, await GetAllocatedForOrderAsync(orderId, skuId));
    }

    // ── Test 4: Double cancel — 409 with ORDER_ALREADY_CANCELLED code ─────────

    [Fact]
    public async Task Cancel_AlreadyCancelledOrder_Returns409()
    {
        var skuId   = await CreateSkuAsync("CANCEL-DOUBLE-01", onHand: 5);
        var orderId = await CreateOrderAsync("ORD-CANCEL-DOUBLE-01", skuId, requestedQty: 3);

        // First cancel — succeeds.
        var first = await Client.PostAsync($"/api/v1/orders/{orderId}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Second cancel — must be 409.
        var second = await Client.PostAsync($"/api/v1/orders/{orderId}/cancel", null);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var envelope = await second.Content
            .ReadFromJsonAsync<ApiResponse<object>>(JsonOptions);
        Assert.Equal("ORDER_ALREADY_CANCELLED", envelope!.Error!.Code);
    }

    // ── Test 5: Cancel releases units for reallocation — conservation proof ────

    [Fact]
    public async Task Cancel_ReleasesUnits_AllowsSubsequentFullAllocation()
    {
        // Arrange: 1 unit. Order A allocated it. Order B is waiting (open, 0 allocated).
        var skuId    = await CreateSkuAsync("CANCEL-RELEASE-01", onHand: 1);
        var orderAId = await CreateOrderAsync("ORD-CANCEL-A-01", skuId, requestedQty: 1);
        var orderBId = await CreateOrderAsync("ORD-CANCEL-B-01", skuId, requestedQty: 1);

        await AllocateAsync(orderAId);
        Assert.Equal(0, await GetOnHandAsync(skuId));   // unit is held by Order A

        // Attempting to allocate Order B now gets 0 units (no stock).
        var (_, zeroBody) = await AllocateAsync(orderBId);
        Assert.Equal("open", zeroBody!.Status);
        Assert.Equal(0, await GetAllocatedForOrderAsync(orderBId, skuId));

        // Act: cancel Order A, releasing the 1 unit back to inventory.
        var cancelResponse = await Client.PostAsync($"/api/v1/orders/{orderAId}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancelResponse.StatusCode);

        Assert.Equal(1, await GetOnHandAsync(skuId));   // unit is back
        Assert.Equal(0, await GetTotalAllocatedAsync(skuId)); // no order holds it now

        // Now Order B can fully allocate the released unit.
        var (_, fullBody) = await AllocateAsync(orderBId);
        Assert.Equal("fully_allocated", fullBody!.Status);
        Assert.True(fullBody.IsFullyAllocated);

        // Conservation: on_hand + total allocated = 1 (the original quantity).
        var onHand     = await GetOnHandAsync(skuId);
        var totalAlloc = await GetTotalAllocatedAsync(skuId);
        Assert.Equal(1, onHand + totalAlloc);
    }

    // ── Test 6: Cancel multi-line order — all lines released atomically ────────

    [Fact]
    public async Task Cancel_MultiLineAllocatedOrder_ReleasesAllLinesAtomically()
    {
        // Two SKUs. Allocate both. Cancel the order. Both on_hand values must be restored.
        var skuAId = await CreateSkuAsync("CANCEL-MULTI-A", onHand: 10);
        var skuBId = await CreateSkuAsync("CANCEL-MULTI-B", onHand: 20);

        var orderId = await CreateOrderMultiLineAsync(
            "ORD-CANCEL-MULTI-01",
            [(skuAId, 6), (skuBId, 14)]);

        await AllocateAsync(orderId);
        Assert.Equal(4,  await GetOnHandAsync(skuAId));   // 10 - 6
        Assert.Equal(6,  await GetOnHandAsync(skuBId));   // 20 - 14
        Assert.Equal(6,  await GetTotalAllocatedAsync(skuAId));
        Assert.Equal(14, await GetTotalAllocatedAsync(skuBId));

        // Act.
        var response = await Client.PostAsync($"/api/v1/orders/{orderId}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Both SKUs must be fully restored.
        Assert.Equal(10, await GetOnHandAsync(skuAId));
        Assert.Equal(20, await GetOnHandAsync(skuBId));
        Assert.Equal(0,  await GetTotalAllocatedAsync(skuAId));
        Assert.Equal(0,  await GetTotalAllocatedAsync(skuBId));

        // Both lines zeroed.
        Assert.Equal(0, await GetAllocatedForOrderAsync(orderId, skuAId));
        Assert.Equal(0, await GetAllocatedForOrderAsync(orderId, skuBId));
    }
}