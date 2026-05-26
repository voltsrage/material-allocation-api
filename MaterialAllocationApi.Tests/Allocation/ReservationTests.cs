using System.Net;
using System.Net.Http.Json;
using Dapper;
using MaterialAllocationApi.Tests.Fixtures;
using MaterialAllocationApi.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace MaterialAllocationApi.Tests.Allocation;

[Collection("Allocation")]
public class ReservationTests(ApiFixture fixture) : AllocationTestBase(fixture)
{
    // ── Test 1: Basic reserve — reservation created, availability drops ────────

    [Fact]
    public async Task Reserve_OpenOrder_CreatesReservationAndReducesAvailability()
    {
        var skuId   = await CreateSkuAsync("RES-BASIC-01", onHand: 10);
        var orderId = await CreateOrderAsync("ORD-RES-BASIC-01", skuId, requestedQty: 6);

        var response = await Client.PostAsJsonAsync(
            $"/api/v1/orders/{orderId}/reserve",
            new { ttlMinutes = 30 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ReadAsync<ReservationResponse>(response);
        Assert.Single(body.Lines);
        Assert.Equal(skuId, body.Lines[0].SkuId);
        Assert.Equal(6, body.Lines[0].QuantityReserved);
        Assert.True(body.ExpiresAt > DateTimeOffset.UtcNow);

        // Availability must reflect the reservation.
        var availability = await GetAvailabilityAsync(skuId);
        Assert.Equal(10, availability.OnHand);
        Assert.Equal(6,  availability.Reserved);
        Assert.Equal(4,  availability.Available);   // 10 - 6
    }

    // ── Test 2: Reserve for Order A blocks allocation for Order B ─────────────

    [Fact]
    public async Task Reserve_OrderA_BlocksAllocationForOrderB()
    {
        // 5 units. Order A reserves all 5. Order B requests 5 — should get 0.
        var skuId    = await CreateSkuAsync("RES-BLOCK-01", onHand: 5);
        var orderAId = await CreateOrderAsync("ORD-RES-A-01", skuId, requestedQty: 5);
        var orderBId = await CreateOrderAsync("ORD-RES-B-01", skuId, requestedQty: 5);

        // Reserve for Order A.
        var reserveResponse = await Client.PostAsJsonAsync(
            $"/api/v1/orders/{orderAId}/reserve",
            new { ttlMinutes = 60 });
        Assert.Equal(HttpStatusCode.OK, reserveResponse.StatusCode);

        // Allocate Order B — should get 0 units because on_hand - reservedByOthers = 0.
        var (_, body) = await AllocateAsync(orderBId);
        Assert.Equal("open", body!.Status);

        var lineB = body.Lines.Single();
        Assert.Equal(0, lineB.AllocatedQty);
        Assert.Equal(5, lineB.RemainingQty);

        // Order A's own reservation does not block its own allocation.
        var (_, bodyA) = await AllocateAsync(orderAId);
        Assert.Equal("fully_allocated", bodyA!.Status);
        Assert.Equal(5, bodyA.Lines.Single().AllocatedQty);
    }

    // ── Test 3: Reserve self — own reservation does not block own allocation ───

    [Fact]
    public async Task Reserve_ThenAllocate_SameOrder_OwnReservationDoesNotBlock()
    {
        var skuId   = await CreateSkuAsync("RES-SELF-01", onHand: 8);
        var orderId = await CreateOrderAsync("ORD-RES-SELF-01", skuId, requestedQty: 8);

        // Reserve all 8 units for this order.
        var reserveResponse = await Client.PostAsJsonAsync(
            $"/api/v1/orders/{orderId}/reserve",
            new { ttlMinutes = 60 });
        Assert.Equal(HttpStatusCode.OK, reserveResponse.StatusCode);

        // Availability should be 0 (all 8 are reserved).
        var avail = await GetAvailabilityAsync(skuId);
        Assert.Equal(0, avail.Available);

        // Allocation for the same order should still succeed — it subtracts
        // reservations from OTHER orders only, so available = on_hand - 0 = 8.
        var (_, body) = await AllocateAsync(orderId);
        Assert.Equal("fully_allocated", body!.Status);
        Assert.Equal(8, body.Lines.Single().AllocatedQty);
    }

    // ── Test 4: Refresh — calling reserve again replaces TTL ──────────────────

    [Fact]
    public async Task Reserve_CalledTwice_ReplacesExistingReservation()
    {
        var skuId   = await CreateSkuAsync("RES-REFRESH-01", onHand: 10);
        var orderId = await CreateOrderAsync("ORD-RES-REFRESH-01", skuId, requestedQty: 5);

        await Client.PostAsJsonAsync($"/api/v1/orders/{orderId}/reserve", new { ttlMinutes = 1 });
        var second = await Client.PostAsJsonAsync($"/api/v1/orders/{orderId}/reserve", new { ttlMinutes = 60 });

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = await ReadAsync<ReservationResponse>(second);

        // Still exactly 5 reserved (not 10 — the first was deleted before creating the second).
        Assert.Equal(5, body.Lines.Single().QuantityReserved);

        var avail = await GetAvailabilityAsync(skuId);
        Assert.Equal(5, avail.Reserved);
    }

    // ── Test 5: Explicit release — availability restored ─────────────────────

    [Fact]
    public async Task Release_Reservation_RestoredToAvailability()
    {
        var skuId   = await CreateSkuAsync("RES-RELEASE-01", onHand: 7);
        var orderId = await CreateOrderAsync("ORD-RES-RELEASE-01", skuId, requestedQty: 7);

        var reserveResponse = await Client.PostAsJsonAsync(
            $"/api/v1/orders/{orderId}/reserve",
            new { ttlMinutes = 60 });
        var body = await ReadAsync<ReservationResponse>(reserveResponse);

        Assert.Equal(0, (await GetAvailabilityAsync(skuId)).Available);

        // Get the reservation ID from the database (response doesn't expose it directly).
        var reservationId = await GetReservationIdForOrderAsync(orderId);

        var releaseResponse = await Client.PostAsync(
            $"/api/v1/reservations/{reservationId}/release", null);
        Assert.Equal(HttpStatusCode.NoContent, releaseResponse.StatusCode);

        // Availability must be fully restored.
        var avail = await GetAvailabilityAsync(skuId);
        Assert.Equal(0, avail.Reserved);
        Assert.Equal(7, avail.Available);
    }

    // ── Test 6: Expiry — expired reservations restore availability ───────────

    [Fact]
    public async Task Expire_PastReservation_RestoredToAvailability()
    {
        var skuId   = await CreateSkuAsync("RES-EXPIRE-01", onHand: 4);
        var orderId = await CreateOrderAsync("ORD-RES-EXPIRE-01", skuId, requestedQty: 4);

        await Client.PostAsJsonAsync($"/api/v1/orders/{orderId}/reserve", new { ttlMinutes = 60 });
        Assert.Equal(0, (await GetAvailabilityAsync(skuId)).Available);

        // Back-date the reservation's expires_at to make it appear already expired.
        await BackdateReservationsAsync(orderId);

        // Run the expiry service directly (no need to wait 60 seconds for the timer).
        using var scope = Fixture.Services.CreateScope();
        var expirySvc = scope.ServiceProvider.GetRequiredService<IReservationService>();
        var deleted = await expirySvc.ExpireAsync();

        Assert.True(deleted >= 1, "Expected at least one reservation to be expired.");

        // Availability must be restored.
        var avail = await GetAvailabilityAsync(skuId);
        Assert.Equal(0, avail.Reserved);
        Assert.Equal(4, avail.Available);
    }

    // ── Test 7: Cancel order — reservations deleted atomically ────────────────

    [Fact]
    public async Task Cancel_OrderWithReservation_DeletesReservationAndRestoresAvailability()
    {
        var skuId   = await CreateSkuAsync("RES-CANCEL-01", onHand: 6);
        var orderId = await CreateOrderAsync("ORD-RES-CANCEL-01", skuId, requestedQty: 6);

        await Client.PostAsJsonAsync($"/api/v1/orders/{orderId}/reserve", new { ttlMinutes = 60 });
        Assert.Equal(0, (await GetAvailabilityAsync(skuId)).Available);

        // Cancel the order — must delete reservations in the same transaction.
        var cancelResponse = await Client.PostAsync($"/api/v1/orders/{orderId}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancelResponse.StatusCode);

        // Reservation gone, full availability restored.
        var avail = await GetAvailabilityAsync(skuId);
        Assert.Equal(0, avail.Reserved);
        Assert.Equal(6, avail.Available);
    }

    // ── DB helpers ────────────────────────────────────────────────────────────

    private async Task<AvailabilityResponse> GetAvailabilityAsync(Guid skuId)
    {
        var response = await Client.GetAsync($"/api/v1/skus/{skuId}/availability");
        response.EnsureSuccessStatusCode();
        return await ReadAsync<AvailabilityResponse>(response);
    }

    private async Task<Guid> GetReservationIdForOrderAsync(Guid orderId)
    {
        await using var conn = new NpgsqlConnection(Fixture.GetConnectionString());
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<Guid>(
            """
            SELECT r.id FROM reservations r
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