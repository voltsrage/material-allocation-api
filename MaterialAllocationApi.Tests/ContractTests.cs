using System.Net;
using System.Net.Http.Json;
using MaterialAllocationApi.Tests.Fixtures;
using MaterialAllocationApi.Tests.Helpers;

namespace MaterialAllocationApi.Tests;

[Collection("Allocation")]
public class ContractTests(ApiFixture fixture) : AllocationTestBase(fixture)
{
    // ── 17a — Create Contract ─────────────────────────────────────────────────

    [Fact]
    public async Task CreateContract_Returns201WithAllFields()
    {
        var customer = await CreateCustomerAsync("CONTRACT-CORP", CustomerTier.Tier1);
        var skuId    = await CreateSkuAsync("CONTRACT-SKU-01", onHand: 50);
        var from     = DateOnly.FromDateTime(DateTime.UtcNow);

        var contract = await CreateContractAsync(customer.Id, skuId,
            floorQty: 5, ceilingQty: 20, effectiveFrom: from);

        Assert.NotEqual(Guid.Empty, contract.Id);
        Assert.Equal(customer.Id, contract.CustomerId);
        Assert.Equal(skuId,       contract.SkuId);
        Assert.Equal(5,           contract.FloorQty);
        Assert.Equal(20,          contract.CeilingQty);
        Assert.Equal(from,        contract.EffectiveFrom);
        Assert.Null(contract.EffectiveTo);
    }

