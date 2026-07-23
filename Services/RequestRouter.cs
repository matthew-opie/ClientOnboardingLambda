using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.Core.ResponseStreaming;
using Amazon.S3;
using ClientOnboardingLambda.Models;

namespace ClientOnboardingLambda.Services;

public static class HttpResponseFactory
{
    public static APIGatewayHttpApiV2ProxyResponse Json(
        object body,
        int statusCode,
        IDictionary<string, string>? extraHeaders = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/json"
        };

        if (extraHeaders is not null)
        {
            foreach (var pair in extraHeaders)
            {
                headers[pair.Key] = pair.Value;
            }
        }

        return new APIGatewayHttpApiV2ProxyResponse
        {
            StatusCode = statusCode,
            Body = JsonSerializer.Serialize(body),
            Headers = headers
        };
    }

    public static APIGatewayHttpApiV2ProxyResponse Error(
        string message,
        int statusCode,
        string? requestId = null,
        IDictionary<string, string>? extraHeaders = null)
    {
        var headers = extraHeaders;
        if (!string.IsNullOrWhiteSpace(requestId))
        {
            headers ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            headers["x-request-id"] = requestId;
        }

        return Json(new
        {
            success = false,
            message,
            requestId,
            timestamp = DateTime.UtcNow
        }, statusCode, headers);
    }

    public static APIGatewayHttpApiV2ProxyResponse Ok(
        ApiResponse response,
        IDictionary<string, string>? extraHeaders = null) =>
        Json(response, 200, extraHeaders);
}

