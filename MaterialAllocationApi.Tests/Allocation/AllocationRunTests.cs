using System.Net;
using System.Net.Http.Json;
using MaterialAllocationApi.Tests.Fixtures;
using MaterialAllocationApi.Tests.Helpers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MaterialAllocationApi.Tests.Allocation;

[Collection("Allocation")]
public class AllocationRunTests(ApiFixture fixture) : AllocationTestBase(fixture)
{
    // ── 5c-i: POST returns 202 with a valid run ID ───────────────────────────

    [Fact]
    public async Task EnqueueRun_Returns202WithValidRunId()
    {
        var response = await Client.PostAsync("/api/v1/allocations/run", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var accepted = await ReadAsync<AllocationRunAcceptedResponse>(response);
        Assert.NotEqual(Guid.Empty, accepted.RunId);

        // Confirm the run ID is immediately readable via the status endpoint.
        var statusResponse = await Client.GetAsync($"/api/v1/allocations/runs/{accepted.RunId}");
        statusResponse.EnsureSuccessStatusCode();
    }

    // ── 5c-ii: Run transitions through pending → running → completed ─────────

    [Fact]
    public async Task Run_AfterProcessing_HasTimestampsAndNonNegativeOrderCount()
    {
        var runId = await SubmitAllocationRunAsync();
        await TriggerAllocationWorkerAsync();
        var run = await PollRunUntilCompleteAsync(runId);

        Assert.Equal("completed", run.Status);
        Assert.NotNull(run.StartedAt);
        Assert.NotNull(run.CompletedAt);
        Assert.True(run.OrdersProcessed >= 0);
    }

    // ── 5c-iii: Completed run contains per-order results ─────────────────────

    [Fact]
    public async Task CompletedRun_ResultsContainEntryForEachOrderWithCorrectFields()
    {
        var skuId      = await CreateSkuAsync("RUN-RESULT-SKU", onHand: 20);
        var standardId = await CreateOrderAsync("RUN-ORD-STD",  skuId, requestedQty: 5, priority: "standard");
        var criticalId = await CreateOrderAsync("RUN-ORD-CRIT", skuId, requestedQty: 5, priority: "critical");

        var runId = await SubmitAllocationRunAsync();
        await TriggerAllocationWorkerAsync();
        var run = await PollRunUntilCompleteAsync(runId);

        Assert.Equal("completed", run.Status);
        Assert.NotNull(run.Results);
        Assert.Equal(2, run.Results!.Count);

        var resultIds = run.Results.Select(r => r.OrderId).ToHashSet();
        Assert.Contains(standardId, resultIds);
        Assert.Contains(criticalId, resultIds);

        var critResult = run.Results.Single(r => r.OrderId == criticalId);
        Assert.Equal("critical",        critResult.Priority);
        Assert.Equal("fully_allocated", critResult.Status);
        Assert.True(critResult.IsFullyAllocated);

        var stdResult = run.Results.Single(r => r.OrderId == standardId);
        Assert.Equal("standard", stdResult.Priority);
    }

    // ── 5c-iv: 409 when a run is already pending ─────────────────────────────

    [Fact]
    public async Task EnqueueRun_WhenAlreadyPending_Returns409WithInProgressRunId()
    {
        var firstRunId = await SubmitAllocationRunAsync();
        // Do NOT trigger the worker — first run stays "pending".

        var response = await Client.PostAsync("/api/v1/allocations/run", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var envelope = await ReadEnvelopeAsync<object>(response);
        Assert.Equal("RUN_IN_PROGRESS", envelope.Error!.Code);
        Assert.Contains(firstRunId.ToString(), envelope.Error.Message);
    }

    // ── 5c-v: New run accepted once previous is complete ─────────────────────

    [Fact]
    public async Task EnqueueRun_AfterPreviousRunCompletes_Returns202()
    {
        var firstRunId = await SubmitAllocationRunAsync();
        await TriggerAllocationWorkerAsync();
        await PollRunUntilCompleteAsync(firstRunId);

        var response = await Client.PostAsync("/api/v1/allocations/run", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    // ── 5c-vi: GET 404 for an unknown run ID ─────────────────────────────────

    [Fact]
    public async Task GetRun_WithUnknownId_Returns404WithNotFoundCode()
    {
        var response = await Client.GetAsync($"/api/v1/allocations/runs/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var envelope = await ReadEnvelopeAsync<object>(response);
        Assert.Equal("NOT_FOUND", envelope.Error!.Code);
    }

    // ── 5c-vii: List returns recent runs newest-first ─────────────────────────

    [Fact]
    public async Task ListRuns_ReturnsBothRunsNewestFirst()
    {
        var runId1 = await SubmitAllocationRunAsync();
        await TriggerAllocationWorkerAsync();
        await PollRunUntilCompleteAsync(runId1);

        var runId2 = await SubmitAllocationRunAsync();
        await TriggerAllocationWorkerAsync();
        await PollRunUntilCompleteAsync(runId2);

        var response = await Client.GetAsync("/api/v1/allocations/runs");
        response.EnsureSuccessStatusCode();

        var runs = await ReadAsync<IReadOnlyList<AllocationRunSummary>>(response);

        Assert.True(runs.Count >= 2, $"Expected at least 2 runs in list, got {runs.Count}.");

        var positions = runs.Select(r => r.RunId).ToList();
        var idx1 = positions.IndexOf(runId1);
        var idx2 = positions.IndexOf(runId2);

        Assert.NotEqual(-1, idx1);
        Assert.NotEqual(-1, idx2);
        Assert.True(idx2 < idx1,
            $"Expected run2 at position {idx2} to precede run1 at position {idx1} (newest-first).");
    }

    // ── 5c-viii: Run with no open orders completes with zero ordersProcessed ──

    [Fact]
    public async Task Run_WithNoOpenOrders_CompletesWithZeroOrdersProcessed()
    {
        // No orders — the reset in InitializeAsync cleared the DB.
        var runId = await SubmitAllocationRunAsync();
        await TriggerAllocationWorkerAsync();
        var run = await PollRunUntilCompleteAsync(runId);

        Assert.Equal("completed", run.Status);
        Assert.Equal(0, run.OrdersProcessed);
    }

    // ── 5c-ix: Failed run is recorded with error message ─────────────────────

    [Fact]
    public async Task Run_WhenAllocationServiceThrows_RecordsFailedStatusWithError()
    {
        // Build a derived factory that replaces IAllocationService with a faulting stub.
        // Fixture.WithWebHostBuilder inherits all of ApiFixture's ConfigureWebHost
        // (test auth, worker removal, appsettings.Test.json), then layers our override on top.
        await using var faultFactory = Fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAllocationService>();
                services.AddScoped<IAllocationService, FaultingAllocationService>();
            }));

        var faultClient = faultFactory.CreateClient();
        faultClient.DefaultRequestHeaders.Add(
            TestAuthHandler.RoleHeader, "allocation-manager");

        var postResponse = await faultClient.PostAsync("/api/v1/allocations/run", null);
        Assert.Equal(HttpStatusCode.Accepted, postResponse.StatusCode);
        var accepted = await ReadAsync<AllocationRunAcceptedResponse>(postResponse);

        // Drive the worker using the faulting factory's DI scope so it resolves
        // FaultingAllocationService instead of the real AllocationService.
        await TriggerAllocationWorkerAsync(faultFactory.Services);

        // Poll via the same server instance.
        AllocationRunStatusResponse? run = null;
        for (var i = 0; i < 10; i++)
        {
            var statusResponse = await faultClient.GetAsync(
                $"/api/v1/allocations/runs/{accepted.RunId}");
            statusResponse.EnsureSuccessStatusCode();
            run = await ReadAsync<AllocationRunStatusResponse>(statusResponse);
            if (run.Status is "completed" or "failed") break;
            await Task.Delay(250);
        }

        Assert.NotNull(run);
        Assert.Equal("failed", run!.Status);
        Assert.NotNull(run.Error);
        Assert.Contains("Simulated", run.Error, StringComparison.OrdinalIgnoreCase);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<ApiResponse<T>> ReadEnvelopeAsync<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ApiResponse<T>>(JsonOptions))!;

    // Implements only RunPriorityAllocationAsync (used by the worker). All other
    // methods are unreachable in this test scenario.
    private sealed class FaultingAllocationService : IAllocationService
    {
        public Task<AllocationResponse> AllocateAsync(Guid orderId, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<AvailabilityResponse> GetAvailabilityAsync(Guid skuId, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<AllocationRunResponse> RunPriorityAllocationAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("Simulated allocation failure.");

        public Task<IReadOnlyList<AllocationEventResponse>> GetEventsAsync(Guid orderId, CancellationToken ct = default) =>
            throw new NotImplementedException();
    }
}
