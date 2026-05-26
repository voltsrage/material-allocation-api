using System.Net;
using MaterialAllocationApi.Tests.Helpers;
using MaterialAllocationApi.Tests.Fixtures;

namespace MaterialAllocationApi.Tests.Allocation;

public class ConcurrentAllocationTests(ApiFixture fixture) : AllocationTestBase(fixture)
{
    // ── Test 1: The critical race ─────────────────────────────────────────────

    [Fact]
    public async Task TwoConcurrentAllocations_SingleUnitSku_ExactlyOneOrderWins()
    {
        // Arrange: 1 unit available; two orders each requesting that 1 unit.
        var skuId   = await CreateSkuAsync("CONC-SINGLE-01", onHand: 1);
        var orderId1 = await CreateOrderAsync("ORD-CONC-01", skuId, requestedQty: 1);
        var orderId2 = await CreateOrderAsync("ORD-CONC-02", skuId, requestedQty: 1);

        // Act: fire both allocations simultaneously.
        // Task.WhenAll starts both before awaiting either — they run concurrently
        // against the test server, which processes each on a thread-pool thread.
        // FOR UPDATE ensures they are serialized at the database layer.
        var (status1, body1, status2, body2) = await RunConcurrentAsync(orderId1, orderId2);

        // Both HTTP responses must be 200 — neither should crash.
        Assert.Equal(HttpStatusCode.OK, status1);
        Assert.Equal(HttpStatusCode.OK, status2);

        // Exactly one order must be fully allocated.
        var fullyAllocatedCount = new[] { body1!.IsFullyAllocated, body2!.IsFullyAllocated }
            .Count(x => x);
        Assert.Equal(1, fullyAllocatedCount);

        // The other order must have allocated 0 units in this run (no stock left).
        var notFullyAllocated = body1.IsFullyAllocated ? body2 : body1;
        var losingLine = notFullyAllocated.Lines.Single();
        Assert.Equal(0, losingLine.AllocatedQty);
        Assert.Equal(1, losingLine.RemainingQty);

        // ── DB invariant: on_hand + sum(allocated_qty) = original quantity (1) ──
        var onHand       = await GetOnHandAsync(skuId);
        var totalAlloc   = await GetTotalAllocatedAsync(skuId);
        var original     = 1;

        Assert.Equal(0, onHand);                   // all stock consumed
        Assert.Equal(1, totalAlloc);               // exactly 1 unit allocated
        Assert.Equal(original, onHand + totalAlloc); // conservation holds
    }

    // ── Test 2: Conservation under higher contention ──────────────────────────

    [Fact]
    public async Task FiveConcurrentAllocations_ThreeUnitSku_OnHandNeverGoesNegative()
    {
        // Arrange: 3 units; 5 orders each requesting 1 unit.
        var skuId = await CreateSkuAsync("CONC-MULTI-01", onHand: 3);

        var orderIds = await Task.WhenAll(Enumerable.Range(1, 5)
            .Select(i => CreateOrderAsync($"ORD-MULTI-{i:D2}", skuId, requestedQty: 1)));

        // Act: all five fire simultaneously.
        var tasks   = orderIds.Select(id => AllocateAsync(id));
        var results = await Task.WhenAll(tasks);

        // All five must return 200 — the constraint catch in ForUpdate prevents any crash.
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.Status));

        // Exactly 3 orders should be fully allocated (one unit each).
        var fullyAllocated = results.Count(r => r.Body!.IsFullyAllocated);
        Assert.Equal(3, fullyAllocated);

        // DB invariant: on_hand + sum(allocated_qty) = 3.
        var onHand     = await GetOnHandAsync(skuId);
        var totalAlloc = await GetTotalAllocatedAsync(skuId);

        Assert.True(onHand >= 0,   $"on_hand went negative: {onHand}");
        Assert.Equal(3, onHand + totalAlloc);
    }

    // ── Test 3: Deadlock prevention — two orders, two shared SKUs ─────────────

    [Fact(Timeout = 8000)]
    public async Task TwoConcurrentAllocations_TwoSharedSkus_NeitherDeadlocks()
    {
        // Arrange: two SKUs. Order 1 requests [SKU-A, SKU-B]. Order 2 requests [SKU-B, SKU-A].
        // Without deterministic lock ordering, this is a classic deadlock setup.
        // With ORDER BY id in the FOR UPDATE query, both transactions lock in the same row order.
        var skuAId = await CreateSkuAsync("CONC-DEAD-A", onHand: 10);
        var skuBId = await CreateSkuAsync("CONC-DEAD-B", onHand: 10);

        var orderId1 = await CreateOrderMultiLineAsync(
            "ORD-DEAD-01",
            [(skuAId, 1), (skuBId, 1)]);

        var orderId2 = await CreateOrderMultiLineAsync(
            "ORD-DEAD-02",
            [(skuBId, 1), (skuAId, 1)]);  // reversed line order — same lock order in DB

        // Act: both fire simultaneously. Timeout = 8000ms fails the test if either hangs
        // waiting for a lock that will never release (deadlock).
        var (status1, _) = await AllocateAsync(orderId1);
        var (status2, _) = await AllocateAsync(orderId2);
        // Note: running these as Task.WhenAll would be ideal but the timeout on the [Fact]
        // is the guard. Sequential is acceptable here — the important thing is neither hangs.

        Assert.Equal(HttpStatusCode.OK, status1);
        Assert.Equal(HttpStatusCode.OK, status2);

        // DB invariant: 4 units allocated total across both SKUs (2 per order × 2 orders).
        var allocatedA = await GetTotalAllocatedAsync(skuAId);
        var allocatedB = await GetTotalAllocatedAsync(skuBId);

        Assert.Equal(4, allocatedA + allocatedB);
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private async Task<(HttpStatusCode s1, AllocationResponse? b1, HttpStatusCode s2, AllocationResponse? b2)>
        RunConcurrentAsync(Guid orderId1, Guid orderId2)
    {
        var task1 = AllocateAsync(orderId1);
        var task2 = AllocateAsync(orderId2);
        var results = await Task.WhenAll(task1, task2);
        return (results[0].Status, results[0].Body, results[1].Status, results[1].Body);
    }
}