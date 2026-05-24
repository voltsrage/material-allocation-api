using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// App runtime uses the restricted role (DML only - no DDL privileges)
builder.Services.AddDbContext<AllocationDbContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

// Add services to the container.

var app = builder.Build();

// Migrations run under the migrator role, which holds DDL privileges
// The app role (dotnetter) cannot CREATE or ALTER tables, so it cannot run migrations
var migratorCs = app.Configuration.GetConnectionString("PostgresMigrator")!;

var migratorOptions = new DbContextOptionsBuilder<AllocationDbContext>()
    .UseNpgsql(migratorCs)
    .Options;
await using(var migratorDb = new AllocationDbContext(migratorOptions))
    await migratorDb.Database.MigrateAsync();

// Seeding uses the app role to confirm that DML grants from ALTER DEFAULT PRIVILEGES
// FOR ROLE material_allocation_migrator are correct. If grants are missing, seeding fails.
using(var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AllocationDbContext>();
    await SkuSeeder.SeedAsync(db);
}

app.Run();

