public class ExceptionHandlerMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlerMiddleware> _logger;

    public ExceptionHandlerMiddleware(RequestDelegate next, ILogger<ExceptionHandlerMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (NotFoundException ex)
        {
            _logger.LogWarning("{Message}", ex.Message);
            await WriteAsync(context, 404, ApiResponse<object>.Fail(404, ex.Message, "NOT_FOUND"));
        }
        catch (ValidationException ex)
        {
            _logger.LogWarning("{Message}", ex.Message);
            await WriteAsync(context, 422, ApiResponse<object>.Fail(422, ex.Message, "VALIDATION_ERROR"));
        }
        catch (ConflictException ex)
        {
            _logger.LogWarning("{Message}", ex.Message);
            await WriteAsync(context, 409, ApiResponse<object>.Fail(409, ex.Message, ex.Code));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception");
            await WriteAsync(context, 500, ApiResponse<object>.Fail(500, "An unexpected error occurred.", "INTERNAL_ERROR"));
        }
    }

    private static Task WriteAsync<T>(HttpContext context, int status, ApiResponse<T> body)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(body);
    }
}