using Dapper;
using MaterialAllocationApi.Tests.Fixtures;
using MaterialAllocationApi.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Net.Http.Json;

namespace MaterialAllocationApi.Tests.Allocation;

[Collection("Allocation")]
public class AllocationAuditTests(ApiFixture fixture) : AllocationTestBase(fixture)
{
    // ── 5a — Events written on allocation ────────────────────────────────────
    // Allocate an order against SKUs with enough stock. Assert GET /orders/{id}/events
    // returns exactly one allocation_committed event per line that received units,
    // with the correct SkuId and Quantity.

    [Fact]
    public async Task Allocate_SufficientStock_WritesOneAllocationCommittedEventPerAllocatedLine()
    {
        var skuAId = await CreateSkuAsync("AUDIT-ALLOC-A", onHand: 10);
        var skuBId = await CreateSkuAsync("AUDIT-ALLOC-B", onHand: 20);

        var orderId = await CreateOrderMultiLineAsync(
            "ORD-AUDIT-ALLOC-01",
            [(skuAId, 7), (skuBId, 12)]);

        await AllocateAsync(orderId);

        var events = await GetOrderEventsAsync(orderId);

        // One event per line — both lines got units.
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal("allocation_committed", e.EventType));

        var evA = Assert.Single(events, e => e.SkuId == skuAId);
        Assert.Equal(7, evA.Quantity);

        var evB = Assert.Single(events, e => e.SkuId == skuBId);
        Assert.Equal(12, evB.Quantity);

