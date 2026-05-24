using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

public class AllocationDbContextFactory : IDesignTimeDbContextFactory<AllocationDbContext>
{
    public AllocationDbContext CreateDbContext(string[] args)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json")
            .AddJsonFile("appsettings.development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var options = new DbContextOptionsBuilder<AllocationDbContext>()
            .UseNpgsql(config.GetConnectionString("PostgresMigrator"))
            .Options;

        return new AllocationDbContext(options);
    }
}