    [Fact]
    public async Task CreateContract_RequiresSalesOpsRole_Returns403ForOtherRoles()
    {
        var customer = await CreateCustomerAsync("RBAC-CORP", CustomerTier.Tier2);
        var skuId    = await CreateSkuAsync("RBAC-SKU-01", onHand: 10);

        AuthorizeAs("warehouse-ops");

        var response = await Client.PostAsJsonAsync(
            $"/api/v1/customers/{customer.Id}/contracts",
            new { skuId, floorQty = 1, effectiveFrom = DateOnly.FromDateTime(DateTime.UtcNow) });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateContract_UnknownCustomer_Returns404()
    {
        var skuId = await CreateSkuAsync("NOTFOUND-SKU-01", onHand: 10);

        var response = await Client.PostAsJsonAsync(
            $"/api/v1/customers/{Guid.NewGuid()}/contracts",
            new { skuId, floorQty = 1, effectiveFrom = DateOnly.FromDateTime(DateTime.UtcNow) });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateContract_UnknownSku_Returns404()
    {
        var customer = await CreateCustomerAsync("BADSKU-CORP", CustomerTier.Tier2);

        var response = await Client.PostAsJsonAsync(
            $"/api/v1/customers/{customer.Id}/contracts",
            new { skuId = Guid.NewGuid(), floorQty = 1, effectiveFrom = DateOnly.FromDateTime(DateTime.UtcNow) });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateContract_NegativeFloor_Returns422()
    {
        var customer = await CreateCustomerAsync("NEGFLOOR-CORP", CustomerTier.Tier1);
        var skuId    = await CreateSkuAsync("NEGFLOOR-SKU-01", onHand: 10);

        var response = await Client.PostAsJsonAsync(
            $"/api/v1/customers/{customer.Id}/contracts",
            new { skuId, floorQty = -1, effectiveFrom = DateOnly.FromDateTime(DateTime.UtcNow) });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task CreateContract_CeilingBelowFloor_Returns422()
    {
        var customer = await CreateCustomerAsync("BADCEILING-CORP", CustomerTier.Tier1);
        var skuId    = await CreateSkuAsync("BADCEILING-SKU-01", onHand: 10);

        var response = await Client.PostAsJsonAsync(
            $"/api/v1/customers/{customer.Id}/contracts",
            new { skuId, floorQty = 10, ceilingQty = 5, effectiveFrom = DateOnly.FromDateTime(DateTime.UtcNow) });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task CreateContract_EffectiveToBeforeFrom_Returns422()
    {
        var customer = await CreateCustomerAsync("BADDATE-CORP", CustomerTier.Tier1);
        var skuId    = await CreateSkuAsync("BADDATE-SKU-01", onHand: 10);
        var today    = DateOnly.FromDateTime(DateTime.UtcNow);

        var response = await Client.PostAsJsonAsync(
            $"/api/v1/customers/{customer.Id}/contracts",
            new { skuId, floorQty = 1, effectiveFrom = today, effectiveTo = today.AddDays(-1) });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task CreateContract_OverlappingPeriod_Returns422()
    {
        var customer = await CreateCustomerAsync("OVERLAP-CORP", CustomerTier.Tier1);
        var skuId    = await CreateSkuAsync("OVERLAP-SKU-01", onHand: 10);
        var from     = DateOnly.FromDateTime(DateTime.UtcNow);

        await CreateContractAsync(customer.Id, skuId, floorQty: 5, effectiveFrom: from);

        // Open-ended second contract for the same customer+SKU must overlap the first.
        var response = await Client.PostAsJsonAsync(
            $"/api/v1/customers/{customer.Id}/contracts",
            new { skuId, floorQty = 10, effectiveFrom = from.AddDays(30) });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // ── 17b — List Contracts ─────────────────────────────────────────────────

    [Fact]
    public async Task ListContracts_EmptyForNewCustomer_Returns200WithEmptyList()
    {
        var customer = await CreateCustomerAsync("EMPTY-CONTRACTS-CORP", CustomerTier.Tier2);

        var response = await Client.GetAsync($"/api/v1/customers/{customer.Id}/contracts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var contracts = await ReadAsync<IReadOnlyList<ContractResponse>>(response);
        Assert.Empty(contracts);
    }

    [Fact]
    public async Task ListContracts_ReturnsBothContracts()
    {
        var customer = await CreateCustomerAsync("LIST-CORP", CustomerTier.Tier1);
        var skuA     = await CreateSkuAsync("LIST-SKU-A", onHand: 100);
        var skuB     = await CreateSkuAsync("LIST-SKU-B", onHand: 100);
        var today    = DateOnly.FromDateTime(DateTime.UtcNow);

        await CreateContractAsync(customer.Id, skuA, floorQty: 5, effectiveFrom: today);
        await CreateContractAsync(customer.Id, skuB, floorQty: 10, ceilingQty: 50, effectiveFrom: today);

        var response  = await Client.GetAsync($"/api/v1/customers/{customer.Id}/contracts");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var contracts = await ReadAsync<IReadOnlyList<ContractResponse>>(response);
        Assert.Equal(2, contracts.Count);
        Assert.Contains(contracts, c => c.SkuId == skuA && c.FloorQty == 5 && c.CeilingQty == null);
        Assert.Contains(contracts, c => c.SkuId == skuB && c.FloorQty == 10 && c.CeilingQty == 50);
    }

    [Fact]
    public async Task ListContracts_UnknownCustomer_Returns404()
    {
        var response = await Client.GetAsync($"/api/v1/customers/{Guid.NewGuid()}/contracts");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── 17c — Utilization ────────────────────────────────────────────────────

    [Fact]
    public async Task GetUtilization_ActiveContract_BeforeAnyOrders_ReturnsZeroAllocated()
    {
        var customer = await CreateCustomerAsync("UTIL-ZERO-CORP", CustomerTier.Tier1);
        var skuId    = await CreateSkuAsync("UTIL-ZERO-SKU", onHand: 50);
        var today    = DateOnly.FromDateTime(DateTime.UtcNow);

        await CreateContractAsync(customer.Id, skuId, floorQty: 5, ceilingQty: 20, effectiveFrom: today);

        var response = await Client.GetAsync($"/api/v1/customers/{customer.Id}/contracts/utilization");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var utilization = await ReadAsync<IReadOnlyList<ContractUtilizationResponse>>(response);
        var row = Assert.Single(utilization);
        Assert.Equal(skuId, row.SkuId);
        Assert.Equal(5,     row.FloorQty);
        Assert.Equal(20,    row.CeilingQty);
        Assert.Equal(0,     row.AllocatedQty);
    }

    [Fact]
    public async Task GetUtilization_AfterDirectAllocation_ShowsAllocatedQty()
    {
        var customer = await CreateCustomerAsync("UTIL-ALLOC-CORP", CustomerTier.Tier1);
        var skuId    = await CreateSkuAsync("UTIL-ALLOC-SKU", onHand: 50);
        var today    = DateOnly.FromDateTime(DateTime.UtcNow);

        await CreateContractAsync(customer.Id, skuId, floorQty: 5, ceilingQty: 20, effectiveFrom: today);

        var orderResponse = await Client.PostAsJsonAsync("/api/v1/orders", new
        {
            referenceCode = "ORD-UTIL-01",
            priority      = "standard",
            customerId    = customer.Id,
            lines         = new[] { new { skuId, requestedQty = 8 } },
        });
        orderResponse.EnsureSuccessStatusCode();
        var orderId = (await ReadAsync<OrderResponse>(orderResponse)).Id;

        var (allocStatus, _) = await AllocateAsync(orderId);
        Assert.Equal(HttpStatusCode.OK, allocStatus);

        var utilResponse = await Client.GetAsync($"/api/v1/customers/{customer.Id}/contracts/utilization");
        Assert.Equal(HttpStatusCode.OK, utilResponse.StatusCode);

        var utilization = await ReadAsync<IReadOnlyList<ContractUtilizationResponse>>(utilResponse);
        var row = Assert.Single(utilization);
        Assert.Equal(8, row.AllocatedQty);
    }

    [Fact]
    public async Task GetUtilization_UnknownCustomer_Returns404()
    {
        var response = await Client.GetAsync($"/api/v1/customers/{Guid.NewGuid()}/contracts/utilization");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── 17d — Contract-Aware Allocation Run ──────────────────────────────────

    [Fact]
    public async Task AllocationRun_CeilingEnforced_TotalAllocatedDoesNotExceedContractMax()
    {
        var customer = await CreateCustomerAsync("CEILING-CORP", CustomerTier.Tier2);
        var skuId    = await CreateSkuAsync("CEILING-SKU-01", onHand: 100); // ample supply
        var today    = DateOnly.FromDateTime(DateTime.UtcNow);

        // Ceiling of 3; customer has two orders totalling 7 requested units.
        await CreateContractAsync(customer.Id, skuId, floorQty: 0, ceilingQty: 3, effectiveFrom: today);

        var r1 = await Client.PostAsJsonAsync("/api/v1/orders", new
        {
            referenceCode = "ORD-CEIL-01", priority = "standard",
            customerId = customer.Id, lines = new[] { new { skuId, requestedQty = 2 } },
        });
        r1.EnsureSuccessStatusCode();
        var orderId1 = (await ReadAsync<OrderResponse>(r1)).Id;

        var r2 = await Client.PostAsJsonAsync("/api/v1/orders", new
        {
            referenceCode = "ORD-CEIL-02", priority = "standard",
            customerId = customer.Id, lines = new[] { new { skuId, requestedQty = 5 } },
        });
        r2.EnsureSuccessStatusCode();
        var orderId2 = (await ReadAsync<OrderResponse>(r2)).Id;

        var runId = await SubmitAllocationRunAsync();
        await TriggerAllocationWorkerAsync();
        var run = await PollRunUntilCompleteAsync(runId);
        Assert.Equal("completed", run.Status);

        var allocated1 = await GetAllocatedForOrderAsync(orderId1, skuId);
        var allocated2 = await GetAllocatedForOrderAsync(orderId2, skuId);
        Assert.True(allocated1 + allocated2 <= 3,
            $"Expected total allocation <= 3 (ceiling), but got {allocated1 + allocated2}.");
    }

    [Fact]
    public async Task AllocationRun_FloorGuaranteed_ContractCustomerReceivesMinimumBeforeOthers()
    {
        var contractCustomer = await CreateCustomerAsync("FLOOR-PRIORITY-CORP", CustomerTier.Tier1);
        var otherCustomer    = await CreateCustomerAsync("FLOOR-OTHER-CORP",    CustomerTier.Tier3);
        // Supply is exactly the floor; nothing left for the unconstrained customer.
        var skuId = await CreateSkuAsync("FLOOR-SKU-01", onHand: 5);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        await CreateContractAsync(contractCustomer.Id, skuId, floorQty: 5, effectiveFrom: today);

        var r1 = await Client.PostAsJsonAsync("/api/v1/orders", new
        {
            referenceCode = "ORD-FLOOR-CONTRACT", priority = "standard",
            customerId    = contractCustomer.Id,
            lines         = new[] { new { skuId, requestedQty = 5 } },
        });
        r1.EnsureSuccessStatusCode();
        var contractOrderId = (await ReadAsync<OrderResponse>(r1)).Id;

        var r2 = await Client.PostAsJsonAsync("/api/v1/orders", new
        {
            referenceCode = "ORD-FLOOR-OTHER", priority = "standard",
            customerId    = otherCustomer.Id,
            lines         = new[] { new { skuId, requestedQty = 5 } },
        });
        r2.EnsureSuccessStatusCode();
        var otherOrderId = (await ReadAsync<OrderResponse>(r2)).Id;

        var runId = await SubmitAllocationRunAsync();
        await TriggerAllocationWorkerAsync();
        var run = await PollRunUntilCompleteAsync(runId);
        Assert.Equal("completed", run.Status);

        var contractAllocated = await GetAllocatedForOrderAsync(contractOrderId, skuId);
        var otherAllocated    = await GetAllocatedForOrderAsync(otherOrderId, skuId);

        Assert.Equal(5, contractAllocated);
        Assert.Equal(0, otherAllocated);
    }
}
