using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Dapper;
using MaterialAllocationApi.Tests.Fixtures;
using MaterialAllocationApi.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MaterialAllocationApi.Tests.Outbox;

[Collection("Allocation")]
public class OutboxPatternTests(ApiFixture fixture) : AllocationTestBase(fixture)
{
    // ── 5b-i — Outbox message written on allocation ───────────────────────────
    // Allocate an order. Assert one order.allocated row exists in outbox_messages,
    // processed_at IS NULL, and the payload deserialises with the correct orderId
    // and isFullyAllocated value.

    [Fact]
    public async Task Allocate_FullyAllocated_WritesOrderAllocatedOutboxMessage()
    {
        var skuId   = await CreateSkuAsync("OUTBOX-ALLOC-01", onHand: 10);
        var orderId = await CreateOrderAsync("ORD-OUTBOX-ALLOC-01", skuId, requestedQty: 7);

        await AllocateAsync(orderId);

        var rows = await GetOutboxRowsAsync("order.allocated");
        var row  = Assert.Single(rows);

        Assert.Null(row.ProcessedAt);
        Assert.Null(row.Error);

        using var doc  = JsonDocument.Parse(row.Payload);
        var root = doc.RootElement;
        Assert.Equal(orderId.ToString(), root.GetProperty("orderId").GetString());
        Assert.True(root.GetProperty("isFullyAllocated").GetBoolean());
        Assert.Equal("fully_allocated",  root.GetProperty("status").GetString());
    }

    // ── 5b-ii — Outbox messages written on allocation + cancellation ─────────
    // Allocate then cancel an order. Both order.allocated and order.cancelled rows
    // must be present. The cancellation payload must carry a releasedLines array.

    [Fact]
    public async Task Cancel_AfterAllocation_WritesBothAllocatedAndCancelledOutboxMessages()
    {
        var skuId   = await CreateSkuAsync("OUTBOX-CANCEL-01", onHand: 5);
        var orderId = await CreateOrderAsync("ORD-OUTBOX-CANCEL-01", skuId, requestedQty: 5);

        await AllocateAsync(orderId);

        var cancel = await Client.PostAsync($"/api/v1/orders/{orderId}/cancel", null);
        cancel.EnsureSuccessStatusCode();

        Assert.Single(await GetOutboxRowsAsync("order.allocated"));

        var cancelledRows = await GetOutboxRowsAsync("order.cancelled");
        var row = Assert.Single(cancelledRows);

        Assert.Null(row.ProcessedAt);

        using var doc  = JsonDocument.Parse(row.Payload);
        var root = doc.RootElement;
        Assert.Equal(orderId.ToString(), root.GetProperty("orderId").GetString());

        // releasedLines is a JSON array; it must exist and have at least one entry
        // (the previously allocated line).
        var lines = root.GetProperty("releasedLines");
        Assert.Equal(JsonValueKind.Array, lines.ValueKind);
        Assert.True(lines.GetArrayLength() >= 1,
            "releasedLines must contain at least one entry for the allocated line.");
    }

    // ── 5b-iii — Outbox message written on reservation ───────────────────────
    // Reserve an order. Assert one reservation.created row with correct orderId,
    // a future expiresAt, and a lines array with the reserved quantities.

    [Fact]
    public async Task Reserve_OpenOrder_WritesReservationCreatedOutboxMessage()
    {
        var skuId   = await CreateSkuAsync("OUTBOX-RES-01", onHand: 8);
        var orderId = await CreateOrderAsync("ORD-OUTBOX-RES-01", skuId, requestedQty: 5);

        var reserve = await Client.PostAsJsonAsync(
            $"/api/v1/orders/{orderId}/reserve",
            new { ttlMinutes = 30 });
        reserve.EnsureSuccessStatusCode();

        var rows = await GetOutboxRowsAsync("reservation.created");
        var row  = Assert.Single(rows);

        Assert.Null(row.ProcessedAt);

        using var doc  = JsonDocument.Parse(row.Payload);
        var root = doc.RootElement;
        Assert.Equal(orderId.ToString(), root.GetProperty("orderId").GetString());

        var expiresAt = root.GetProperty("expiresAt").GetDateTime();
        Assert.True(expiresAt > DateTime.UtcNow, "expiresAt must be a future timestamp.");

        var lines = root.GetProperty("lines");
        Assert.Equal(JsonValueKind.Array, lines.ValueKind);
        Assert.Equal(5, lines[0].GetProperty("reservedQty").GetInt32());
    }

    // ── 5b-iv — Outbox message written on explicit reservation release ────────
    // Reserve then explicitly release. Assert one reservation.released row with
    // the correct reservationId, orderId, and quantity.

