using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

// TIMESTAMPTZ maps to DateTimeOffset in both EF and Dapper. Without this, Npgsql 6+
// maps it to DateTime(UTC), causing a type mismatch between EF entities and Dapper DTOs.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

// Dapper maps snake_case column names (sku_code) to PascalCase C# properties (SkuCode)
// automatically — no column aliases needed in SQL queries.
Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<RouteOptions>(o => o.LowercaseUrls = true);

// App runtime uses the restricted role (DML only - no DDL privileges)
builder.Services.AddDbContext<AllocationDbContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

// Add services to the container.

builder.Services.AddScoped<IDbConnectionFactory, NpgsqlConnectionFactory>();
builder.Services.AddScoped<ISkuService, SkuService>();
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<IAllocationService, AllocationService>();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Material Allocation API", Version = "v1" });  
});

builder.Services.Configure<ApiBehaviorOptions>(options => 
    options.InvalidModelStateResponseFactory = ctx =>
    {
        var message = ctx.ModelState.Values
            .SelectMany(v => v.Errors)
            .Select(e => e.ErrorMessage)
            .FirstOrDefault() ?? "Validation failed.";

        return new UnprocessableEntityObjectResult(
            ApiResponse<object>.Fail(422, message, "VALIDATION_ERROR")
        );
    }
);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<ExceptionHandlerMiddleware>();

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

app.MapControllers();
app.Run();

