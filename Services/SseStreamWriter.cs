using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.Lambda.Core.ResponseStreaming;

namespace ClientOnboardingLambda.Services;

public sealed class SseStreamWriter(Stream output)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task WriteEventAsync(string eventName, object? data, CancellationToken cancellationToken = default)
    {
        var payload = data is null ? "{}" : JsonSerializer.Serialize(data, JsonOptions);
        var message = $"event: {eventName}\ndata: {payload}\n\n";
        var bytes = Encoding.UTF8.GetBytes(message);
        await output.WriteAsync(bytes, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    public static HttpResponseStreamPrelude CreatePrelude(HttpStatusCode statusCode = HttpStatusCode.OK) => new()
    {
        StatusCode = statusCode,
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "text/event-stream; charset=utf-8",
            ["Cache-Control"] = "no-cache",
            ["Connection"] = "keep-alive"
        }
    };
}
