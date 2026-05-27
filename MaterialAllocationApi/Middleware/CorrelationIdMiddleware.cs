using System.Security.Claims;
using Serilog.Context;

namespace MaterialAllocationApi.Common.Middleware;

public class CorrelationIdMiddleware
{
    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        // Accept caller-supplied ID so distributed traces can correlate across services.
        // Fall back to a short random ID when not supplied.
        var correlationId = context.Request.Headers["X-Correlation-ID"].FirstOrDefault()
            ?? Guid.NewGuid().ToString("N")[..12];

        context.Response.Headers["X-Correlation-ID"] = correlationId;

        // Push into Serilog's LogContext so every log entry within this request carries
        // the CorrelationId property without callers needing to pass it explicitly.
        var role = context.User.FindFirst(ClaimTypes.Role)?.Value ?? "anonymous";
        using (LogContext.PushProperty("CallerRole", role))
        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context);
        }
    }
}