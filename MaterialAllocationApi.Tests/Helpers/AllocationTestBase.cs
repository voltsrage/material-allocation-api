using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using MaterialAllocationApi.Tests.Fixtures;
using Npgsql;

namespace MaterialAllocationApi.Tests.Helpers;

[Collection("Allocation")]
public abstract class AllocationTestBase : IAsyncLifetime
{
    protected readonly ApiFixture Fixture;
    protected readonly HttpClient Client;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    protected AllocationTestBase(ApiFixture fixture)
    {
        Fixture = fixture;
        Client  = fixture.CreateClient();
    }

    // Runs before each test — clean slate every time.
    public Task InitializeAsync() => Fixture.ResetDatabaseAsync();
    public Task DisposeAsync()    => Task.CompletedTask;

    // ── HTTP helpers ──────────────────────────────────────────────────────────

    protected async Task<Guid> CreateSkuAsync(string code, int onHand)
    {
        var response = await Client.PostAsJsonAsync("/api/v1/skus", new
        {
            skuCode       = code,
            description   = $"Test — {code}",
            initialOnHand = onHand,
        });
        response.EnsureSuccessStatusCode();

        var envelope = await ReadAsync<SkuResponse>(response);
        return envelope.Id;
    }

    protected async Task<Guid> CreateOrderAsync(
        string referenceCode, Guid skuId, int requestedQty, string priority = "standard")
    {
        var response = await Client.PostAsJsonAsync("/api/v1/orders", new
        {
            referenceCode,
            priority,
            lines = new[] { new { skuId, requestedQty } },
        });
        response.EnsureSuccessStatusCode();

        var envelope = await ReadAsync<OrderResponse>(response);
        return envelope.Id;
    }

    protected async Task<Guid> CreateOrderMultiLineAsync(
        string referenceCode,
        IEnumerable<(Guid SkuId, int Qty)> lines,
        string priority = "standard")
    {
        var response = await Client.PostAsJsonAsync("/api/v1/orders", new
        {
            referenceCode,
            priority,
            lines = lines.Select(l => new { skuId = l.SkuId, requestedQty = l.Qty }),
        });
        response.EnsureSuccessStatusCode();

        var envelope = await ReadAsync<OrderResponse>(response);
        return envelope.Id;
    }

    protected async Task<(HttpStatusCode Status, AllocationResponse? Body)> AllocateAsync(Guid orderId)
    {
        var response = await Client.PostAsync($"/api/v1/orders/{orderId}/allocate", null);

        if (!response.IsSuccessStatusCode)
            return (response.StatusCode, null);

        var envelope = await ReadAsync<AllocationResponse>(response);
        return (response.StatusCode, envelope);
    }

    protected async Task AdjustSkuAsync(Guid skuId, int delta, string reason)
    {
        var response = await Client.PostAsJsonAsync($"/api/v1/skus/{skuId}/adjust", new { delta, reason });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var envelope = await response.Content
            .ReadFromJsonAsync<ApiResponse<T>>(JsonOptions);

        return envelope!.Data!;
    }

    // ── Database assertion helpers ────────────────────────────────────────────
    // Use Npgsql + Dapper directly — bypasses EF's change tracker so concurrent
    // test writes are visible immediately after the allocating transaction commits.

    protected async Task<int> GetOnHandAsync(Guid skuId)
    {
        await using var conn = new NpgsqlConnection(Fixture.GetConnectionString());
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT on_hand FROM skus WHERE id = @id", new { id = skuId });
    }

    protected async Task<int> GetTotalAllocatedAsync(Guid skuId)
    {
        await using var conn = new NpgsqlConnection(Fixture.GetConnectionString());
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COALESCE(SUM(ol.allocated_qty), 0) FROM order_lines ol WHERE ol.sku_id = @id",
            new { id = skuId });
    }

    protected async Task<int> GetAllocatedForOrderAsync(Guid orderId, Guid skuId)
    {
        await using var conn = new NpgsqlConnection(Fixture.GetConnectionString());
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<int>(
            """
            SELECT COALESCE(allocated_qty, 0)
            FROM order_lines
            WHERE order_id = @orderId AND sku_id = @skuId
            """,
            new { orderId, skuId });
    }
}