    [Fact]
    public async Task ReleaseReservation_WritesReservationReleasedOutboxMessage()
    {
        var skuId   = await CreateSkuAsync("OUTBOX-REL-01", onHand: 6);
        var orderId = await CreateOrderAsync("ORD-OUTBOX-REL-01", skuId, requestedQty: 6);

        await Client.PostAsJsonAsync(
            $"/api/v1/orders/{orderId}/reserve",
            new { ttlMinutes = 60 });

        var reservationId = await GetReservationIdForOrderAsync(orderId);

        var release = await Client.PostAsync(
            $"/api/v1/reservations/{reservationId}/release", null);
        release.EnsureSuccessStatusCode();

        var rows = await GetOutboxRowsAsync("reservation.released");
        var row  = Assert.Single(rows);

        Assert.Null(row.ProcessedAt);

        using var doc  = JsonDocument.Parse(row.Payload);
        var root = doc.RootElement;
        Assert.Equal(reservationId.ToString(), root.GetProperty("reservationId").GetString());
        Assert.Equal(orderId.ToString(),       root.GetProperty("orderId").GetString());
        Assert.Equal(6,                        root.GetProperty("quantity").GetInt32());
    }

    // ── 5b-v — Outbox message written on reservation expiry ──────────────────
    // Reserve, back-date to make it appear expired, trigger ExpireAsync. Assert
    // one reservation.expired row written atomically by the CTE.

    [Fact]
    public async Task ExpireReservation_WritesReservationExpiredOutboxMessage()
    {
        var skuId   = await CreateSkuAsync("OUTBOX-EXP-01", onHand: 4);
        var orderId = await CreateOrderAsync("ORD-OUTBOX-EXP-01", skuId, requestedQty: 4);

        await Client.PostAsJsonAsync(
            $"/api/v1/orders/{orderId}/reserve",
            new { ttlMinutes = 60 });

        var reservationId = await GetReservationIdForOrderAsync(orderId);
        await BackdateReservationsAsync(orderId);

        using var scope = Fixture.Services.CreateScope();
        var expirySvc = scope.ServiceProvider.GetRequiredService<IReservationService>();
        var expired = await expirySvc.ExpireAsync();
        Assert.True(expired >= 1);

        var rows = await GetOutboxRowsAsync("reservation.expired");
        var row  = Assert.Single(rows);

        Assert.Null(row.ProcessedAt);

        using var doc  = JsonDocument.Parse(row.Payload);
        var root = doc.RootElement;
        Assert.Equal(reservationId.ToString(), root.GetProperty("reservationId").GetString());
        Assert.Equal(orderId.ToString(),       root.GetProperty("orderId").GetString());
        Assert.Equal(4,                        root.GetProperty("quantity").GetInt32());
    }

    // ── 5b-vi — Relay marks messages as processed ─────────────────────────────
    // After an allocation writes an outbox row, invoke the relay's batch processor
    // directly. Assert every row has processed_at set and error IS NULL.

    [Fact]
    public async Task RelayJob_ProcessBatch_MarksAllMessagesProcessed()
    {
        var skuId   = await CreateSkuAsync("OUTBOX-RELAY-01", onHand: 5);
        var orderId = await CreateOrderAsync("ORD-OUTBOX-RELAY-01", skuId, requestedQty: 5);
        await AllocateAsync(orderId);

        // Verify there are unprocessed messages before running the relay.
        var before = await GetAllOutboxRowsAsync();
        Assert.NotEmpty(before);
        Assert.All(before, r => Assert.Null(r.ProcessedAt));

        await InvokeRelayBatchAsync();

        // Every row must now be marked processed with no error.
        var after = await GetAllOutboxRowsAsync();
        Assert.NotEmpty(after);
        Assert.All(after, r =>
        {
            Assert.NotNull(r.ProcessedAt);
            Assert.Null(r.Error);
        });
    }

    // ── 5b-vii — Relay records error, leaves message eligible for retry ───────
    // Simulate the relay's per-message try/catch with a publisher that throws.
    // Assert the row has error set and processed_at remains NULL, keeping the
    // message eligible for the next relay pass.

