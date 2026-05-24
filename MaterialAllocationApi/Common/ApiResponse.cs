public record ApiResponse<T>(bool Success, int StatusCode, T? Data, ApiError? Error)
{
    public static ApiResponse<T> Ok(T data) =>
        new(true, 200, data, null);

    public static ApiResponse<T> Created(T data) =>
        new(true, 201, data, null);

    public static ApiResponse<T> Fail(int statusCode, string message, string code) =>
        new(false, statusCode, default, new ApiError(message, code));
}

public record ApiError(string Message, string Code);