public sealed class RequestRouter(
    AppConfig config,
    RagOrchestrator rag,
    DocumentIngestService ingest,
    RagasEvalService eval,
    OpenAiService openAi,
    DynamoDbRepository dynamoDb)
{
    public static RequestRouter Create(AppConfig config)
    {
        var openAi = new OpenAiService(config.OpenAiApiKey);
        var dynamoDb = new DynamoDbRepository(new AmazonDynamoDBClient(), config.DynamoDbTableName);
        var qdrant = new QdrantClient(config.QdrantUrl, config.QdrantApiKey);
        var retrieval = new HybridRetrievalService(dynamoDb, qdrant, openAi);
        var complianceTools = new ComplianceToolService(dynamoDb);
        var mcpClient = new McpClientRouter(config, complianceTools, new HttpClient());
        var tools = new McpToolExecutor(mcpClient);
        var rag = new RagOrchestrator(retrieval, tools, openAi);
        var ingest = new DocumentIngestService(config, new AmazonS3Client(), dynamoDb, qdrant, openAi);
        var evalStatusWriter = new EvalStatusWriter(dynamoDb);
        var evalService = new RagasEvalService(rag, openAi, evalStatusWriter);
        return new RequestRouter(config, rag, ingest, evalService, openAi, dynamoDb);
    }

    public async Task<APIGatewayHttpApiV2ProxyResponse> RouteAsync(
        APIGatewayHttpApiV2ProxyRequest request,
        ILambdaLogger logger,
        CancellationToken cancellationToken = default)
    {
        var log = RequestLogContext.FromRequest(request, logger, config);
        log.LogStage("request_received");

        var method = request.RequestContext.Http.Method?.ToUpperInvariant() ?? "GET";
        var path = request.RawPath?.TrimEnd('/') ?? "/";

        try
        {
            if (method == "OPTIONS")
            {
                var origin = CorsHeaders.GetOrigin(request);
                var isStreamPath = path.StartsWith("/tenants/", StringComparison.OrdinalIgnoreCase) &&
                                   path.EndsWith("/query/stream", StringComparison.OrdinalIgnoreCase);
                var headers = isStreamPath
                    ? CorsHeaders.BuildPreflight(origin, config.CorsAllowedOrigins)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                return new APIGatewayHttpApiV2ProxyResponse
                {
                    StatusCode = 204,
                    Headers = headers,
                    Body = string.Empty
                };
            }

            APIGatewayHttpApiV2ProxyResponse response = (method, path) switch
            {
                ("GET", "/health") => await HandleHealthAsync(cancellationToken, log),
                ("GET", "/tenants") => HandleTenants(log),
                ("POST", "/chat") => await HandleLegacyChatAsync(request.Body, log, cancellationToken),
                _ when method == "POST" && path.StartsWith("/admin/ingest/", StringComparison.OrdinalIgnoreCase)
                    => await HandleIngestAsync(request, path["/admin/ingest/".Length..], log, cancellationToken),
                _ when method == "POST" && path.StartsWith("/admin/eval/", StringComparison.OrdinalIgnoreCase)
                    => await HandleEvalAsync(request, path["/admin/eval/".Length..], log, cancellationToken),
                _ when method == "POST" && path.StartsWith("/tenants/", StringComparison.OrdinalIgnoreCase) &&
                       path.EndsWith("/query", StringComparison.OrdinalIgnoreCase)
                    => await HandleQueryAsync(
                        request.Body,
                        path["/tenants/".Length..^"/query".Length],
                        log,
                        cancellationToken),
                _ when method == "GET" && path.StartsWith("/tenants/", StringComparison.OrdinalIgnoreCase) &&
                       path.EndsWith("/ingest-status", StringComparison.OrdinalIgnoreCase)
                    => await HandleIngestStatusAsync(
                        path["/tenants/".Length..^"/ingest-status".Length],
                        log,
                        cancellationToken),
                _ when method == "GET" && path.StartsWith("/tenants/", StringComparison.OrdinalIgnoreCase) &&
                       path.EndsWith("/eval", StringComparison.OrdinalIgnoreCase)
                    => await HandleEvalStatusAsync(
                        path["/tenants/".Length..^"/eval".Length],
                        log,
                        cancellationToken),
                _ => HttpResponseFactory.Error($"Route not found: {method} {path}", 404, log.RequestId, log.ResponseHeaders)
            };

            log.LogStage("request_complete", durationMs: log.ElapsedMs, statusCode: response.StatusCode);
            return response;
        }
        catch (Exception ex)
        {
            log.LogStage("request_error", durationMs: log.ElapsedMs, error: ex.Message);
            logger.LogError($"Unhandled route error [{log.RequestId}]: {ex}");
            return HttpResponseFactory.Error(ex.Message, 500, log.RequestId, log.ResponseHeaders);
        }
    }

    private Task<APIGatewayHttpApiV2ProxyResponse> HandleHealthAsync(
        CancellationToken cancellationToken,
        RequestLogContext log)
    {
        _ = cancellationToken;
        return Task.FromResult(HttpResponseFactory.Json(new
        {
            success = true,
            message = "Configuration loaded.",
            requestId = log.RequestId,
            timestamp = DateTime.UtcNow,
            dynamodbTable = config.DynamoDbTableName,
            seedBucket = config.SeedBucketName,
            qdrantConfigured = !string.IsNullOrWhiteSpace(config.QdrantUrl)
        }, 200, log.ResponseHeaders));
    }

    private APIGatewayHttpApiV2ProxyResponse HandleTenants(RequestLogContext log)
    {
        var tenants = TenantRegistry.All.Select(t => new TenantListItemDto
        {
            TenantId = t.TenantId,
            DisplayId = t.DisplayId,
            Name = t.Name,
            PartitionKey = t.PartitionKey,
            VectorCollection = t.QdrantCollection
        });
        return HttpResponseFactory.Json(new { success = true, tenants }, 200, log.ResponseHeaders);
    }

    private async Task<APIGatewayHttpApiV2ProxyResponse> HandleLegacyChatAsync(
        string? body,
        RequestLogContext log,
        CancellationToken cancellationToken)
    {
        config.ValidateForQuery();

        if (string.IsNullOrWhiteSpace(body))
        {
            return HttpResponseFactory.Error("Prompt body is missing.", 400, log.RequestId, log.ResponseHeaders);
        }

        var request = JsonSerializer.Deserialize<PromptRequest>(body);
        if (string.IsNullOrWhiteSpace(request?.Prompt))
        {
            return HttpResponseFactory.Error("Prompt is empty.", 400, log.RequestId, log.ResponseHeaders);
        }

        var message = await openAi.ChatLegacyAsync(request.Prompt, cancellationToken);
        return HttpResponseFactory.Ok(new ApiResponse
        {
            Success = true,
            Message = message,
            Timestamp = DateTime.UtcNow
        }, log.ResponseHeaders);
    }

    private async Task<APIGatewayHttpApiV2ProxyResponse> HandleIngestAsync(
        APIGatewayHttpApiV2ProxyRequest request,
        string tenantId,
        RequestLogContext log,
        CancellationToken cancellationToken)
    {
        var apiKey = GetHeader(request, "x-api-key");
        if (string.IsNullOrWhiteSpace(config.AdminApiKey) ||
            !string.Equals(apiKey, config.AdminApiKey, StringComparison.Ordinal))
        {
            return HttpResponseFactory.Error("Unauthorized ingest request.", 401, log.RequestId, log.ResponseHeaders);
        }

        var tenant = TenantRegistry.Find(tenantId);
        if (tenant is null)
        {
            return HttpResponseFactory.Error($"Unknown tenant: {tenantId}", 404, log.RequestId, log.ResponseHeaders);
        }

        log.LogStage("ingest_started", tenantId: tenant.TenantId);
        var ingestStarted = Environment.TickCount64;
        var summary = await ingest.IngestTenantAsync(tenant, cancellationToken);
        log.LogStage("ingest_complete", durationMs: Environment.TickCount64 - ingestStarted, tenantId: tenant.TenantId);

        return HttpResponseFactory.Ok(new ApiResponse
        {
            Success = true,
            Message = summary.Message,
            Timestamp = DateTime.UtcNow
        }, log.ResponseHeaders);
    }

    private async Task<APIGatewayHttpApiV2ProxyResponse> HandleIngestStatusAsync(
        string tenantId,
        RequestLogContext log,
        CancellationToken cancellationToken)
    {
        var tenant = TenantRegistry.Find(tenantId);
        if (tenant is null)
        {
            return HttpResponseFactory.Error($"Unknown tenant: {tenantId}", 404, log.RequestId, log.ResponseHeaders);
        }

        var ingestStatus = await dynamoDb.GetIngestStatusAsync(tenant.PartitionKey, cancellationToken);

        return HttpResponseFactory.Json(new
        {
            success = true,
            tenantId = tenant.TenantId,
            status = ingestStatus?.Status ?? "unknown",
            startedAt = ingestStatus?.StartedAt,
            completedAt = ingestStatus?.CompletedAt,
            pdfCount = ingestStatus?.PdfCount,
            chunkCount = ingestStatus?.ChunkCount,
            error = ingestStatus?.Error
        }, 200, log.ResponseHeaders);
    }

    private async Task<APIGatewayHttpApiV2ProxyResponse> HandleEvalAsync(
        APIGatewayHttpApiV2ProxyRequest request,
        string tenantId,
        RequestLogContext log,
        CancellationToken cancellationToken)
    {
        var apiKey = GetHeader(request, "x-api-key");
        if (string.IsNullOrWhiteSpace(config.AdminApiKey) ||
            !string.Equals(apiKey, config.AdminApiKey, StringComparison.Ordinal))
        {
            return HttpResponseFactory.Error("Unauthorized eval request.", 401, log.RequestId, log.ResponseHeaders);
        }

        var tenant = TenantRegistry.Find(tenantId);
        if (tenant is null)
        {
            return HttpResponseFactory.Error($"Unknown tenant: {tenantId}", 404, log.RequestId, log.ResponseHeaders);
        }

        log.LogStage("eval_started", tenantId: tenant.TenantId);
        var evalStarted = Environment.TickCount64;
        var summary = await eval.RunEvalAsync(tenant, cancellationToken);
        log.LogStage("eval_complete", durationMs: Environment.TickCount64 - evalStarted, tenantId: tenant.TenantId);

        return HttpResponseFactory.Json(new
        {
            success = true,
            tenantId = tenant.TenantId,
            faithfulness = summary.Faithfulness,
            questionCount = summary.QuestionCount,
            results = summary.Results.Select(r => new
            {
                question = r.Question,
                faithfulness = r.Faithfulness,
                expectedContainsPassed = r.ExpectedContainsPassed
            }),
            message = summary.Message,
            timestamp = DateTime.UtcNow
        }, 200, log.ResponseHeaders);
    }

    private async Task<APIGatewayHttpApiV2ProxyResponse> HandleEvalStatusAsync(
        string tenantId,
        RequestLogContext log,
        CancellationToken cancellationToken)
    {
        var tenant = TenantRegistry.Find(tenantId);
        if (tenant is null)
        {
            return HttpResponseFactory.Error($"Unknown tenant: {tenantId}", 404, log.RequestId, log.ResponseHeaders);
        }

        var evalStatus = await dynamoDb.GetEvalStatusAsync(tenant.PartitionKey, cancellationToken);

        return HttpResponseFactory.Json(new
        {
            success = true,
            tenantId = tenant.TenantId,
            status = evalStatus?.Status ?? "unknown",
            faithfulness = evalStatus?.Faithfulness,
            lastEvalRunAt = evalStatus?.RunAt,
            questionCount = evalStatus?.QuestionCount,
            error = evalStatus?.Error
        }, 200, log.ResponseHeaders);
    }

    private async Task<APIGatewayHttpApiV2ProxyResponse> HandleQueryAsync(
        string? body,
        string tenantId,
        RequestLogContext log,
        CancellationToken cancellationToken)
    {
        config.ValidateForQuery();

        if (string.IsNullOrWhiteSpace(body))
        {
            return HttpResponseFactory.Error("Query body is missing.", 400, log.RequestId, log.ResponseHeaders);
        }

        var request = JsonSerializer.Deserialize<QueryRequest>(body);
        if (string.IsNullOrWhiteSpace(request?.Query))
        {
            return HttpResponseFactory.Error("Query is empty.", 400, log.RequestId, log.ResponseHeaders);
        }

        var tenant = TenantRegistry.Find(tenantId);
        if (tenant is null)
        {
            return HttpResponseFactory.Error($"Unknown tenant: {tenantId}", 404, log.RequestId, log.ResponseHeaders);
        }

        var response = await rag.QueryAsync(tenant, request.Query, log, cancellationToken);
        log.LogStage(
            "query_complete",
            tenantId: tenant.TenantId,
            retrievedChunks: response.Telemetry?.RetrievedChunks);

        return HttpResponseFactory.Ok(response, log.ResponseHeaders);
    }

    public async Task RouteStreamAsync(
        APIGatewayHttpApiV2ProxyRequest request,
        ILambdaContext lambdaContext,
        CancellationToken cancellationToken = default)
    {
        var log = RequestLogContext.FromRequest(request, lambdaContext.Logger, config);
        var path = request.RawPath?.TrimEnd('/') ?? "/";
        var tenantId = path["/tenants/".Length..^"/query/stream".Length];

        await using var responseStream = LambdaResponseStreamFactory.CreateHttpStream(
            SseStreamWriter.CreatePrelude(log.StreamResponseHeaders));
        var sse = new SseStreamWriter(responseStream);

        try
        {
            config.ValidateForQuery();

            if (string.IsNullOrWhiteSpace(request.Body))
            {
                await sse.WriteEventAsync("error", new { message = "Query body is missing." }, cancellationToken);
                return;
            }

            var queryRequest = JsonSerializer.Deserialize<QueryRequest>(request.Body);
            if (string.IsNullOrWhiteSpace(queryRequest?.Query))
            {
                await sse.WriteEventAsync("error", new { message = "Query is empty." }, cancellationToken);
                return;
            }

            var tenant = TenantRegistry.Find(tenantId);
            if (tenant is null)
            {
                await sse.WriteEventAsync("error", new { message = $"Unknown tenant: {tenantId}" }, cancellationToken);
                return;
            }

            log.LogStage("stream_query_started", tenantId: tenant.TenantId);
            await rag.QueryStreamAsync(tenant, queryRequest.Query, sse, log, cancellationToken);
            log.LogStage("stream_query_complete", durationMs: log.ElapsedMs, tenantId: tenant.TenantId);
        }
        catch (Exception ex)
        {
            lambdaContext.Logger.LogError($"Stream query failed [{log.RequestId}]: {ex}");
            await sse.WriteEventAsync("error", new { message = ex.Message, requestId = log.RequestId }, cancellationToken);
        }
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
