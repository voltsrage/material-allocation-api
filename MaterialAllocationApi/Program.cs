using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MaterialAllocationApi.Common.Middleware;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    // TIMESTAMPTZ maps to DateTimeOffset in both EF and Dapper. Without this, Npgsql 6+
    // maps it to DateTime(UTC), causing a type mismatch between EF entities and Dapper DTOs.
    AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

    // Dapper maps snake_case column names (sku_code) to PascalCase C# properties (SkuCode)
    // automatically — no column aliases needed in SQL queries.
    Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, lc) => lc
        .ReadFrom.Configuration(ctx.Configuration)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .Enrich.WithThreadId());

    builder.Services.Configure<RouteOptions>(o => o.LowercaseUrls = true);

    // App runtime uses the restricted role (DML only - no DDL privileges)
    builder.Services.AddDbContext<AllocationDbContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

    // Add services to the container.

    builder.Services.AddScoped<IDbConnectionFactory, NpgsqlConnectionFactory>();
    builder.Services.AddScoped<ISkuService, SkuService>();
    builder.Services.AddScoped<IOrderService, OrderService>();
    builder.Services.AddScoped<IAllocationService, AllocationService>();
    builder.Services.AddScoped<IReservationService, ReservationService>();
    builder.Services.AddHostedService<ReservationExpiryJob>();
    builder.Services.AddScoped<IRollupService, RollupService>();
    builder.Services.AddScoped<ITokenService, JwtTokenService>();
    builder.Services.AddScoped<IAllocationRunService, AllocationRunService>();

    builder.Services.Configure<OutboxRelaySettings>(
        builder.Configuration.GetSection("OutboxRelay")
    );
    builder.Services.AddScoped<IEventPublisher, LoggingEventPublisher>();
    builder.Services.AddHostedService<OutboxRelayJob>();

    builder.Services.AddHostedService<IdempotencyCleanupJob>();

    builder.Services.AddHostedService<AllocationRunWorker>();

    var authSettings = builder.Configuration
        .GetSection("Authentication")
        .Get<AuthSettings>()!;

    builder.Services.Configure<AuthSettings>(
        builder.Configuration.GetSection("Authentication")
    );

    var signingKey = new SymmetricSecurityKey(
        Encoding.UTF8.GetBytes(authSettings.JwtSecret)
    );

    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new
            TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = authSettings.Issuer,
                ValidAudience= authSettings.Audience,
                IssuerSigningKey = signingKey,
                ClockSkew = TimeSpan.Zero
            };

            // Return JSON in the standard ApiResponse envelope instead of the
            // default 401/403 plain-text or redirect responses

            options.Events = new JwtBearerEvents
            {
                OnChallenge = async ctx =>
                {
                    ctx.HandleResponse();
                    ctx.Response.StatusCode = 401;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsJsonAsync(
                        ApiResponse<object>.Fail(401, "Authentication required.", "UNAUTHORIZED")
                    );
                },
                OnForbidden = async ctx =>
                {
                    ctx.Response.StatusCode  = 403;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsJsonAsync(
                        ApiResponse<object>.Fail(403, "Insufficient permissions.", "FORBIDDEN"));
                }
            };
        });

    builder.Services.AddAuthorization();

    builder.Services.Configure<IdempotencySettings>(
        builder.Configuration.GetSection("Idempotency")
    );

    builder.Services.AddControllers()
        .AddJsonOptions(o =>
            o.JsonSerializerOptions.Converters.Add(
                new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower)));
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new OpenApiInfo
        {
            Title   = "Material Allocation API",
            Version = "v1",
            Description = """
                Allocates constrained inventory (SKUs) to competing orders under concurrency.

                **Concurrency strategy:** `SELECT ... FOR UPDATE` on SKU rows in ascending `sku_id` order.
                All operations that touch inventory (allocate, reserve, cancel-release) acquire these locks
                first, guaranteeing that concurrent requests are serialized at the database layer.

                **Error model:** every non-2xx response returns `ApiResponse<null>` with a machine-readable
                `error.code` field. Conflict codes: `ORDER_CANCELLED`, `ORDER_FULLY_ALLOCATED`,
                `ORDER_ALREADY_CANCELLED`, `CONCURRENT_MODIFICATION`. Validation code: `VALIDATION_ERROR`.
                """
        });

        // Load the XML file generated by <GenerateDocumentationFile>.
        var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
        var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
        options.IncludeXmlComments(xmlPath);

        options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Name         = "Authorization",
            Type         = SecuritySchemeType.Http,
            Scheme       = "bearer",
            BearerFormat = "JWT",
            In           = ParameterLocation.Header,
            Description  = "Paste a JWT from POST /api/v1/auth/token. Example: `Bearer eyJ...`"
        });

        options.AddSecurityRequirement(_ => new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecuritySchemeReference("Bearer"),
                new List<string>()
            }
        });

        options.OperationFilter<IdempotencyHeaderOperationFilter>();

        // Group operations under their controller tags.
        options.TagActionsBy(api => [api.GroupName ?? api.ActionDescriptor.RouteValues["controller"]!]); 
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

    builder.Services.AddHealthChecks()
        .AddNpgSql(
            connectionString: builder.Configuration.GetConnectionString("Postgres")!,
            name: "postgres",
            tags: ["db", "ready"]
        )
        .AddCheck<OutboxLagHealthCheck>("outbox-lag", tags: ["ready"])
        .AddCheck<AllocationRunHealthCheck>("allocation-run", tags: ["ready"]);;

    var app = builder.Build();

    app.UseSerilogRequestLogging(options =>
    {
        options.MessageTemplate =
            "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.000}ms [{CorrelationId}]";

        options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
        {
            diagnosticContext.Set("RequestHost",   httpContext.Request.Host.Value);
            diagnosticContext.Set("CorrelationId",
                httpContext.Response.Headers["X-Correlation-ID"].ToString());
        };
    });

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseMiddleware<CorrelationIdMiddleware>();
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

        // Skip seeder in test environment - tess create their own known data
        if(!app.Environment.IsEnvironment("Test"))
            await SkuSeeder.SeedAsync(db);
    }

    app.UseAuthentication();
    app.UseAuthorization();
    app.UseMiddleware<IdempotencyMiddleware>();
    app.MapControllers();
    app.MapHealthChecks("/health", new HealthCheckOptions
    {
        ResponseWriter = WriteHealthJson
    });

    app.MapHealthChecks("/health/live", new HealthCheckOptions
    {
        // Liveness: always returns Healthy if the process is running. No dependency checks.
        Predicate      = _ => false,
        ResponseWriter = WriteHealthJson
    });

    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        // Readiness: returns Healthy only when all "ready"-tagged dependencies are up.
        Predicate      = hc => hc.Tags.Contains("ready"),
        ResponseWriter = WriteHealthJson
    });
    app.Run();
}
/***
dotnet ef tools use HostFactoryResolver which throws HostAbortedException internally as a control-flow mechanism to stop the host after discovering the DbContext. 
Your generic catch was swallowing it instead of letting it propagate, so EF saw the process exit abnormally.
***/ 
catch (HostAbortedException)
{
    throw;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application failed to start.");
}
finally
{
    Log.CloseAndFlush();
}


static Task WriteHealthJson(HttpContext ctx, HealthReport report)
{
    ctx.Response.ContentType = "application/json";

    var payload = new
    {
        status = report.Status.ToString(),
        totalDuration = report.TotalDuration,
        checks = report.Entries.Select(e => new
        {
            name        = e.Key,
            status      = e.Value.Status.ToString(),
            description = e.Value.Description,
            duration    = e.Value.Duration,
            tags        = e.Value.Tags
        })
    };

    return ctx.Response.WriteAsJsonAsync(payload, new JsonSerializerOptions
    {
        WriteIndented   = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    });
}

// Exposes Program as a public type so WebApplicationFactory<Program> can reference it
// from the test project. The partial class matches the implicit top-level Program class.
public partial class Program { }

