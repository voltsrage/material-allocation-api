using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Dapper;
using MaterialAllocationApi.Tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MaterialAllocationApi.Tests.Helpers;

[Collection("Allocation")]
public abstract class AllocationTestBase : IAsyncLifetime
{
    protected readonly ApiFixture Fixture;
    protected readonly HttpClient Client;

    protected static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    protected AllocationTestBase(ApiFixture fixture)
    {
        Fixture = fixture;
        Client  = fixture.CreateClient();
    }

    // Runs before each test — clean slate every time.
    public async Task InitializeAsync(){
        AuthorizeAsAll();
        await Fixture.ResetDatabaseAsync();
    }
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

    protected async Task<OrderResponse> GetOrderAsync(Guid orderId)
    {
        var response = await Client.GetAsync($"/api/v1/orders/{orderId}");
        response.EnsureSuccessStatusCode();
        return await ReadAsync<OrderResponse>(response);
    }

    protected async Task AdjustSkuAsync(Guid skuId, int delta, string reason)
    {
        var response = await Client.PostAsJsonAsync($"/api/v1/skus/{skuId}/adjust", new { delta, reason });
        response.EnsureSuccessStatusCode();
    }

    protected static async Task<T> ReadAsync<T>(HttpResponseMessage response)
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

    // Sets the default role header for all subsequent requests on this client.
    protected void AuthorizeAs(params string[] roles)
    {
        Client.DefaultRequestHeaders.Remove(TestAuthHandler.RoleHeader);
        Client.DefaultRequestHeaders.Add(
            TestAuthHandler.RoleHeader, string.Join(",", roles)
        );
    }

    // Grants all roles — used by test setup methods that mix SKU, order, and allocation calls.
    protected void AuthorizeAsAll() =>
        AuthorizeAs("warehouse-ops", "sales-ops", "allocation-manager", "read-only");

    /// <summary>
    /// Creates a fresh <see cref="AllocationRunWorker"/> and invokes one processing cycle
    /// synchronously via reflection. The background worker is stripped from the test host
    /// (see <see cref="ApiFixture"/>) so tests control exactly when a run is processed.
    /// </summary>
    protected Task TriggerAllocationWorkerAsync() =>
        TriggerAllocationWorkerAsync(Fixture.Services);

    /// <summary>
    /// Same as <see cref="TriggerAllocationWorkerAsync()"/> but resolves services from
    /// <paramref name="services"/>. Use this when the test creates a custom
    /// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>
    /// (e.g. for fault injection) and needs the worker to use that factory's DI scope.
    /// </summary>
    protected async Task TriggerAllocationWorkerAsync(IServiceProvider services)
    {
        var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
        var logger       = services.GetRequiredService<ILogger<AllocationRunWorker>>();
        var config       = services.GetRequiredService<IConfiguration>();

        var worker = new AllocationRunWorker(scopeFactory, logger, config);

        using var scope = services.CreateScope();

        var method = typeof(AllocationRunWorker).GetMethod(
            "ProcessNextAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "ProcessNextAsync not found — was the method renamed?");

        await (Task)method.Invoke(worker, [scope, CancellationToken.None])!;
    }

    protected async Task<Guid> SubmitAllocationRunAsync()
    {
        var response = await Client.PostAsync("/api/v1/allocations/run", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var envelope = await ReadAsync<AllocationRunAcceptedResponse>(response);
        return envelope.RunId;
    }

    protected async Task<AllocationRunStatusResponse> PollRunUntilCompleteAsync(
        Guid runId, int maxAttempts = 20, int delayMs = 250)
    {
        for (var i = 0; i < maxAttempts; i++)
        {
            var response = await Client.GetAsync($"/api/v1/allocations/runs/{runId}");
            response.EnsureSuccessStatusCode();

            var run = await ReadAsync<AllocationRunStatusResponse>(response);

            if (run.Status is "completed" or "failed")
                return run;

            await Task.Delay(delayMs);
        }

        throw new TimeoutException(
            $"Allocation run {runId} did not complete within {maxAttempts * delayMs}ms.");
    }

    protected async Task<CustomerResponse> CreateCustomerAsync(
    string code = "CUST-A",
    CustomerTier tier = CustomerTier.Tier1)
    {
        var response = await Client.PostAsJsonAsync("/api/v1/customers",
            new { customerCode = code, name = code, tier = tier.ToString().ToLowerInvariant() });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<CustomerResponse>>();
        return body!.Data!;
    }

    protected async Task<ContractResponse> CreateContractAsync(
        Guid customerId, Guid skuId,
        int floorQty, int? ceilingQty = null,
        DateOnly? effectiveFrom = null)
    {
        var from = effectiveFrom ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var response = await Client.PostAsJsonAsync(
            $"/api/v1/customers/{customerId}/contracts",
            new { skuId, floorQty, ceilingQty, effectiveFrom = from });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<ContractResponse>>();
        return body!.Data!;
    }
}