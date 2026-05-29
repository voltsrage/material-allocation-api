using System.Net;
using System.Net.Http.Json;
using MaterialAllocationApi.Tests.Fixtures;
using MaterialAllocationApi.Tests.Helpers;

namespace MaterialAllocationApi.Tests;

[Collection("Allocation")]
public class LotTests(ApiFixture fixture) : AllocationTestBase(fixture)
{
    // ── 7a — Lot intake ───────────────────────────────────────────────────────

    [Fact]
    public async Task ReceiveLot_Returns201AndIncrementsOnHand()
    {
        var skuId = await CreateSkuAsync("LOT-SKU-01", onHand: 0);

        var response = await Client.PostAsJsonAsync($"/api/v1/skus/{skuId}/lots", new
        {
            lotCode  = "LOT-001",
            quantity = 100,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var lot = await ReadAsync<LotResponse>(response);
        Assert.NotEqual(Guid.Empty, lot.Id);
        Assert.Equal(skuId,     lot.SkuId);
        Assert.Equal("LOT-001", lot.LotCode);
        Assert.Equal(100,       lot.Quantity);
        Assert.Equal(100,       lot.AvailableQty);
        Assert.Equal("available", lot.Status);

        var onHand = await GetOnHandAsync(skuId);
        Assert.Equal(100, onHand);
    }

    [Fact]
    public async Task ReceiveTwoLots_AccumulatesOnHand()
    {
        var skuId = await CreateSkuAsync("LOT-SKU-02", onHand: 0);

        await ReceiveLotAsync(skuId, "LOT-A", 60);
        await ReceiveLotAsync(skuId, "LOT-B", 40);

        var onHand = await GetOnHandAsync(skuId);
        Assert.Equal(100, onHand);
    }

    [Fact]
    public async Task ReceiveLot_UnknownSku_Returns404()
    {
        var response = await Client.PostAsJsonAsync($"/api/v1/skus/{Guid.NewGuid()}/lots", new
        {
            lotCode  = "LOT-UNK-001",
            quantity = 50,
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ReceiveLot_DuplicateLotCode_Returns422()
    {
        var skuId = await CreateSkuAsync("LOT-SKU-03", onHand: 0);
        await ReceiveLotAsync(skuId, "LOT-DUP-001", 50);

        var response = await Client.PostAsJsonAsync($"/api/v1/skus/{skuId}/lots", new
        {
            lotCode  = "LOT-DUP-001",
            quantity = 10,
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task ReceiveLot_ZeroQuantity_Returns422()
    {
        var skuId = await CreateSkuAsync("LOT-SKU-04", onHand: 0);

        var response = await Client.PostAsJsonAsync($"/api/v1/skus/{skuId}/lots", new
        {
            lotCode  = "LOT-ZERO-001",
            quantity = 0,
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task ReceiveLot_NegativeQuantity_Returns422()
    {
        var skuId = await CreateSkuAsync("LOT-SKU-05", onHand: 0);

        var response = await Client.PostAsJsonAsync($"/api/v1/skus/{skuId}/lots", new
        {
            lotCode  = "LOT-NEG-001",
            quantity = -10,
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task ReceiveLot_ReceivedAtOmitted_DefaultsToNow()
    {
        var skuId = await CreateSkuAsync("LOT-SKU-06", onHand: 0);
        var before = DateTime.UtcNow.AddSeconds(-5);

        var response = await Client.PostAsJsonAsync($"/api/v1/skus/{skuId}/lots", new
        {
            lotCode  = "LOT-NODATE-001",
            quantity = 25,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var lot   = await ReadAsync<LotResponse>(response);
        var after = DateTime.UtcNow.AddSeconds(5);
        Assert.InRange(lot.ReceivedAt.ToUniversalTime(), before, after);
    }

    [Fact]
    public async Task ReceiveLot_ReceivedAtProvided_IsPreserved()
    {
        var skuId      = await CreateSkuAsync("LOT-SKU-07", onHand: 0);
        var receivedAt = new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);

        var response = await Client.PostAsJsonAsync($"/api/v1/skus/{skuId}/lots", new
        {
            lotCode    = "LOT-DATE-001",
            quantity   = 30,
            receivedAt = receivedAt,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var lot = await ReadAsync<LotResponse>(response);
        Assert.Equal(receivedAt.UtcDateTime, lot.ReceivedAt.ToUniversalTime());
    }

    // ── 7b — List and get ─────────────────────────────────────────────────────

    [Fact]
    public async Task ListLots_ReturnsPaginatedResultsOrderedByReceivedAtDesc()
    {
        var skuA = await CreateSkuAsync("LIST-SKU-A", onHand: 0);
        var skuB = await CreateSkuAsync("LIST-SKU-B", onHand: 0);

        var t0 = DateTimeOffset.UtcNow.AddDays(-3);
        var t1 = DateTimeOffset.UtcNow.AddDays(-2);
        var t2 = DateTimeOffset.UtcNow.AddDays(-1);

        await ReceiveLotAsync(skuA, "LIST-LOT-C", 10, t0);
        await ReceiveLotAsync(skuA, "LIST-LOT-B", 20, t1);
        await ReceiveLotAsync(skuA, "LIST-LOT-A", 30, t2);
        await ReceiveLotAsync(skuB, "LIST-LOT-OTHER", 5);

        var response = await Client.GetAsync($"/api/v1/skus/{skuA}/lots?pageSize=2");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = await ReadAsync<PagedResult<LotResponse>>(response);
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal("LIST-LOT-A", page.Items[0].LotCode);
        Assert.Equal("LIST-LOT-B", page.Items[1].LotCode);
    }

    [Fact]
    public async Task ListLots_FilteredByStatus_ReturnsMatchingLots()
    {
        var skuId = await CreateSkuAsync("FILT-SKU-01", onHand: 0);
        await ReceiveLotAsync(skuId, "FILT-LOT-A", 10);
        await ReceiveLotAsync(skuId, "FILT-LOT-B", 20);

        var availableResp = await Client.GetAsync($"/api/v1/skus/{skuId}/lots?status=available");
        Assert.Equal(HttpStatusCode.OK, availableResp.StatusCode);
        var availablePage = await ReadAsync<PagedResult<LotResponse>>(availableResp);
        Assert.Equal(2, availablePage.TotalCount);

        var quarantinedResp = await Client.GetAsync($"/api/v1/skus/{skuId}/lots?status=quarantined");
        Assert.Equal(HttpStatusCode.OK, quarantinedResp.StatusCode);
        var quarantinedPage = await ReadAsync<PagedResult<LotResponse>>(quarantinedResp);
        Assert.Equal(0, quarantinedPage.TotalCount);
    }

    [Fact]
    public async Task ListLots_UnknownSku_Returns404()
    {
        var response = await Client.GetAsync($"/api/v1/skus/{Guid.NewGuid()}/lots");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetLotById_ReturnsCorrectFields()
    {
        var skuId      = await CreateSkuAsync("GET-SKU-01", onHand: 0);
        var receivedAt = DateTimeOffset.UtcNow.AddDays(-1);
        var intake     = await ReceiveLotAsync(skuId, "GET-LOT-001", 75, receivedAt);

        var response = await Client.GetAsync($"/api/v1/lots/{intake.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var lot = await ReadAsync<LotResponse>(response);
        Assert.Equal(intake.Id,       lot.Id);
        Assert.Equal(skuId,           lot.SkuId);
        Assert.Equal("GET-LOT-001",   lot.LotCode);
        Assert.Equal(75,              lot.Quantity);
        Assert.Equal(75,              lot.AvailableQty);
        Assert.Equal("available",     lot.Status);
        Assert.Equal(intake.ReceivedAt, lot.ReceivedAt);
    }

    [Fact]
    public async Task GetLotById_UnknownId_Returns404()
    {
        var response = await Client.GetAsync($"/api/v1/lots/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── 8a — Quarantine ───────────────────────────────────────────────────────

    [Fact]
    public async Task QuarantineAvailableLot_DecrementsOnHand()
    {
        var skuId = await CreateSkuAsync("QUAR-SKU-01", onHand: 0);
        var lot   = await ReceiveLotAsync(skuId, "QUAR-LOT-001", 100);

        var response = await Client.PostAsJsonAsync($"/api/v1/lots/{lot.Id}/quarantine", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await ReadAsync<LotResponse>(response);
        Assert.Equal("quarantined", updated.Status);

        Assert.Equal(0, await GetOnHandAsync(skuId));
    }

    [Fact]
    public async Task QuarantineLot_WithNotes_Returns200()
    {
        var skuId = await CreateSkuAsync("QUAR-SKU-02", onHand: 0);
        var lot   = await ReceiveLotAsync(skuId, "QUAR-LOT-002", 50);

        var response = await Client.PostAsJsonAsync($"/api/v1/lots/{lot.Id}/quarantine",
            new { notes = "Particle contamination on wafer W03" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task QuarantineAlreadyQuarantinedLot_Returns409()
    {
        var skuId = await CreateSkuAsync("QUAR-SKU-03", onHand: 0);
        var lot   = await ReceiveLotAsync(skuId, "QUAR-LOT-003", 40);
        await QuarantineLotAsync(lot.Id);

        var response = await Client.PostAsJsonAsync($"/api/v1/lots/{lot.Id}/quarantine", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task QuarantineScrappedLot_Returns409()
    {
        var skuId = await CreateSkuAsync("QUAR-SKU-04", onHand: 0);
        var lot   = await ReceiveLotAsync(skuId, "QUAR-LOT-004", 30);
        await ScrapLotAsync(lot.Id);

        var response = await Client.PostAsJsonAsync($"/api/v1/lots/{lot.Id}/quarantine", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task QuarantineUnknownLot_Returns404()
    {
        var response = await Client.PostAsJsonAsync($"/api/v1/lots/{Guid.NewGuid()}/quarantine", new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── 8b — Release ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ReleaseLot_RestoresOnHandExactly()
    {
        var skuId = await CreateSkuAsync("REL-SKU-01", onHand: 0);
        var lot   = await ReceiveLotAsync(skuId, "REL-LOT-001", 80);
        await QuarantineLotAsync(lot.Id);
        Assert.Equal(0, await GetOnHandAsync(skuId));

        var response = await Client.PostAsJsonAsync($"/api/v1/lots/{lot.Id}/release", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await ReadAsync<LotResponse>(response);
        Assert.Equal("available", updated.Status);
        Assert.Equal(80, await GetOnHandAsync(skuId));
    }

    [Fact]
    public async Task ReleaseAvailableLot_Returns409()
    {
        var skuId = await CreateSkuAsync("REL-SKU-02", onHand: 0);
        var lot   = await ReceiveLotAsync(skuId, "REL-LOT-002", 20);

        var response = await Client.PostAsJsonAsync($"/api/v1/lots/{lot.Id}/release", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task ReleaseScrappedLot_Returns409()
    {
        var skuId = await CreateSkuAsync("REL-SKU-03", onHand: 0);
        var lot   = await ReceiveLotAsync(skuId, "REL-LOT-003", 25);
        await ScrapLotAsync(lot.Id);

        var response = await Client.PostAsJsonAsync($"/api/v1/lots/{lot.Id}/release", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ── 8c — Scrap ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ScrapAvailableLot_DecrementsOnHandAndZeroesAvailableQty()
    {
        var skuId = await CreateSkuAsync("SCRAP-SKU-01", onHand: 0);
        var lot   = await ReceiveLotAsync(skuId, "SCRAP-LOT-001", 60);

        var response = await Client.PostAsJsonAsync($"/api/v1/lots/{lot.Id}/scrap", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await ReadAsync<LotResponse>(response);
        Assert.Equal("scrapped", updated.Status);
        Assert.Equal(0, updated.AvailableQty);
        Assert.Equal(0, await GetOnHandAsync(skuId));
    }

    [Fact]
    public async Task ScrapQuarantinedLot_DoesNotFurtherDecrementOnHand()
    {
        var skuId = await CreateSkuAsync("SCRAP-SKU-02", onHand: 0);
        var lot   = await ReceiveLotAsync(skuId, "SCRAP-LOT-002", 50);
        await QuarantineLotAsync(lot.Id);
        Assert.Equal(0, await GetOnHandAsync(skuId));

        var response = await Client.PostAsJsonAsync($"/api/v1/lots/{lot.Id}/scrap", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await ReadAsync<LotResponse>(response);
        Assert.Equal("scrapped", updated.Status);
        Assert.Equal(0, updated.AvailableQty);
        Assert.Equal(0, await GetOnHandAsync(skuId));
    }

    [Fact]
    public async Task ScrapAlreadyScrappedLot_Returns409()
    {
        var skuId = await CreateSkuAsync("SCRAP-SKU-03", onHand: 0);
        var lot   = await ReceiveLotAsync(skuId, "SCRAP-LOT-003", 35);
        await ScrapLotAsync(lot.Id);

        var response = await Client.PostAsJsonAsync($"/api/v1/lots/{lot.Id}/scrap", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task ScrapLot_WithNotes_Returns200()
    {
        var skuId = await CreateSkuAsync("SCRAP-SKU-04", onHand: 0);
        var lot   = await ReceiveLotAsync(skuId, "SCRAP-LOT-004", 20);

        var response = await Client.PostAsJsonAsync($"/api/v1/lots/{lot.Id}/scrap",
            new { notes = "Physical damage during handling" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── 8d — Full lifecycle round-trip ────────────────────────────────────────

    [Fact]
    public async Task FullLifecycle_OnHandConsistentThroughQuarantineReleaseScrap()
    {
        var skuId = await CreateSkuAsync("LIFE-SKU-01", onHand: 0);
        var lotA  = await ReceiveLotAsync(skuId, "LIFE-LOT-A", 100);
        var lotB  = await ReceiveLotAsync(skuId, "LIFE-LOT-B", 50);
        Assert.Equal(150, await GetOnHandAsync(skuId));

        await QuarantineLotAsync(lotA.Id);
        Assert.Equal(50, await GetOnHandAsync(skuId));

        await ReleaseLotAsync(lotA.Id);
        Assert.Equal(150, await GetOnHandAsync(skuId));

        await ScrapLotAsync(lotB.Id);
        Assert.Equal(100, await GetOnHandAsync(skuId));
    }

    // ── 8e — Concurrency ─────────────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentQuarantine_OnlyOneSucceeds()
    {
        var skuId = await CreateSkuAsync("CONC-SKU-01", onHand: 0);
        var lot   = await ReceiveLotAsync(skuId, "CONC-LOT-001", 100);

        var t1 = Client.PostAsJsonAsync($"/api/v1/lots/{lot.Id}/quarantine", new { });
        var t2 = Client.PostAsJsonAsync($"/api/v1/lots/{lot.Id}/quarantine", new { });
        var results = await Task.WhenAll(t1, t2);

        var statuses = results.Select(r => (int)r.StatusCode).OrderBy(s => s).ToList();
        Assert.Equal([200, 409], statuses);
        Assert.Equal(0, await GetOnHandAsync(skuId));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<LotResponse> ReceiveLotAsync(
        Guid skuId, string lotCode, int quantity,
        DateTimeOffset? receivedAt = null)
    {
        var response = await Client.PostAsJsonAsync($"/api/v1/skus/{skuId}/lots", new
        {
            lotCode,
            quantity,
            receivedAt,
        });
        response.EnsureSuccessStatusCode();
        return await ReadAsync<LotResponse>(response);
    }

    private async Task<LotResponse> QuarantineLotAsync(Guid lotId, string? notes = null)
    {
        var response = await Client.PostAsJsonAsync($"/api/v1/lots/{lotId}/quarantine", new { notes });
        response.EnsureSuccessStatusCode();
        return await ReadAsync<LotResponse>(response);
    }

    private async Task<LotResponse> ReleaseLotAsync(Guid lotId, string? notes = null)
    {
        var response = await Client.PostAsJsonAsync($"/api/v1/lots/{lotId}/release", new { notes });
        response.EnsureSuccessStatusCode();
        return await ReadAsync<LotResponse>(response);
    }

    private async Task<LotResponse> ScrapLotAsync(Guid lotId, string? notes = null)
    {
        var response = await Client.PostAsJsonAsync($"/api/v1/lots/{lotId}/scrap", new { notes });
        response.EnsureSuccessStatusCode();
        return await ReadAsync<LotResponse>(response);
    }
}
