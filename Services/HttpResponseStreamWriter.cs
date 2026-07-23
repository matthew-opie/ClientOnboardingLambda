using System.Net;
using System.Text;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core.ResponseStreaming;

namespace ClientOnboardingLambda.Services;

public static class HttpResponseStreamWriter
{
    public static async Task WriteAsync(
        APIGatewayHttpApiV2ProxyResponse response,
        CancellationToken cancellationToken = default)
    {
        var prelude = new HttpResponseStreamPrelude
        {
            StatusCode = (HttpStatusCode)response.StatusCode,
            Headers = response.Headers?.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };

        if (!prelude.Headers.ContainsKey("Content-Type"))
        {
            prelude.Headers["Content-Type"] = "application/json";
        }

        await using var stream = LambdaResponseStreamFactory.CreateHttpStream(prelude);
        if (!string.IsNullOrEmpty(response.Body))
        {
            var bytes = Encoding.UTF8.GetBytes(response.Body);
            await stream.WriteAsync(bytes, cancellationToken);
        }
    }
}
