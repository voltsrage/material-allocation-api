using System.Net;
using System.Net.Http.Json;
using Dapper;
using MaterialAllocationApi.Tests.Fixtures;
using MaterialAllocationApi.Tests.Helpers;
using Npgsql;

namespace MaterialAllocationApi.Tests.Auth;

[Collection("Allocation")]
public class RbacTests(ApiFixture fixture) : AllocationTestBase(fixture)
{
    // ── 13a — Token endpoint is anonymous ────────────────────────────────────
    // POST /api/v1/auth/token carries [AllowAnonymous], so it must return 200
    // even when no X-Test-Role header is present. An unknown role must return 422.

    [Fact]
    public async Task GetToken_ValidRole_Returns200WithToken()
    {
        Client.DefaultRequestHeaders.Remove(TestAuthHandler.RoleHeader);

        var response = await Client.PostAsJsonAsync("/api/v1/auth/token",
            new { role = "warehouse-ops" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync<TokenResponse>(response);
        Assert.False(string.IsNullOrWhiteSpace(body.Token));
    }

    [Fact]
    public async Task GetToken_UnknownRole_Returns422()
    {
        Client.DefaultRequestHeaders.Remove(TestAuthHandler.RoleHeader);

        var response = await Client.PostAsJsonAsync("/api/v1/auth/token",
            new { role = "super-admin" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // ── 13b — Unauthenticated callers receive 401 ─────────────────────────────
    // The test auth handler returns NoResult() when the X-Test-Role header is
    // absent, which causes the framework to challenge with 401 on any [Authorize]
    // endpoint — read or write.

    [Fact]
    public async Task UnauthenticatedRequest_ReadEndpoint_Returns401()
    {
        Client.DefaultRequestHeaders.Remove(TestAuthHandler.RoleHeader);

        var response = await Client.GetAsync("/api/v1/skus");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UnauthenticatedRequest_WriteEndpoint_Returns401()
    {
        Client.DefaultRequestHeaders.Remove(TestAuthHandler.RoleHeader);

        var response = await Client.PostAsJsonAsync("/api/v1/skus",
            new { skuCode = "NOAUTH-01", description = "x", initialOnHand = 0 });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── 13c — Wrong role returns 403 ──────────────────────────────────────────
    // The [Authorize(Roles = "...")] check fires in the ASP.NET Core middleware
    // stack before any service logic executes. A valid-but-wrong role yields 403.

    [Fact]
    public async Task SalesOps_CreateSku_Returns403()
    {
        AuthorizeAs("sales-ops");

        var response = await Client.PostAsJsonAsync("/api/v1/skus",
            new { skuCode = "RBAC-SALES-SKU", description = "x", initialOnHand = 0 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SalesOps_AdjustSku_Returns403()
    {
        AuthorizeAsAll();
        var skuId = await CreateSkuAsync("RBAC-SALES-ADJ", onHand: 5);

        AuthorizeAs("sales-ops");
        var response = await Client.PostAsJsonAsync($"/api/v1/skus/{skuId}/adjust",
            new { delta = 1, reason = "should be blocked" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task WarehouseOps_CreateOrder_Returns403()
    {
        AuthorizeAs("warehouse-ops");

        // The [Authorize(Roles = "sales-ops")] check fires before the service
        // validates the SKU id, so 403 is returned regardless of the payload.
        var response = await Client.PostAsJsonAsync("/api/v1/orders",
            new
            {
                referenceCode = "ORD-RBAC-WH-BLOCK",
                priority      = "standard",
                lines         = new[] { new { skuId = Guid.NewGuid(), requestedQty = 1 } },
            });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task WarehouseOps_CancelOrder_Returns403()
    {
        AuthorizeAsAll();
        var skuId   = await CreateSkuAsync("RBAC-WH-CANCEL", onHand: 5);
        var orderId = await CreateOrderAsync("ORD-RBAC-WH-CANCEL", skuId, requestedQty: 3);

        AuthorizeAs("warehouse-ops");
        var response = await Client.PostAsync($"/api/v1/orders/{orderId}/cancel", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SalesOps_AllocateOrder_Returns403()
    {
        AuthorizeAsAll();
        var skuId   = await CreateSkuAsync("RBAC-SALES-ALLOC", onHand: 5);
        var orderId = await CreateOrderAsync("ORD-RBAC-SALES-ALLOC", skuId, requestedQty: 3);

        AuthorizeAs("sales-ops");
        var response = await Client.PostAsync($"/api/v1/orders/{orderId}/allocate", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SalesOps_ReserveOrder_Returns403()
    {
        AuthorizeAsAll();
        var skuId   = await CreateSkuAsync("RBAC-SALES-RES", onHand: 5);
        var orderId = await CreateOrderAsync("ORD-RBAC-SALES-RES", skuId, requestedQty: 3);

        AuthorizeAs("sales-ops");
        var response = await Client.PostAsJsonAsync(
            $"/api/v1/orders/{orderId}/reserve",
            new { ttlMinutes = 30 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SalesOps_ReleaseReservation_Returns403()
    {
        AuthorizeAsAll();
        var skuId   = await CreateSkuAsync("RBAC-SALES-REL", onHand: 5);
        var orderId = await CreateOrderAsync("ORD-RBAC-SALES-REL", skuId, requestedQty: 3);
        await Client.PostAsJsonAsync($"/api/v1/orders/{orderId}/reserve",
            new { ttlMinutes = 30 });

        var reservationId = await GetReservationIdForOrderAsync(orderId);

        AuthorizeAs("sales-ops");
        var response = await Client.PostAsync(
            $"/api/v1/reservations/{reservationId}/release", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task WarehouseOps_RunAllocation_Returns403()
    {
        AuthorizeAs("warehouse-ops");

        var response = await Client.PostAsync("/api/v1/allocations/run", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ReadOnly_CreateSku_Returns403()
    {
        AuthorizeAs("read-only");

        var response = await Client.PostAsJsonAsync("/api/v1/skus",
            new { skuCode = "RBAC-RO-SKU", description = "x", initialOnHand = 0 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ReadOnly_CreateOrder_Returns403()
    {
        AuthorizeAs("read-only");

        var response = await Client.PostAsJsonAsync("/api/v1/orders",
            new
            {
                referenceCode = "ORD-RBAC-RO-BLOCK",
                priority      = "standard",
                lines         = new[] { new { skuId = Guid.NewGuid(), requestedQty = 1 } },
            });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── 13d — Each role can reach its permitted endpoints ─────────────────────
    // Positive assertions: verify the happy-path status code for every
    // role-restricted action with a minimal but valid request.

    [Fact]
    public async Task WarehouseOps_CreateAndAdjustSku_Succeeds()
    {
        AuthorizeAs("warehouse-ops");

        var createResponse = await Client.PostAsJsonAsync("/api/v1/skus",
            new { skuCode = "RBAC-WH-OK", description = "RBAC test", initialOnHand = 10 });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var sku = await ReadAsync<SkuResponse>(createResponse);

        var adjustResponse = await Client.PostAsJsonAsync($"/api/v1/skus/{sku.Id}/adjust",
            new { delta = 5, reason = "RBAC test restock" });
        Assert.Equal(HttpStatusCode.OK, adjustResponse.StatusCode);
    }

    [Fact]
    public async Task SalesOps_CreateAndCancelOrder_Succeeds()
    {
        // Create the SKU with warehouse-ops; the test focus is sales-ops order actions.
        AuthorizeAsAll();
        var skuId = await CreateSkuAsync("RBAC-SALES-OK", onHand: 10);

        AuthorizeAs("sales-ops");

        var createResponse = await Client.PostAsJsonAsync("/api/v1/orders",
            new
            {
                referenceCode = "ORD-RBAC-SALES-OK",
                priority      = "standard",
                lines         = new[] { new { skuId, requestedQty = 5 } },
            });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var order = await ReadAsync<OrderResponse>(createResponse);

        var cancelResponse = await Client.PostAsync(
            $"/api/v1/orders/{order.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancelResponse.StatusCode);
    }

    [Fact]
    public async Task AllocationManager_AllocateReserveRelease_Succeeds()
    {
        // Seed data with all roles; the test focus is allocation-manager actions.
        AuthorizeAsAll();
        var skuId    = await CreateSkuAsync("RBAC-AM-SKU", onHand: 20);
        var orderId1 = await CreateOrderAsync("ORD-RBAC-AM-ALLOC", skuId, requestedQty: 5);
        var orderId2 = await CreateOrderAsync("ORD-RBAC-AM-RES",   skuId, requestedQty: 3);

        AuthorizeAs("allocation-manager");

        // Allocate.
        var allocateResponse = await Client.PostAsync(
            $"/api/v1/orders/{orderId1}/allocate", null);
        Assert.Equal(HttpStatusCode.OK, allocateResponse.StatusCode);

        // Reserve.
        var reserveResponse = await Client.PostAsJsonAsync(
            $"/api/v1/orders/{orderId2}/reserve",
            new { ttlMinutes = 30 });
        Assert.Equal(HttpStatusCode.OK, reserveResponse.StatusCode);

        // Release.
        var reservationId = await GetReservationIdForOrderAsync(orderId2);
        var releaseResponse = await Client.PostAsync(
            $"/api/v1/reservations/{reservationId}/release", null);
        Assert.Equal(HttpStatusCode.NoContent, releaseResponse.StatusCode);
    }

    [Fact]
    public async Task AllocationManager_RunAllocation_Returns200()
    {
        AuthorizeAs("allocation-manager");

        var response = await Client.PostAsync("/api/v1/allocations/run", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ReadOnly_GetEndpoints_AllReturn200()
    {
        AuthorizeAsAll();
        var skuId   = await CreateSkuAsync("RBAC-RO-READ", onHand: 5);
        var orderId = await CreateOrderAsync("ORD-RBAC-RO-READ", skuId, requestedQty: 3);

        AuthorizeAs("read-only");

        Assert.Equal(HttpStatusCode.OK,
            (await Client.GetAsync($"/api/v1/skus/{skuId}")).StatusCode);

        Assert.Equal(HttpStatusCode.OK,
            (await Client.GetAsync("/api/v1/skus")).StatusCode);

        Assert.Equal(HttpStatusCode.OK,
            (await Client.GetAsync($"/api/v1/skus/{skuId}/availability")).StatusCode);

        Assert.Equal(HttpStatusCode.OK,
            (await Client.GetAsync($"/api/v1/orders/{orderId}")).StatusCode);

        Assert.Equal(HttpStatusCode.OK,
            (await Client.GetAsync("/api/v1/orders")).StatusCode);

        Assert.Equal(HttpStatusCode.OK,
            (await Client.GetAsync($"/api/v1/orders/{orderId}/events")).StatusCode);

        Assert.Equal(HttpStatusCode.OK,
            (await Client.GetAsync("/api/v1/rollup/sku-shortages")).StatusCode);
    }

    // ── Helper ────────────────────────────────────────────────────────────────

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
}
