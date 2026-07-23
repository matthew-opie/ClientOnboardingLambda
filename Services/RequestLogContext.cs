using System.Diagnostics;
using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;

namespace ClientOnboardingLambda.Services;

/// <summary>
/// Structured JSON logging for CloudWatch Logs Insights.
/// </summary>
public sealed class RequestLogContext
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public RequestLogContext(
        ILambdaLogger logger,
        string requestId,
        string method,
        string path,
        string? origin,
        string corsAllowedOrigins)
    {
        Logger = logger;
        RequestId = requestId;
        Route = $"{method} {path}";
        _origin = origin;
        _corsAllowedOrigins = corsAllowedOrigins;
        _stopwatch = Stopwatch.StartNew();
    }

    public string RequestId { get; }
    public string Route { get; }
    public ILambdaLogger Logger { get; }

    private readonly Stopwatch _stopwatch;
    private readonly string? _origin;
    private readonly string _corsAllowedOrigins;

    public static RequestLogContext FromRequest(
        APIGatewayHttpApiV2ProxyRequest request,
        ILambdaLogger logger,
        AppConfig config)
    {
        var method = request.RequestContext.Http.Method?.ToUpperInvariant() ?? "GET";
        var path = request.RawPath?.TrimEnd('/') ?? "/";
        var requestId = ResolveRequestId(request);
        return new RequestLogContext(
            logger,
            requestId,
            method,
            path,
            CorsHeaders.GetOrigin(request),
            config.CorsAllowedOrigins);
    }

    public void LogStage(
        string stage,
        long? durationMs = null,
        string? tenantId = null,
        int? retrievedChunks = null,
        int? statusCode = null,
        string? error = null)
    {
        var entry = new Dictionary<string, object?>
        {
            ["requestId"] = RequestId,
            ["route"] = Route,
            ["stage"] = stage
        };

        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            entry["tenantId"] = tenantId;
        }

        if (durationMs.HasValue)
        {
            entry["durationMs"] = durationMs.Value;
        }

        if (retrievedChunks.HasValue)
        {
            entry["retrievedChunks"] = retrievedChunks.Value;
        }

        if (statusCode.HasValue)
        {
            entry["statusCode"] = statusCode.Value;
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            entry["error"] = error;
        }

        Logger.LogInformation(JsonSerializer.Serialize(entry, JsonOptions));
    }

    public long ElapsedMs => _stopwatch.ElapsedMilliseconds;

    public Dictionary<string, string> ResponseHeaders
    {
        get
        {
            var headers = CorsHeaders.Build(_origin, _corsAllowedOrigins);
            headers["x-request-id"] = RequestId;
            return headers;
        }
    }

    private static string ResolveRequestId(APIGatewayHttpApiV2ProxyRequest request)
    {
        if (request.Headers is not null)
        {
            foreach (var header in request.Headers)
            {
                if (string.Equals(header.Key, "x-request-id", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(header.Value))
                {
                    return header.Value.Trim();
                }
            }
        }

        return Guid.NewGuid().ToString("N");
    }
}
