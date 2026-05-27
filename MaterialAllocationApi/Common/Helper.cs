using System.Text.Json;

public static class Helpers
{
    public static string Serialize(object payload) =>
        System.Text.Json.JsonSerializer.Serialize(payload, 
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }
        );
}