using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Authentication;

namespace MaterialAllocationApi.Tests.Fixtures;

public class ApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddJsonFile(
                Path.Combine(AppContext.BaseDirectory, "appsettings.Test.json"),
                optional: false
            );
        });

        builder.ConfigureServices(services =>
        {
            // Remove the background relay job from the test host.
            // The relay has a known bug where Task.Delay sits outside the while loop,
            // causing it to spin at 100 % CPU and process every outbox message the instant
            // it is written — this races with any test that needs to inspect an unprocessed
            // row before the relay touches it.
            // Tests that need to exercise relay behaviour call InvokeRelayBatchAsync()
            // directly, which creates a fresh OutboxRelayJob instance on demand.
            var descriptor = services.SingleOrDefault(
                d => d.ImplementationType == typeof(OutboxRelayJob));
            if (descriptor != null)
                services.Remove(descriptor);

            // Remove the allocation run worker from the test host.
            // Its 5-second poll interval is longer than the test's total polling window,
            // making timing non-deterministic and causing stale runs from previous sessions
            // to interfere with the current test.
            // Tests that need to exercise allocation run behaviour call
            // TriggerAllocationWorkerAsync() directly, which invokes one processing cycle
            // on demand via reflection — mirroring the OutboxRelayJob pattern above.
            var workerDescriptor = services.SingleOrDefault(
                d => d.ImplementationType == typeof(AllocationRunWorker));
            if (workerDescriptor != null)
                services.Remove(workerDescriptor);

            // Replace JWT Bearer with the test handler so tests don't need real tokens.
            // Auth enforcement (401/403) is still active — only the token format changes.
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.SchemeName, _=>{}
                );
        });
    }

    public async Task InitializeAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AllocationDbContext>();
        await db.Database.MigrateAsync();
    }

    public new Task DisposeAsync() => base.DisposeAsync().AsTask();

    public async Task ResetDatabaseAsync()
    {
        using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AllocationDbContext>();

        // Delete in reverse FK dependency order.
        // idempotency_keys and outbox_messages have no FK constraints — safe to clear at any point.
        await db.Database.ExecuteSqlRawAsync("DELETE FROM allocation_runs");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM idempotency_keys");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM outbox_messages");
        // allocation_events has RESTRICT FKs to order_lines, orders, and skus,
        // so it must be deleted before any of those three tables.
        await db.Database.ExecuteSqlRawAsync("DELETE FROM allocation_events");
        // inventory_adjustments has a RESTRICT FK to skus — must precede skus.
        await db.Database.ExecuteSqlRawAsync("DELETE FROM inventory_adjustments");
        // order_lines deletion cascades to reservations.
        await db.Database.ExecuteSqlRawAsync("DELETE FROM order_lines");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM orders");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM skus");
    }

    public string GetConnectionString() =>
        Services.GetRequiredService<IConfiguration>()
            .GetConnectionString("Postgres");
}
// Sharing one ApiFixture instance across multiple test classes avoids starting the
// WebApplicationFactory (and running migrations) once per class.
[CollectionDefinition("Allocation")]
public class AllocationCollection : ICollectionFixture<ApiFixture> {}