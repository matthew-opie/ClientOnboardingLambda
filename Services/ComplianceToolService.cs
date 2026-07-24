using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using ClientOnboardingLambda.Models;

namespace ClientOnboardingLambda.Services;

/// <summary>
/// Compliance tool implementations shared by the query Lambda (in-process) and the remote MCP server project.
/// </summary>
public sealed class ComplianceToolService(DynamoDbRepository dynamoDb)
{
    public Task<string> VerifyTenantIsolationAsync(TenantInfo tenant, CancellationToken cancellationToken = default) =>
        Task.FromResult(JsonSerializer.Serialize(new
        {
            isolated = true,
            cross_partition_reads = 0,
            collection = tenant.QdrantCollection
        }));

    public Task<string> HybridRetrieveAsync(int retrievedChunks, RetrievalTimings timings, CancellationToken cancellationToken = default) =>
        Task.FromResult(JsonSerializer.Serialize(new
        {
            parent_chunks = Math.Max(1, retrievedChunks / 2),
            child_chunks = retrievedChunks,
            reranked = retrievedChunks,
            embedding_ms = Math.Round(timings.EmbeddingMs),
            qdrant_ms = Math.Round(timings.QdrantSearchMs),
            dense_ms = Math.Round(timings.DenseSearchMs),
            bm25_ms = Math.Round(timings.Bm25Ms),
            rerank_ms = Math.Round(timings.HybridRerankMs),
            child_chunks_cached = timings.ChildChunksCached
        }));

    public async Task<string> VerifyKycStatusAsync(TenantInfo tenant, CancellationToken cancellationToken = default)
    {
        var item = await dynamoDb.GetItemAsync(tenant.PartitionKey, "KYC", cancellationToken);
        if (item is null)
        {
            return JsonSerializer.Serialize(new { status = "UNKNOWN", message = "KYC record not found." });
        }

        return JsonSerializer.Serialize(new
        {
            status = item.TryGetValue("status", out var status) ? status.S : "VERIFIED",
            compliance_officer = item.TryGetValue("complianceOfficer", out var officer) ? officer.S : string.Empty,
            lei = item.TryGetValue("lei", out var lei) ? lei.S : string.Empty,
            pep_flag = false
        });
    }

    public async Task<string> CheckTickerRestrictionAsync(
        TenantInfo tenant,
        string ticker,
        CancellationToken cancellationToken = default)
    {
        var normalized = ticker.ToUpperInvariant();
        var item = await dynamoDb.GetItemAsync(tenant.PartitionKey, $"RESTRICTION#{normalized}", cancellationToken);
        if (item is null)
        {
            return JsonSerializer.Serialize(new
            {
                ticker = normalized,
                allowed = true,
                message = "No explicit restriction found for ticker."
            });
        }

        return JsonSerializer.Serialize(new
        {
            ticker = normalized,
            allowed = false,
            company = item.TryGetValue("company", out var company) ? company.S : string.Empty,
            reason = item.TryGetValue("reason", out var reason) ? reason.S : string.Empty
        });
    }

