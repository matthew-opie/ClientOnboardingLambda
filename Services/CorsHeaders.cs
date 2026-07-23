using Amazon.Lambda.APIGatewayEvents;

namespace ClientOnboardingLambda.Services;

public static class CorsHeaders
{
    public const string AllowedMethods = "GET, POST, OPTIONS";
    public const string AllowedHeaders = "Content-Type, x-api-key, x-request-id";

    public static string? GetOrigin(APIGatewayHttpApiV2ProxyRequest request)
    {
        if (request.Headers is null)
        {
            return null;
        }

        return request.Headers.FirstOrDefault(h =>
            string.Equals(h.Key, "Origin", StringComparison.OrdinalIgnoreCase)).Value;
    }

    public static Dictionary<string, string> Build(string? origin, string allowedOrigins)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var allowedOrigin = ResolveAllowedOrigin(origin, allowedOrigins);

        if (allowedOrigin is null)
        {
            return headers;
        }

        headers["Access-Control-Allow-Origin"] = allowedOrigin;

        if (!string.Equals(allowedOrigin, "*", StringComparison.Ordinal))
        {
            headers["Vary"] = "Origin";
        }

        return headers;
    }

    public static Dictionary<string, string> BuildPreflight(string? origin, string allowedOrigins)
    {
        var headers = Build(origin, allowedOrigins);
        headers["Access-Control-Allow-Methods"] = AllowedMethods;
        headers["Access-Control-Allow-Headers"] = AllowedHeaders;
        headers["Access-Control-Max-Age"] = "86400";
        return headers;
    }

    public static void ApplyTo(IDictionary<string, string> headers, string? origin, string allowedOrigins)
    {
        foreach (var pair in Build(origin, allowedOrigins))
        {
            headers[pair.Key] = pair.Value;
        }
    }

    private static string? ResolveAllowedOrigin(string? origin, string allowedOrigins)
    {
        if (string.IsNullOrWhiteSpace(allowedOrigins))
        {
            return null;
        }

        if (allowedOrigins == "*")
        {
            return "*";
        }

        if (string.IsNullOrWhiteSpace(origin))
        {
            return null;
        }

        var allowed = allowedOrigins
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return allowed.Any(candidate =>
            string.Equals(candidate, origin, StringComparison.OrdinalIgnoreCase))
            ? origin
            : null;
    }
}
