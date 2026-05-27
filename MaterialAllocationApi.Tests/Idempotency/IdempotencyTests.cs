using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Dapper;
using MaterialAllocationApi.Tests.Fixtures;
using MaterialAllocationApi.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MaterialAllocationApi.Tests.Idempotency;

[Collection("Allocation")]
public class IdempotencyTests(ApiFixture fixture) : AllocationTestBase(fixture)
{
    // ── 5a — No key: request processes normally ───────────────────────────────
    // Call POST /orders/{id}/allocate without Idempotency-Key. Assert 200 and
    // that the allocation committed. Confirms opt-in behaviour — absent header
    // is a transparent pass-through; no idempotency_keys row is written.

    [Fact]
    public async Task Allocate_NoKey_ProcessesNormallyWithNoRecord()
    {
        var skuId   = await CreateSkuAsync("IDMP-NOKEY-01", onHand: 5);
        var orderId = await CreateOrderAsync("ORD-IDMP-NOKEY-01", skuId, requestedQty: 5);

        var (status, body) = await AllocateAsync(orderId);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("fully_allocated", body!.Status);
        Assert.Equal(0, await GetOnHandAsync(skuId));
        Assert.Equal(0, await CountIdempotencyRowsAsync());
    }

    // ── 5b — First request with key: processes and stores ─────────────────────
    // Call POST /orders/{id}/allocate with Idempotency-Key. Assert 200, no
    // replay header on the first call, and a complete row in idempotency_keys
    // with the captured response status and body.

    [Fact]
    public async Task Allocate_WithKey_FirstCall_ProcessesAndStoresCompleteRecord()
    {
        var skuId   = await CreateSkuAsync("IDMP-FIRST-01", onHand: 5);
        var orderId = await CreateOrderAsync("ORD-IDMP-FIRST-01", skuId, requestedQty: 5);

        var key = Guid.NewGuid().ToString();
        Client.DefaultRequestHeaders.Add("Idempotency-Key", key);

        var response = await Client.PostAsync($"/api/v1/orders/{orderId}/allocate", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("X-Idempotency-Replayed"),
            "X-Idempotency-Replayed must be absent on the first (real) call.");

        var row = await GetIdempotencyRowAsync(key);
        Assert.NotNull(row);
        Assert.Equal("complete", row.Status);
        Assert.Equal(200, row.ResponseStatus);
        Assert.False(string.IsNullOrWhiteSpace(row.ResponseBody));
    }

    // ── 5c — Second request with same key: replays stored response ────────────
    // Same key, same endpoint — the replay must return the cached body byte-for-
    // byte, set X-Idempotency-Replayed: true, and not move any inventory.

