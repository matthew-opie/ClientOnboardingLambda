using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.S3;
using ClientOnboardingLambda.Models;

namespace ClientOnboardingLambda.Services;

public static class HttpResponseFactory
{
    // CORS is handled exclusively by the Lambda Function URL configuration.
    public static APIGatewayHttpApiV2ProxyResponse Json(object body, int statusCode)
    {
        return new APIGatewayHttpApiV2ProxyResponse
        {
            StatusCode = statusCode,
            Body = JsonSerializer.Serialize(body),
            Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" }
        };
    }

    public static APIGatewayHttpApiV2ProxyResponse Error(string message, int statusCode) =>
        Json(new ApiResponse
        {
            Success = false,
            Message = message,
            Timestamp = DateTime.UtcNow
        }, statusCode);

    public static APIGatewayHttpApiV2ProxyResponse Ok(ApiResponse response) => Json(response, 200);
}

public sealed class RequestRouter
{
    private readonly AppConfig _config;
    private readonly RagOrchestrator _rag;
    private readonly DocumentIngestService _ingest;
    private readonly OpenAiService _openAi;
    private readonly DynamoDbRepository _dynamoDb;
    private readonly QdrantClient _qdrant;

    public RequestRouter(
        AppConfig config,
        RagOrchestrator rag,
        DocumentIngestService ingest,
        OpenAiService openAi,
        DynamoDbRepository dynamoDb,
        QdrantClient qdrant)
    {
        _config = config;
        _rag = rag;
        _ingest = ingest;
        _openAi = openAi;
        _dynamoDb = dynamoDb;
        _qdrant = qdrant;
    }

    public static RequestRouter Create(AppConfig config)
    {
        var openAi = new OpenAiService(config.OpenAiApiKey);
        var dynamoDb = new DynamoDbRepository(new AmazonDynamoDBClient(), config.DynamoDbTableName);
        var qdrant = new QdrantClient(config.QdrantUrl, config.QdrantApiKey);
        var retrieval = new HybridRetrievalService(dynamoDb, qdrant, openAi);
        var tools = new McpToolExecutor(dynamoDb);
        var rag = new RagOrchestrator(retrieval, tools, openAi);
        var ingest = new DocumentIngestService(config, new AmazonS3Client(), dynamoDb, qdrant, openAi);
        return new RequestRouter(config, rag, ingest, openAi, dynamoDb, qdrant);
    }

    public async Task<APIGatewayHttpApiV2ProxyResponse> RouteAsync(
        APIGatewayHttpApiV2ProxyRequest request,
        ILambdaLogger logger,
        CancellationToken cancellationToken = default)
    {
        var method = request.RequestContext.Http.Method?.ToUpperInvariant() ?? "GET";
        var path = request.RawPath?.TrimEnd('/') ?? "/";

        try
        {
            if (method == "GET" && path.Equals("/health", StringComparison.OrdinalIgnoreCase))
            {
                return await HandleHealthAsync(cancellationToken);
            }

            if (method == "GET" && path.Equals("/tenants", StringComparison.OrdinalIgnoreCase))
            {
                var tenants = TenantRegistry.All.Select(t => new TenantListItemDto
                {
                    TenantId = t.TenantId,
                    DisplayId = t.DisplayId,
                    Name = t.Name,
                    PartitionKey = t.PartitionKey,
                    VectorCollection = t.QdrantCollection
                });
                return HttpResponseFactory.Json(new { success = true, tenants }, 200);
            }

            if (method == "POST" && path.Equals("/chat", StringComparison.OrdinalIgnoreCase))
            {
                return await HandleLegacyChatAsync(request.Body, cancellationToken);
            }

            if (method == "POST" && path.StartsWith("/admin/ingest/", StringComparison.OrdinalIgnoreCase))
            {
                var tenantId = path["/admin/ingest/".Length..];
                return await HandleIngestAsync(request, tenantId, cancellationToken);
            }

            if (method == "POST" && path.StartsWith("/tenants/", StringComparison.OrdinalIgnoreCase) &&
                path.EndsWith("/query", StringComparison.OrdinalIgnoreCase))
            {
                var tenantPart = path["/tenants/".Length..^"/query".Length];
                return await HandleQueryAsync(request.Body, tenantPart, cancellationToken);
            }

            return HttpResponseFactory.Error($"Route not found: {method} {path}", 404);
        }
        catch (Exception ex)
        {
            logger.LogError($"Unhandled route error: {ex}");
            return HttpResponseFactory.Error(ex.Message, 500);
        }
    }