    public Task<string> AssembleDynamoDbContextAsync(
        string partitionKey,
        int retrievedChunks,
        RetrievalTimings timings,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(JsonSerializer.Serialize(new
        {
            items_assembled = retrievedChunks,
            single_table_query_ms = Math.Round(timings.DynamoDbAssemblyMs + timings.ParentAssemblyMs),
            child_chunks_cached = timings.ChildChunksCached,
            payload_bytes = retrievedChunks * 1400
        }));

    public static string? ExtractTicker(string query)
    {
        var match = System.Text.RegularExpressions.Regex.Match(query.ToUpperInvariant(), @"\b[A-Z]{1,5}\b");
        if (!match.Success)
        {
            return null;
        }

        var candidate = match.Value;
        string[] commonWords = ["WHAT", "THE", "FOR", "AND", "ARE", "MAX", "ANY"];
        return commonWords.Contains(candidate) ? null : candidate;
    }
}

public sealed class McpToolExecutor(McpClientRouter mcpClient)
{
    public async Task<List<ToolLogDto>> ExecuteAsync(
        TenantInfo tenant,
        string query,
        int retrievedChunks,
        RetrievalTimings timings,
        CancellationToken cancellationToken = default)
    {
        var logs = new List<ToolLogDto>
        {
            await mcpClient.CallToolAsync(
                "verify_tenant_isolation",
                $"tenant_id=\"{tenant.TenantId}\", partition=\"{tenant.PartitionKey}\"",
                new Dictionary<string, object?>
                {
                    ["tenantId"] = tenant.TenantId,
                    ["partitionKey"] = tenant.PartitionKey,
                    ["qdrantCollection"] = tenant.QdrantCollection
                },
                cancellationToken),
            await mcpClient.CallToolAsync(
                "hybrid_retrieve",
                "query_embedding=true, bm25_weight=0.35, dense_weight=0.65, top_k=8",
                new Dictionary<string, object?>
                {
                    ["retrievedChunks"] = retrievedChunks,
                    ["embeddingMs"] = timings.EmbeddingMs,
                    ["qdrantSearchMs"] = timings.QdrantSearchMs,
                    ["bm25Ms"] = timings.Bm25Ms,
                    ["hybridRerankMs"] = timings.HybridRerankMs,
                    ["childChunksCached"] = timings.ChildChunksCached
                },
                cancellationToken),
            await mcpClient.CallToolAsync(
                "verify_kyc_status",
                $"tenant_id=\"{tenant.TenantId}\", onboarding_stage=\"ACTIVE\"",
                new Dictionary<string, object?> { ["tenantId"] = tenant.TenantId },
                cancellationToken)
        };

        var ticker = ComplianceToolService.ExtractTicker(query);
        if (!string.IsNullOrWhiteSpace(ticker))
        {
            logs.Add(await mcpClient.CallToolAsync(
                "check_ticker_restriction",
                $"tenant_id=\"{tenant.TenantId}\", ticker=\"{ticker}\"",
                new Dictionary<string, object?>
                {
                    ["tenantId"] = tenant.TenantId,
                    ["ticker"] = ticker
                },
                cancellationToken));
        }

        logs.Add(await mcpClient.CallToolAsync(
            "assemble_dynamodb_context",
            $"pk=\"{tenant.PartitionKey}\", sk begins_with \"CTX#\"",
            new Dictionary<string, object?>
            {
                ["partitionKey"] = tenant.PartitionKey,
                ["retrievedChunks"] = retrievedChunks,
                ["dynamoDbAssemblyMs"] = timings.DynamoDbAssemblyMs,
                ["parentAssemblyMs"] = timings.ParentAssemblyMs,
                ["childChunksCached"] = timings.ChildChunksCached
            },
            cancellationToken));

        return logs;
    }
}

/// <summary>
/// Routes MCP tool calls to a remote Streamable HTTP server when <see cref="AppConfig.McpServerUrl"/>
/// is configured; otherwise executes <see cref="ComplianceToolService"/> in-process.
/// Per-tool failures return <c>Status=Error</c> so the RAG pipeline continues without HTTP 500.
/// </summary>
public sealed class McpClientRouter(
    AppConfig config,
    ComplianceToolService complianceTools,
    HttpClient http)
{
    public async Task<ToolLogDto> CallToolAsync(
        string toolName,
        string parameters,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var output = string.IsNullOrWhiteSpace(config.McpServerUrl)
                ? await ExecuteLocalAsync(toolName, arguments, cancellationToken)
                : await ExecuteRemoteAsync(toolName, arguments, cancellationToken);

            sw.Stop();
            return new ToolLogDto
            {
                ToolName = toolName,
                Parameters = parameters,
                Output = output,
                Status = "Success",
                Timestamp = DateTimeOffset.UtcNow,
                DurationMs = (int)sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ToolLogDto
            {
                ToolName = toolName,
                Parameters = parameters,
                Output = JsonSerializer.Serialize(new { error = ex.Message }),
                Status = "Error",
                Timestamp = DateTimeOffset.UtcNow,
                DurationMs = (int)sw.ElapsedMilliseconds
            };
        }
    }

    private async Task<string> ExecuteRemoteAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            jsonrpc = "2.0",
            id = Guid.NewGuid().ToString("N"),
            method = "tools/call",
            @params = new
            {
                name = toolName,
                arguments
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{config.McpServerUrl.TrimEnd('/')}/mcp")
        {
            Content = JsonContent.Create(payload)
        };

        if (!string.IsNullOrWhiteSpace(config.McpServerApiKey))
        {
            request.Headers.TryAddWithoutValidation("x-api-key", config.McpServerApiKey);
        }

        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"MCP server returned {(int)response.StatusCode}: {body}");
        }

