using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
        // Order_lines -> orders (cascade would handle it, but explicit is clearer in tests)
        await db.Database.ExecuteSqlRawAsync("DELETE FROM inventory_adjustments");
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