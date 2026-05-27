
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

public class IdempotencyHeaderOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        // Only POST operations, skip the auth token endpoint.
        if (context.ApiDescription.HttpMethod is not "POST") return;
        if (context.ApiDescription.RelativePath?.Contains("auth/token") == true) return;

        operation.Parameters ??= new List<IOpenApiParameter>();

        operation.Parameters.Add(new OpenApiParameter
        {
            Name        = "Idempotency-Key",
            In          = ParameterLocation.Header,
            Required    = false,
            Schema      = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = 128 },
            Description =
                "Client-generated key (recommended: UUID v4) that makes this request idempotent. " +
                "If the same key is sent again within 24 hours, the original response is replayed " +
                "without re-executing the operation. The response includes " +
                "`X-Idempotency-Replayed: true` when a replay occurs."
        });
    }
}