    private Task<APIGatewayHttpApiV2ProxyResponse> HandleHealthAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.FromResult(HttpResponseFactory.Json(new
        {
            success = true,
            message = "Configuration loaded.",
            timestamp = DateTime.UtcNow,
            dynamodbTable = _config.DynamoDbTableName,
            seedBucket = _config.SeedBucketName,
            qdrantConfigured = !string.IsNullOrWhiteSpace(_config.QdrantUrl)
        }, 200));
    }

    private async Task<APIGatewayHttpApiV2ProxyResponse> HandleLegacyChatAsync(
        string? body,
        CancellationToken cancellationToken)
    {
        _config.ValidateForQuery();

        if (string.IsNullOrWhiteSpace(body))
        {
            return HttpResponseFactory.Error("Prompt body is missing.", 400);
        }

        var request = JsonSerializer.Deserialize<PromptRequest>(body);
        if (string.IsNullOrWhiteSpace(request?.Prompt))
        {
            return HttpResponseFactory.Error("Prompt is empty.", 400);
        }

        var message = await _openAi.ChatLegacyAsync(request.Prompt, cancellationToken);
        return HttpResponseFactory.Ok(new ApiResponse
        {
            Success = true,
            Message = message,
            Timestamp = DateTime.UtcNow
        });
    }

    private async Task<APIGatewayHttpApiV2ProxyResponse> HandleIngestAsync(
        APIGatewayHttpApiV2ProxyRequest request,
        string tenantId,
        CancellationToken cancellationToken)
    {
        var apiKey = GetHeader(request, "x-api-key");
        if (string.IsNullOrWhiteSpace(_config.AdminApiKey) ||
            !string.Equals(apiKey, _config.AdminApiKey, StringComparison.Ordinal))
        {
            return HttpResponseFactory.Error("Unauthorized ingest request.", 401);
        }

        var tenant = TenantRegistry.Find(tenantId);
        if (tenant is null)
        {
            return HttpResponseFactory.Error($"Unknown tenant: {tenantId}", 404);
        }

        var summary = await _ingest.IngestTenantAsync(tenant, cancellationToken);
        return HttpResponseFactory.Ok(new ApiResponse
        {
            Success = true,
            Message = summary,
            Timestamp = DateTime.UtcNow
        });
    }

    private async Task<APIGatewayHttpApiV2ProxyResponse> HandleQueryAsync(
        string? body,
        string tenantId,
        CancellationToken cancellationToken)
    {
        _config.ValidateForQuery();

        if (string.IsNullOrWhiteSpace(body))
        {
            return HttpResponseFactory.Error("Query body is missing.", 400);
        }

        var request = JsonSerializer.Deserialize<QueryRequest>(body);
        if (string.IsNullOrWhiteSpace(request?.Query))
        {
            return HttpResponseFactory.Error("Query is empty.", 400);
        }

        var tenant = TenantRegistry.Find(tenantId);
        if (tenant is null)
        {
            return HttpResponseFactory.Error($"Unknown tenant: {tenantId}", 404);
        }

        var response = await _rag.QueryAsync(tenant, request.Query, cancellationToken);
        return HttpResponseFactory.Ok(response);
    }

    private static string? GetHeader(APIGatewayHttpApiV2ProxyRequest request, string name)
    {
        if (request.Headers is null)
        {
            return null;
        }

        return request.Headers.FirstOrDefault(h =>
            string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
    }
}