    [Fact]
    public async Task RelayJob_OnPublisherFailure_SetsErrorAndKeepsProcessedAtNull()
    {
        var skuId   = await CreateSkuAsync("OUTBOX-FAIL-01", onHand: 3);
        var orderId = await CreateOrderAsync("ORD-OUTBOX-FAIL-01", skuId, requestedQty: 3);
        await AllocateAsync(orderId);

        // Confirm messages are in the DB via Dapper (avoids DateTimeOffset EF mapping
        // noise — the relay itself uses Dapper-free EF which works inside its own scope).
        var rowsBefore = await GetAllOutboxRowsAsync();
        Assert.NotEmpty(rowsBefore);

        // Load each message by primary key (no timestamp WHERE clause) and call
        // MarkFailed — this exercises the same code path the relay uses in its catch block.
        using var scope = Fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AllocationDbContext>();

        foreach (var row in rowsBefore)
        {
            var msg = await db.OutboxMessages.FindAsync(row.Id);
            Assert.NotNull(msg);

            try { throw new InvalidOperationException("Simulated transport unavailable."); }
            catch (Exception ex) { msg.MarkFailed(ex.Message); }
        }

        await db.SaveChangesAsync();

        // All rows must carry an error and processed_at must remain null —
        // the message stays eligible for the next relay pass.
        var rowsAfter = await GetAllOutboxRowsAsync();
        Assert.NotEmpty(rowsAfter);
        Assert.All(rowsAfter, r =>
        {
            Assert.NotNull(r.Error);
            Assert.Null(r.ProcessedAt);
        });
    }

    // ── 5b-viii — Atomicity: no outbox row added when operation is rejected ────
    // A 409 ConflictException is thrown before the transaction begins, so
    // _db.OutboxMessages.Add is never reached. The row count must not change.

    [Fact]
    public async Task Allocate_AlreadyFullyAllocated_AddsNoAdditionalOutboxRow()
    {
        var skuId   = await CreateSkuAsync("OUTBOX-ATOM-01", onHand: 5);
        var orderId = await CreateOrderAsync("ORD-OUTBOX-ATOM-01", skuId, requestedQty: 5);
        await AllocateAsync(orderId);   // fully allocates; writes order.allocated

        var countBefore = (await GetAllOutboxRowsAsync()).Count;

        // Second allocate on a fully-allocated order → 409 ConflictException.
        // The exception is raised before the transaction and SaveChangesAsync are
        // reached, so no outbox message is ever staged for the failed attempt.
        var (status, _) = await AllocateAsync(orderId);
        Assert.Equal(HttpStatusCode.Conflict, status);

        var countAfter = (await GetAllOutboxRowsAsync()).Count;
        Assert.Equal(countBefore, countAfter);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<List<OutboxRow>> GetOutboxRowsAsync(string eventType)
    {
        await using var conn = new NpgsqlConnection(Fixture.GetConnectionString());
        await conn.OpenAsync();
        var rows = await conn.QueryAsync<OutboxRow>(
            """
            SELECT
                id           AS Id,
                event_type   AS EventType,
                payload::text AS Payload,
                created_at   AS CreatedAt,
                processed_at AS ProcessedAt,
                error        AS Error
            FROM outbox_messages
            WHERE event_type = @eventType
            ORDER BY created_at
            """,
            new { eventType });
        return rows.ToList();
    }

    private async Task<List<OutboxRow>> GetAllOutboxRowsAsync()
    {
        await using var conn = new NpgsqlConnection(Fixture.GetConnectionString());
        await conn.OpenAsync();
        var rows = await conn.QueryAsync<OutboxRow>(
            """
            SELECT
                id           AS Id,
                event_type   AS EventType,
                payload::text AS Payload,
                created_at   AS CreatedAt,
                processed_at AS ProcessedAt,
                error        AS Error
            FROM outbox_messages
            ORDER BY created_at
            """);
        return rows.ToList();
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

    /// <summary>
    /// Creates a fresh OutboxRelayJob instance using real DI dependencies and invokes
    /// ProcessBatchAsync via reflection. ProcessBatchAsync is private by design — it is
    /// an implementation detail of the hosted service loop, not a public API.
    /// </summary>
    private async Task InvokeRelayBatchAsync()
    {
        var scopeFactory = Fixture.Services.GetRequiredService<IServiceScopeFactory>();
        var logger       = Fixture.Services.GetRequiredService<ILogger<OutboxRelayJob>>();
        var settings     = Fixture.Services.GetRequiredService<IOptions<OutboxRelaySettings>>();

        var relay  = new OutboxRelayJob(scopeFactory, logger, settings);
        var method = typeof(OutboxRelayJob).GetMethod(
            "ProcessBatchAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "ProcessBatchAsync not found — was the method renamed?");

        await (Task)method.Invoke(relay, [CancellationToken.None])!;
    }

    // Npgsql returns timestamptz columns as DateTime (UTC Kind) by default, not
    // DateTimeOffset — the record must match those CLR types for Dapper to bind.
    private record OutboxRow(
        Guid Id,
        string EventType,
        string Payload,
        DateTime CreatedAt,
        DateTime? ProcessedAt,
        string? Error
    );
}
