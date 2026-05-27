using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

public class IdempotencyMiddleware
{
    private const string HeaderName = "Idempotency-Key";
    private const string ExcludedPath = "/api/v1/auth/token";

    private readonly RequestDelegate _next;
    private readonly IdempotencySettings _settings;

    public IdempotencyMiddleware(RequestDelegate next, IOptions<IdempotencySettings> settings)
    {
        _next = next;
        _settings = settings.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Only POST mutations, GET, DELETE, PUT are either safe or handled elsewhere
        if(!HttpMethods.IsPost(context.Request.Method) ||
            context.Request.Path.StartsWithSegments(ExcludedPath)
        )
        {
            await _next(context);
            return;
        }

        if(!context.Request.Headers.TryGetValue(HeaderName, out var keyValues))
        {
            await _next(context);
            return;
        }

        var key = keyValues.ToString().Trim();

        if(string.IsNullOrEmpty(key) || key.Length > 128)
        {
            context.Response.StatusCode = 422;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(
                ApiResponse<object>.Fail(422,
                    $"'{HeaderName}' must be a non-empty string of at most 128 characters.",
                    "VALIDATION_ERROR"));
            return;
        }

        var db = context.RequestServices.GetRequiredService<AllocationDbContext>();
        var path = context.Request.Path.Value!;

        // -- Phase 1: try to claim this key by inserting a 'processing' record---
        var record = new IdempotencyRecord(key, path, context.Request.Method, DateTimeOffset.UtcNow.AddHours(_settings.ExpiryHours));

        db.IdempotencyRecords.Add(record);

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex))
        {
            // Key already exists - detach the failed entity so EF does not track it.
            db.Entry(record).State = EntityState.Detached;

            var existing = await db.IdempotencyRecords
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.IdempotencyKey == key);

            if(existing is null)
            {
                // Deleted by cleanup between the failed insert and this read - treat as new
                await _next(context);
                return;
            }

            // Validate the caller is reusing the key for the same operation
            if(existing.RequestPath != path)
            {
                context.Response.StatusCode  = 422;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(
                    ApiResponse<object>.Fail(422,
                        $"Idempotency key was previously used for '{existing.RequestMethod} {existing.RequestPath}'.",
                        "IDEMPOTENCY_KEY_MISMATCH"));
                return;
            }

            if(existing.Status == "processing")
            {
                context.Response.StatusCode = 409;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(
                    ApiResponse<object>.Fail(409,
                        "A request with this idempotency key is already in flight.",
                        "IDEMPOTENCY_IN_FLIGHT"));
                return;
            }

            // Status == "complete": replay stored response
            context.Response.StatusCode = existing.ResponseStatus!.Value;
            context.Response.ContentType = "application/json";
            context.Response.Headers["X-Idempotency-Replayed"] = "true";
            await context.Response.WriteAsync(existing.ResponseBody!);
            return;
        }

        // --- Phase 2; key claimed. Buffer the response and mark complete ---
        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        Exception? caughtException = null;
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            caughtException = ex;
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        if(caughtException is not null)
        {
            // Let ExceptionHandlerMiddleware (upstream) handle and write the error
            // The 'processing' record stays - the cleanup job will delete it after
            // StuckProcessingAgeMinutes. Don not store 5xx outcomes as idempotent
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(caughtException).Throw();
        }

        buffer.Seek(0, SeekOrigin.Begin);

        var responseBody = await new StreamReader(buffer).ReadToEndAsync();

        // Only persist 2xx and 4xx outcomes. 5xx may be transient; the client should retry
        if(context.Response.StatusCode < 500)
        {
            var tracked = await db.IdempotencyRecords
                .FirstOrDefaultAsync(r => r.IdempotencyKey == key);

            if(tracked is not null)
            {
                tracked.Complete(context.Response.StatusCode, responseBody);
                await db.SaveChangesAsync();
            }
        }

        // Write the buffered response to the original stream
        buffer.Seek(0, SeekOrigin.Begin);
        await buffer.CopyToAsync(context.Response.Body);
    }
}