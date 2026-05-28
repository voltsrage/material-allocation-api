using System.Net;
using System.Net.Http.Json;
using MaterialAllocationApi.Tests.Fixtures;
using MaterialAllocationApi.Tests.Helpers;

namespace MaterialAllocationApi.Tests;

[Collection("Allocation")]
public class CustomerTests(ApiFixture fixture) : AllocationTestBase(fixture)
{
    // ── 5a — Customer CRUD ──────────────────────────────────────────────────────

    [Fact]
    public async Task CreateCustomer_Returns201WithCustomerFields()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/customers", new
        {
            customerCode = "APPLE-NA",
            name         = "Apple North America",
            tier         = "tier1",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var customer = await ReadAsync<CustomerResponse>(response);
        Assert.NotEqual(Guid.Empty, customer.Id);
        Assert.Equal("APPLE-NA",  customer.CustomerCode);
        Assert.Equal("tier-1",    customer.Tier);
    }

    [Fact]
    public async Task CreateCustomer_DuplicateCode_Returns422()
    {
        await CreateCustomerAsync("DUP-01", "First Corp", "tier1");

        var response = await Client.PostAsJsonAsync("/api/v1/customers", new
        {
            customerCode = "DUP-01",
            name         = "Second Corp",
            tier         = "tier1",
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task GetCustomer_ById_Returns200WithAllFields()
    {
        var created = await CreateCustomerAsync("BANANA-EU", "Banana Europe", "tier2");

        var response = await Client.GetAsync($"/api/v1/customers/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var customer = await ReadAsync<CustomerResponse>(response);
        Assert.Equal(created.Id,    customer.Id);
        Assert.Equal("BANANA-EU",   customer.CustomerCode);
        Assert.Equal("Banana Europe", customer.Name);
        Assert.Equal("tier-2",      customer.Tier);
    }

    [Fact]
    public async Task GetCustomer_UnknownId_Returns404()
    {
        var response = await Client.GetAsync($"/api/v1/customers/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ListCustomers_ReturnsPaginatedResultsOrderedByCode()
    {
        await CreateCustomerAsync("AARDVARK-INC",  "Aardvark Inc",  "tier3");
        await CreateCustomerAsync("BANANA-CORP",   "Banana Corp",   "tier2");
        await CreateCustomerAsync("CHERRY-GLOBAL", "Cherry Global", "tier1");

        var response = await Client.GetAsync("/api/v1/customers?page=1&pageSize=2");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = await ReadAsync<PagedResult<CustomerResponse>>(response);
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal("AARDVARK-INC", page.Items[0].CustomerCode);
        Assert.Equal("BANANA-CORP",  page.Items[1].CustomerCode);
    }

    // ── 5b — Order linkage ──────────────────────────────────────────────────────

    [Fact]
    public async Task CreateOrder_WithValidCustomerId_Returns201AndCustomerFieldsOnGet()
    {
        var customer = await CreateCustomerAsync("LINK-CORP", "Link Corp", "tier1");
        var skuId    = await CreateSkuAsync("LINK-SKU-01", onHand: 10);

        var orderId = await CreateOrderWithCustomerAsync("ORD-LINK-01", skuId, 5, customer.Id);

        var order = await GetOrderAsync(orderId);
        Assert.Equal(customer.Id,   order.CustomerId);
        Assert.Equal("LINK-CORP",   order.CustomerCode);
        Assert.Equal("Link Corp",   order.CustomerName);
    }

    [Fact]
    public async Task CreateOrder_WithoutCustomerId_Returns201AndNullCustomerFields()
    {
        var skuId   = await CreateSkuAsync("NOLINK-SKU-01", onHand: 10);
        var orderId = await CreateOrderAsync("ORD-NOLINK-01", skuId, requestedQty: 5);

        var order = await GetOrderAsync(orderId);
        Assert.Null(order.CustomerId);
        Assert.Null(order.CustomerCode);
        Assert.Null(order.CustomerName);
    }

    [Fact]
    public async Task CreateOrder_WithUnknownCustomerId_Returns422()
    {
        var skuId = await CreateSkuAsync("BADCUST-SKU-01", onHand: 10);

        var response = await Client.PostAsJsonAsync("/api/v1/orders", new
        {
            referenceCode = "ORD-BADCUST-01",
            priority      = "standard",
            customerId    = Guid.NewGuid(),
            lines         = new[] { new { skuId, requestedQty = 5 } },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task ListOrders_FilteredByCustomerId_ReturnsOnlyMatchingOrders()
    {
        var customerA = await CreateCustomerAsync("CUST-FILTER-A", "Customer A", "tier1");
        var customerB = await CreateCustomerAsync("CUST-FILTER-B", "Customer B", "tier3");
        var skuId     = await CreateSkuAsync("LIST-FILT-SKU", onHand: 50);

        await CreateOrderWithCustomerAsync("ORD-LISTA-01", skuId, 5, customerA.Id);
        await CreateOrderWithCustomerAsync("ORD-LISTA-02", skuId, 5, customerA.Id);
        await CreateOrderWithCustomerAsync("ORD-LISTB-01", skuId, 5, customerB.Id);

        var response = await Client.GetAsync($"/api/v1/orders?customerId={customerA.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = await ReadAsync<PagedResult<OrderSummaryResponse>>(response);
        Assert.Equal(2, page.TotalCount);
        Assert.All(page.Items, o => Assert.Equal(customerA.Id, o.CustomerId));
    }

    [Fact]
    public async Task ListOrders_IncludesCustomerCodeInSummary()
    {
        var customer = await CreateCustomerAsync("SUMMARY-CORP", "Summary Corp", "tier2");
        var skuId    = await CreateSkuAsync("SUMMARY-SKU-01", onHand: 10);

        await CreateOrderWithCustomerAsync("ORD-SUMMARY-01", skuId, 5, customer.Id);

        var response = await Client.GetAsync("/api/v1/orders");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = await ReadAsync<PagedResult<OrderSummaryResponse>>(response);
        var row  = Assert.Single(page.Items);
        Assert.Equal("SUMMARY-CORP", row.CustomerCode);
    }

    [Fact]
    public async Task AllocateThenCancel_WithCustomerId_PreservesCustomerLink()
    {
        var customer = await CreateCustomerAsync("CYCLE-CORP", "Cycle Corp", "tier1");
        var skuId    = await CreateSkuAsync("CYCLE-SKU-01", onHand: 10);
        var orderId  = await CreateOrderWithCustomerAsync("ORD-CYCLE-01", skuId, 5, customer.Id);

        var (allocStatus, _) = await AllocateAsync(orderId);
        Assert.Equal(HttpStatusCode.OK, allocStatus);

        var afterAlloc = await GetOrderAsync(orderId);
        Assert.Equal(customer.Id,  afterAlloc.CustomerId);
        Assert.Equal("CYCLE-CORP", afterAlloc.CustomerCode);

        var cancelResponse = await Client.PostAsync($"/api/v1/orders/{orderId}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancelResponse.StatusCode);

        var afterCancel = await GetOrderAsync(orderId);
        Assert.Equal(customer.Id,  afterCancel.CustomerId);
        Assert.Equal("CYCLE-CORP", afterCancel.CustomerCode);
        Assert.Equal("cancelled",  afterCancel.Status);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private async Task<CustomerResponse> CreateCustomerAsync(
        string code, string name, string tier)
    {
        var response = await Client.PostAsJsonAsync("/api/v1/customers", new
        {
            customerCode = code,
            name,
            tier,
        });
        response.EnsureSuccessStatusCode();
        return await ReadAsync<CustomerResponse>(response);
    }

    private async Task<Guid> CreateOrderWithCustomerAsync(
        string referenceCode, Guid skuId, int requestedQty, Guid customerId,
        string priority = "standard")
    {
        var response = await Client.PostAsJsonAsync("/api/v1/orders", new
        {
            referenceCode,
            priority,
            customerId,
            lines = new[] { new { skuId, requestedQty } },
        });
        response.EnsureSuccessStatusCode();
        return (await ReadAsync<OrderResponse>(response)).Id;
    }
}