    [Fact]
    public async Task Allocate_WithKey_SecondCall_ReplaysWithoutReallocating()
    {
        var skuId   = await CreateSkuAsync("IDMP-REPLAY-01", onHand: 5);
        var orderId = await CreateOrderAsync("ORD-IDMP-REPLAY-01", skuId, requestedQty: 5);

        var key = Guid.NewGuid().ToString();
        Client.DefaultRequestHeaders.Add("Idempotency-Key", key);

        // First call: real allocation.
        var first = await Client.PostAsync($"/api/v1/orders/{orderId}/allocate", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody    = await first.Content.ReadAsStringAsync();
        var onHandAfter1 = await GetOnHandAsync(skuId);

        // Second call: replay.
        var second = await Client.PostAsync($"/api/v1/orders/{orderId}/allocate", null);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True(second.Headers.Contains("X-Idempotency-Replayed"),
            "X-Idempotency-Replayed must be set on a replayed response.");
        Assert.Equal("true",
            second.Headers.GetValues("X-Idempotency-Replayed").First());

        // Replayed body must be byte-for-byte identical to the original response.
        var secondBody = await second.Content.ReadAsStringAsync();
        Assert.Equal(firstBody, secondBody);

        // No inventory must have moved on the second call.
        Assert.Equal(onHandAfter1, await GetOnHandAsync(skuId));
    }

    // ── 5d — Replay does not re-execute business logic ────────────────────────
    // After full allocation with key K, a second call with key K must replay the
    // 200 fully_allocated response — not re-run the service and return a 409.

    [Fact]
    public async Task Allocate_WithKey_SecondCall_ReturnsReplayedBodyNotFreshConflict()
    {
        var skuId   = await CreateSkuAsync("IDMP-NOREEXEC-01", onHand: 5);
        var orderId = await CreateOrderAsync("ORD-IDMP-NOREEXEC-01", skuId, requestedQty: 5);

        var key = Guid.NewGuid().ToString();
        Client.DefaultRequestHeaders.Add("Idempotency-Key", key);

        var first = await Client.PostAsync($"/api/v1/orders/{orderId}/allocate", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Without idempotency a second allocate on a fully-allocated order returns
        // 409 ORDER_FULLY_ALLOCATED. With replay it must be 200.
        var second = await Client.PostAsync($"/api/v1/orders/{orderId}/allocate", null);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True(second.Headers.Contains("X-Idempotency-Replayed"));

        var body = await ReadAsync<AllocationResponse>(second);
        Assert.Equal("fully_allocated", body.Status);
    }

    // ── 5e — Key reuse on different endpoint returns 422 ─────────────────────
    // A key used for POST /orders cannot be reused for POST /skus. The middleware
    // returns 422 IDEMPOTENCY_KEY_MISMATCH on the second (mismatched) call.

    [Fact]
    public async Task KeyReusedOnDifferentEndpoint_Returns422KeyMismatch()
    {
        // Seed a SKU so the order creation line reference is valid.
        var skuId = await CreateSkuAsync("IDMP-MISMATCH-SKU", onHand: 5);

        var key = Guid.NewGuid().ToString();
        Client.DefaultRequestHeaders.Add("Idempotency-Key", key);

        // First use: POST /orders — key K is registered for this path.
        var orderResponse = await Client.PostAsJsonAsync("/api/v1/orders", new
        {
            referenceCode = "ORD-IDMP-MISMATCH-01",
            priority      = "standard",
            lines         = new[] { new { skuId, requestedQty = 1 } },
        });
        Assert.Equal(HttpStatusCode.Created, orderResponse.StatusCode);

        // Second use: key K reused for a different path.
        var skuResponse = await Client.PostAsJsonAsync("/api/v1/skus",
            new { skuCode = "IDMP-MISMATCH-SKU2", description = "mismatch test", initialOnHand = 0 });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, skuResponse.StatusCode);

        var envelope = await skuResponse.Content
            .ReadFromJsonAsync<ApiResponse<object>>(JsonOptions);
        Assert.Equal("IDEMPOTENCY_KEY_MISMATCH", envelope!.Error!.Code);
    }

    // ── 5f — In-flight key returns 409 ────────────────────────────────────────
    // Insert a processing record directly (simulating a concurrent request that
    // has claimed the key but not yet completed). A new request with the same key
    // must return 409 IDEMPOTENCY_IN_FLIGHT without reaching the service layer.

    [Fact]
    public async Task InFlightKey_Returns409IdempotencyInFlight()
    {
        var skuId   = await CreateSkuAsync("IDMP-INFLIGHT-01", onHand: 5);
        var orderId = await CreateOrderAsync("ORD-IDMP-INFLIGHT-01", skuId, requestedQty: 3);

        var key  = Guid.NewGuid().ToString();
        var path = $"/api/v1/orders/{orderId}/allocate";

        await InsertProcessingRecordAsync(key, path, "POST");

        Client.DefaultRequestHeaders.Add("Idempotency-Key", key);
        var response = await Client.PostAsync(path, null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var envelope = await response.Content
            .ReadFromJsonAsync<ApiResponse<object>>(JsonOptions);
        Assert.Equal("IDEMPOTENCY_IN_FLIGHT", envelope!.Error!.Code);
    }

    // ── 5g — Stuck processing record is cleaned up ────────────────────────────
    // A processing record older than StuckProcessingAgeMinutes is deleted by
    // InvokeCleanupAsync. The same key then processes normally on the next call.

    [Fact]
    public async Task StuckProcessingRecord_IsRemovedByCleanup_ThenKeyProcessesNormally()
    {
        var skuId   = await CreateSkuAsync("IDMP-STUCK-01", onHand: 5);
        var orderId = await CreateOrderAsync("ORD-IDMP-STUCK-01", skuId, requestedQty: 5);

        var key  = Guid.NewGuid().ToString();
        var path = $"/api/v1/orders/{orderId}/allocate";

        // Insert a stuck processing record — created well before the 5-minute threshold.
        await InsertProcessingRecordAsync(key, path, "POST",
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-10));

        Assert.NotNull(await GetIdempotencyRowAsync(key));

        await InvokeCleanupAsync();

        Assert.Null(await GetIdempotencyRowAsync(key));   // cleaned up

        // After cleanup the key is treated as new — allocation runs normally.
        Client.DefaultRequestHeaders.Add("Idempotency-Key", key);
        var response = await Client.PostAsync(path, null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("X-Idempotency-Replayed"));
        Assert.Equal(0, await GetOnHandAsync(skuId));   // stock actually moved
    }

    // ── 5h — Expired key treated as new ──────────────────────────────────────
    // After the cleanup job removes an expired complete record the same key is
    // treated as new: the operation runs fresh and X-Idempotency-Replayed is absent.

    [Fact]
    public async Task ExpiredCompleteRecord_IsRemovedByCleanup_ThenKeyTreatedAsNew()
    {
        var skuId   = await CreateSkuAsync("IDMP-EXPIRED-01", onHand: 5);
        var orderId = await CreateOrderAsync("ORD-IDMP-EXPIRED-01", skuId, requestedQty: 5);

        var key  = Guid.NewGuid().ToString();
        var path = $"/api/v1/orders/{orderId}/allocate";

        // Insert an already-expired complete record.
        await InsertCompleteRecordAsync(key, path, "POST",
            responseStatus: 200,
            responseBody:   """{"expired":true}""",
            expiresAt:      DateTimeOffset.UtcNow.AddHours(-1));

        var before = await GetIdempotencyRowAsync(key);
        Assert.NotNull(before);
        Assert.Equal("complete", before.Status);

        await InvokeCleanupAsync();

        Assert.Null(await GetIdempotencyRowAsync(key));   // cleaned up

        // Same key should now trigger a fresh allocation, not a replay.
        Client.DefaultRequestHeaders.Add("Idempotency-Key", key);
        var response = await Client.PostAsync(path, null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("X-Idempotency-Replayed"),
            "X-Idempotency-Replayed must be absent — key was expired and cleaned up.");
        Assert.Equal(0, await GetOnHandAsync(skuId));   // allocation actually ran
    }

    // ── 5i — 5xx response is not stored ──────────────────────────────────────
    // When the service layer throws an unhandled exception the idempotency record
    // must stay 'processing' (never promoted to 'complete'). The cleanup job is
    // the recovery mechanism for stranded processing records.

    [Fact]
    public async Task FiveHundredResponse_LeavesRecordAsProcessingNotComplete()
    {
        var skuId   = await CreateSkuAsync("IDMP-5XX-SKU", onHand: 5);
        var orderId = await CreateOrderAsync("ORD-IDMP-5XX-01", skuId, requestedQty: 3);

        // Replace IAllocationService with a stub that always throws so the
        // controller produces a 500, exercising the exception re-throw path in
        // IdempotencyMiddleware without the record being marked complete.
        await using var throwingFactory = Fixture.WithWebHostBuilder(wb =>
            wb.ConfigureServices(services =>
            {
                var descriptor = services.Single(s => s.ServiceType == typeof(IAllocationService));
                services.Remove(descriptor);
                services.AddScoped<IAllocationService, ThrowingAllocationService>();
            }));

        var throwingClient = throwingFactory.CreateClient();
        throwingClient.DefaultRequestHeaders.Add(
            TestAuthHandler.RoleHeader, "allocation-manager");

        var key = Guid.NewGuid().ToString();
        throwingClient.DefaultRequestHeaders.Add("Idempotency-Key", key);

        var response = await throwingClient.PostAsync(
            $"/api/v1/orders/{orderId}/allocate", null);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        // The record must be 'processing', not 'complete'.
        var row = await GetIdempotencyRowAsync(key);
        Assert.NotNull(row);
        Assert.Equal("processing", row.Status);
        Assert.Null(row.ResponseStatus);
    }

    // ── 5j — SKU adjust: retry does not double delta ──────────────────────────
    // A second POST /skus/{id}/adjust with the same Idempotency-Key must replay
    // the stored response without applying the stock delta a second time.

    [Fact]
    public async Task SkuAdjust_WithKey_RetryDoesNotDoubleDelta()
    {
        var skuId = await CreateSkuAsync("IDMP-ADJ-01", onHand: 10);
        var key   = Guid.NewGuid().ToString();
        Client.DefaultRequestHeaders.Add("Idempotency-Key", key);

        // First adjust: on_hand 10 → 15.
        var first = await Client.PostAsJsonAsync($"/api/v1/skus/{skuId}/adjust",
            new { delta = 5, reason = "idempotency test" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(15, await GetOnHandAsync(skuId));

        // Retry with same key: must replay, not apply delta again.
        var second = await Client.PostAsJsonAsync($"/api/v1/skus/{skuId}/adjust",
            new { delta = 5, reason = "idempotency test" });

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True(second.Headers.Contains("X-Idempotency-Replayed"));
        Assert.Equal(15, await GetOnHandAsync(skuId));   // delta not applied twice
    }

    // ── 5k — No regression ────────────────────────────────────────────────────
    // A complete order lifecycle — create SKU, create order, allocate, cancel —
    // without any Idempotency-Key header must behave exactly as before. The
    // middleware pass-through path is transparent when the header is absent.

    [Fact]
    public async Task FullWorkflow_NoIdempotencyKey_WorksNormally()
    {
        var skuId   = await CreateSkuAsync("IDMP-REGR-SKU", onHand: 20);
        var orderId = await CreateOrderAsync("ORD-IDMP-REGR-01", skuId, requestedQty: 10);

        var (allocStatus, allocBody) = await AllocateAsync(orderId);
        Assert.Equal(HttpStatusCode.OK, allocStatus);
        Assert.Equal("fully_allocated", allocBody!.Status);
        Assert.Equal(10, await GetOnHandAsync(skuId));

        var cancel = await Client.PostAsync($"/api/v1/orders/{orderId}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal(20, await GetOnHandAsync(skuId));   // on_hand restored after cancel

        // Middleware pass-through produces no idempotency rows.
        Assert.Equal(0, await CountIdempotencyRowsAsync());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<IdempotencyRow?> GetIdempotencyRowAsync(string key)
    {
        await using var conn = new NpgsqlConnection(Fixture.GetConnectionString());
        await conn.OpenAsync();
        return await conn.QuerySingleOrDefaultAsync<IdempotencyRow>(
            """
            SELECT
                idempotency_key  AS IdempotencyKey,
                status           AS Status,
                response_status  AS ResponseStatus,
                response_body::text AS ResponseBody,
                created_at       AS CreatedAt,
                expires_at       AS ExpiresAt
            FROM idempotency_keys
            WHERE idempotency_key = @key
            """,
            new { key });
    }

    private async Task<int> CountIdempotencyRowsAsync()
    {
        await using var conn = new NpgsqlConnection(Fixture.GetConnectionString());
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM idempotency_keys");
    }

    private async Task InsertProcessingRecordAsync(
        string key, string path, string method,
        DateTimeOffset? createdAt = null)
    {
        await using var conn = new NpgsqlConnection(Fixture.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            """
            INSERT INTO idempotency_keys
                (id, idempotency_key, request_path, request_method, status, created_at, expires_at)
            VALUES
                (gen_random_uuid(), @key, @path, @method, 'processing',
                 @createdAt, @expiresAt)
            """,
            new
            {
                key,
                path,
                method,
                createdAt = createdAt ?? DateTimeOffset.UtcNow,
                expiresAt = DateTimeOffset.UtcNow.AddHours(24),
            });
    }

    private async Task InsertCompleteRecordAsync(
        string key, string path, string method,
        int responseStatus, string responseBody,
        DateTimeOffset? expiresAt = null)
    {
        await using var conn = new NpgsqlConnection(Fixture.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            """
            INSERT INTO idempotency_keys
                (id, idempotency_key, request_path, request_method, status,
                 response_status, response_body, created_at, expires_at)
            VALUES
                (gen_random_uuid(), @key, @path, @method, 'complete',
                 @responseStatus, @responseBody::jsonb, @createdAt, @expiresAt)
            """,
            new
            {
                key,
                path,
                method,
                responseStatus,
                responseBody,
                createdAt = DateTimeOffset.UtcNow,
                expiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddHours(24),
            });
    }

    /// <summary>
    /// Instantiates a fresh <see cref="IdempotencyCleanupJob"/> and invokes its
    /// private <c>CleanupAsync</c> method directly — the same logic path the
    /// hosted background service runs, without waiting for the timer interval.
    /// </summary>
    private async Task InvokeCleanupAsync()
    {
        var logger   = Fixture.Services.GetRequiredService<ILogger<IdempotencyCleanupJob>>();
        var settings = Fixture.Services.GetRequiredService<IOptions<IdempotencySettings>>();

        var job = new IdempotencyCleanupJob(Fixture.Services, logger, settings);

        using var scope = Fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AllocationDbContext>();

        var method = typeof(IdempotencyCleanupJob)
            .GetMethod("CleanupAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "CleanupAsync not found — was the method renamed?");

        await (Task)method.Invoke(job, [db, CancellationToken.None])!;
    }

    // ── Private stub ──────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal <see cref="IAllocationService"/> used by test 5i. Only
    /// <c>AllocateAsync</c> is ever invoked by the allocate endpoint; the
    /// throw triggers the 5xx / processing-stays code path in
    /// <see cref="IdempotencyMiddleware"/>.
    /// </summary>
    private sealed class ThrowingAllocationService : IAllocationService
    {
        public Task<AllocationResponse> AllocateAsync(Guid orderId, CancellationToken ct = default)
            => throw new InvalidOperationException("Simulated service failure for idempotency 5i test.");

        public Task<AvailabilityResponse> GetAvailabilityAsync(Guid skuId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<AllocationRunResponse> RunPriorityAllocationAsync(CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<AllocationEventResponse>> GetEventsAsync(Guid orderId, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    // Npgsql returns timestamptz as DateTimeOffset by default; record fields must match.
    // Use DateTime (not DateTimeOffset): AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true)
    // makes Npgsql return DateTime for timestamptz columns. Dapper matches constructor
    // parameters by type, so DateTimeOffset would cause a "no matching constructor" error.
    private record IdempotencyRow(
        string IdempotencyKey,
        string Status,
        int? ResponseStatus,
        string? ResponseBody,
        DateTime CreatedAt,
        DateTime ExpiresAt
    );
}