        // All events carry a valid timestamp.
        Assert.All(events, e => Assert.True(e.OccurredAt > DateTime.MinValue));
    }

    // ── 5b — Events written on cancel-with-release ───────────────────────────
    // Allocate an order, then cancel it. Assert the event log contains the
    // allocation_committed events followed by allocation_released events, one
    // per previously allocated line, with matching quantities.

    [Fact]
    public async Task Cancel_AfterAllocation_WritesAllocationReleasedEventsAfterCommittedEvents()
    {
        var skuId   = await CreateSkuAsync("AUDIT-CANCEL-01", onHand: 8);
        var orderId = await CreateOrderAsync("ORD-AUDIT-CANCEL-01", skuId, requestedQty: 8);

        await AllocateAsync(orderId);

        var cancelResponse = await Client.PostAsync($"/api/v1/orders/{orderId}/cancel", null);
        cancelResponse.EnsureSuccessStatusCode();

        var events = await GetOrderEventsAsync(orderId);

        // Two events total: committed, then released.
        Assert.Equal(2, events.Count);

        Assert.Equal("allocation_committed", events[0].EventType);
        Assert.Equal(skuId, events[0].SkuId);
        Assert.Equal(8, events[0].Quantity);

        Assert.Equal("allocation_released", events[1].EventType);
        Assert.Equal(skuId, events[1].SkuId);
        Assert.Equal(8, events[1].Quantity);   // released qty matches what was allocated

        // Released event is chronologically at or after the committed event.
        Assert.True(events[1].OccurredAt >= events[0].OccurredAt);
    }

    // ── 5c — Events written on reserve ───────────────────────────────────────
    // Reserve an order. Assert one reservation_created event per line that
    // received a reservation, with Quantity matching the reserved amount.

    [Fact]
    public async Task Reserve_OpenOrder_WritesOneReservationCreatedEventPerLine()
    {
        var skuId   = await CreateSkuAsync("AUDIT-RES-01", onHand: 9);
        var orderId = await CreateOrderAsync("ORD-AUDIT-RES-01", skuId, requestedQty: 6);

        var reserveResponse = await Client.PostAsJsonAsync(
            $"/api/v1/orders/{orderId}/reserve",
            new { ttlMinutes = 30 });
        reserveResponse.EnsureSuccessStatusCode();

        var events = await GetOrderEventsAsync(orderId);

        Assert.Single(events);
        var ev = events[0];
        Assert.Equal("reservation_created", ev.EventType);
        Assert.Equal(skuId, ev.SkuId);
        Assert.Equal(6, ev.Quantity);
    }

    // ── 5d — Events written on explicit release ───────────────────────────────
    // Reserve an order, then call POST /reservations/{id}/release. Assert one
    // reservation_released event is appended to the order's history with the
    // correct quantity.

    [Fact]
    public async Task ReleaseReservation_AppendsReservationReleasedEvent()
    {
        var skuId   = await CreateSkuAsync("AUDIT-REL-01", onHand: 7);
        var orderId = await CreateOrderAsync("ORD-AUDIT-REL-01", skuId, requestedQty: 7);

        await Client.PostAsJsonAsync(
            $"/api/v1/orders/{orderId}/reserve",
            new { ttlMinutes = 60 });

        var reservationId = await GetReservationIdForOrderAsync(orderId);

        var releaseResponse = await Client.PostAsync(
            $"/api/v1/reservations/{reservationId}/release", null);
        releaseResponse.EnsureSuccessStatusCode();

        var events = await GetOrderEventsAsync(orderId);

        // reservation_created first, reservation_released second.
        Assert.Equal(2, events.Count);
        Assert.Equal("reservation_created",  events[0].EventType);
        Assert.Equal("reservation_released", events[1].EventType);
        Assert.Equal(skuId, events[1].SkuId);
        Assert.Equal(7,     events[1].Quantity);
        Assert.True(events[1].OccurredAt >= events[0].OccurredAt);
    }

    // ── 5e — Events written on expiry ─────────────────────────────────────────
    // Reserve an order, back-date the reservation so it appears expired, trigger
    // the expiry job, then assert one reservation_expired event per expired row.

    [Fact]
    public async Task ExpireReservation_WritesOneReservationExpiredEventPerExpiredRow()
    {
        var skuId   = await CreateSkuAsync("AUDIT-EXP-01", onHand: 5);
        var orderId = await CreateOrderAsync("ORD-AUDIT-EXP-01", skuId, requestedQty: 5);

        await Client.PostAsJsonAsync(
            $"/api/v1/orders/{orderId}/reserve",
            new { ttlMinutes = 60 });

        // Move the reservation's expires_at into the past so ExpireAsync picks it up.
        await BackdateReservationsAsync(orderId);

        using var scope = Fixture.Services.CreateScope();
        var expirySvc = scope.ServiceProvider.GetRequiredService<IReservationService>();
        var expired = await expirySvc.ExpireAsync();
        Assert.True(expired >= 1, "Expected at least one reservation to be expired.");

        var events = await GetOrderEventsAsync(orderId);

        // reservation_created (from reserve) + reservation_expired (from expiry job).
        Assert.Equal(2, events.Count);
        Assert.Equal("reservation_created", events[0].EventType);
        Assert.Equal("reservation_expired", events[1].EventType);
        Assert.Equal(skuId, events[1].SkuId);
        Assert.Equal(5,     events[1].Quantity);
    }

    // ── 5f — Atomicity: no event when no units are moved ─────────────────────
    // When every line resolves to canAllocate == 0 the transaction still commits
    // but no allocation_events rows are written. Verifies the `if (canAllocate > 0)`
    // guard inside the loop — a zero-unit run must produce no audit trail.

    [Fact]
    public async Task Allocate_ZeroStock_WritesNoEvents()
    {
        var skuId   = await CreateSkuAsync("AUDIT-ZERO-01", onHand: 0);
        var orderId = await CreateOrderAsync("ORD-AUDIT-ZERO-01", skuId, requestedQty: 5);

        var (_, body) = await AllocateAsync(orderId);
        Assert.Equal("open", body!.Status);   // sanity: order is still open, nothing moved

        // No inventory movement → no event row written, even though the
        // transaction committed and the order status was saved.
        var events = await GetOrderEventsAsync(orderId);
        Assert.Empty(events);
    }

    [Fact]
    public async Task Allocate_PartialStockAcrossLines_OnlyWritesEventsForLinesWithUnitsAllocated()
    {
        // SKUA has stock; SKUB has none. The allocation loop processes both lines
        // in a single transaction, but only the SKUA line satisfies canAllocate > 0.
        var skuAId = await CreateSkuAsync("AUDIT-PART-A", onHand: 4);
        var skuBId = await CreateSkuAsync("AUDIT-PART-B", onHand: 0);

        var orderId = await CreateOrderMultiLineAsync(
            "ORD-AUDIT-PART-01",
            [(skuAId, 4), (skuBId, 6)]);

        await AllocateAsync(orderId);

        var events = await GetOrderEventsAsync(orderId);

        // Only one event: SKUA received 4 units.
        // SKUB received 0 units → its event was never added to the change tracker,
        // so nothing for SKUB survives the commit.
        Assert.Single(events);
        Assert.Equal("allocation_committed", events[0].EventType);
        Assert.Equal(skuAId, events[0].SkuId);
        Assert.Equal(4, events[0].Quantity);
        Assert.DoesNotContain(events, e => e.SkuId == skuBId);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Call GET /orders/{id}/events and return the ordered event list.</summary>
    private async Task<IReadOnlyList<AllocationEventResponse>> GetOrderEventsAsync(Guid orderId)
    {
        var response = await Client.GetAsync($"/api/v1/orders/{orderId}/events");
        response.EnsureSuccessStatusCode();
        return await ReadAsync<IReadOnlyList<AllocationEventResponse>>(response);
    }

    private async Task<Guid> GetReservationIdForOrderAsync(Guid orderId)
    {
        await using var conn = new NpgsqlConnection(Fixture.GetConnectionString());
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<Guid>(
            """
            SELECT r.id
            FROM reservations r
            JOIN order_lines ol ON ol.id = r.order_line_id
            WHERE ol.order_id = @orderId
            LIMIT 1
            """,
            new { orderId });
    }

    private async Task BackdateReservationsAsync(Guid orderId)
    {
        await using var conn = new NpgsqlConnection(Fixture.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            """
            UPDATE reservations r
            SET expires_at = NOW() - INTERVAL '1 hour'
            FROM order_lines ol
            WHERE ol.id = r.order_line_id
              AND ol.order_id = @orderId
            """,
            new { orderId });
    }
}