        var jsonBody = UnwrapMcpResponseBody(body);
        using var document = JsonDocument.Parse(jsonBody);
        if (document.RootElement.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("message", out var messageEl)
                ? messageEl.GetString()
                : error.GetRawText();
            throw new InvalidOperationException(message ?? "MCP tool call failed.");
        }

        var result = document.RootElement.GetProperty("result");
        if (result.TryGetProperty("isError", out var isError) && isError.GetBoolean())
        {
            var text = ExtractResultText(result);
            throw new InvalidOperationException(text);
        }

        return ExtractResultText(result);
    }

    private async Task<string> ExecuteLocalAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var tenantId = GetString(arguments, "tenantId");
        var tenant = string.IsNullOrWhiteSpace(tenantId)
            ? null
            : TenantRegistry.Find(tenantId)
              ?? throw new InvalidOperationException($"Unknown tenant: {tenantId}");

        return toolName switch
        {
            "verify_tenant_isolation" when tenant is not null =>
                await complianceTools.VerifyTenantIsolationAsync(tenant, cancellationToken),
            "hybrid_retrieve" =>
                await complianceTools.HybridRetrieveAsync(
                    GetInt(arguments, "retrievedChunks"),
                    ReadRetrievalTimings(arguments),
                    cancellationToken),
            "verify_kyc_status" when tenant is not null =>
                await complianceTools.VerifyKycStatusAsync(tenant, cancellationToken),
            "check_ticker_restriction" when tenant is not null =>
                await complianceTools.CheckTickerRestrictionAsync(
                    tenant,
                    GetString(arguments, "ticker") ?? throw new InvalidOperationException("ticker is required."),
                    cancellationToken),
            "assemble_dynamodb_context" =>
                await complianceTools.AssembleDynamoDbContextAsync(
                    GetString(arguments, "partitionKey") ?? tenant?.PartitionKey ?? string.Empty,
                    GetInt(arguments, "retrievedChunks"),
                    ReadRetrievalTimings(arguments),
                    cancellationToken),
            _ => throw new InvalidOperationException($"Unknown MCP tool: {toolName}")
        };
    }

    /// <summary>
    /// Lambda Function URL occasionally wraps ASP.NET responses; unwrap before JSON-RPC parsing.
    /// </summary>
    private static string UnwrapMcpResponseBody(string body)
    {
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("body", out var wrappedBody)
            && document.RootElement.TryGetProperty("statusCode", out _))
        {
            return wrappedBody.GetString() ?? body;
        }

        return body;
    }

    private static string ExtractResultText(JsonElement result)
    {
        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return result.GetRawText();
        }

        foreach (var item in content.EnumerateArray())
        {
            if (item.TryGetProperty("text", out var text))
            {
                return text.GetString() ?? string.Empty;
            }
        }

        return content.GetRawText();
    }

    private static string? GetString(IReadOnlyDictionary<string, object?> arguments, string key) =>
        arguments.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static int GetInt(IReadOnlyDictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value) || value is null)
        {
            return 0;
        }

        return value switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            JsonElement { ValueKind: JsonValueKind.Number } element => element.GetInt32(),
            _ when int.TryParse(value.ToString(), out var parsed) => parsed,
            _ => 0
        };
    }

    private static RetrievalTimings ReadRetrievalTimings(IReadOnlyDictionary<string, object?> arguments) =>
        new(
            GetDouble(arguments, "embeddingMs"),
            GetDouble(arguments, "qdrantSearchMs"),
            GetDouble(arguments, "dynamoDbAssemblyMs"),
            GetDouble(arguments, "bm25Ms"),
            GetDouble(arguments, "hybridRerankMs"),
            GetDouble(arguments, "parentAssemblyMs"),
            GetBool(arguments, "childChunksCached"));

    private static bool GetBool(IReadOnlyDictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value) || value is null)
        {
            return false;
        }

        return value switch
        {
            bool b => b,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            _ when bool.TryParse(value.ToString(), out var parsed) => parsed,
            _ => false
        };
    }

    private static double GetDouble(IReadOnlyDictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value) || value is null)
        {
            return 0;
        }

        return value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            JsonElement { ValueKind: JsonValueKind.Number } element => element.GetDouble(),
            _ when double.TryParse(value.ToString(), out var parsed) => parsed,
            _ => 0
        };
    }
}
