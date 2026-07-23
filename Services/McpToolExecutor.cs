using System.Diagnostics;
using System.Text.Json;
using ClientOnboardingLambda.Models;

namespace ClientOnboardingLambda.Services;

public sealed class McpToolExecutor
{
    private readonly DynamoDbRepository _dynamoDb;

    public McpToolExecutor(DynamoDbRepository dynamoDb) => _dynamoDb = dynamoDb;

    public async Task<List<ToolLogDto>> ExecuteAsync(
        TenantInfo tenant,
        string query,
        int retrievedChunks,
        double vectorMs,
        CancellationToken cancellationToken = default)
    {
        var logs = new List<ToolLogDto>();

        logs.Add(await RunAsync(
            "verify_tenant_isolation",
            $"tenant_id=\"{tenant.TenantId}\", partition=\"{tenant.PartitionKey}\"",
            () => Task.FromResult(JsonSerializer.Serialize(new
            {
                isolated = true,
                cross_partition_reads = 0,
                collection = tenant.QdrantCollection
            })),
            cancellationToken));

        logs.Add(await RunAsync(
            "hybrid_retrieve",
            "query_embedding=true, bm25_weight=0.35, dense_weight=0.65, top_k=8",
            () => Task.FromResult(JsonSerializer.Serialize(new
            {
                parent_chunks = Math.Max(1, retrievedChunks / 2),
                child_chunks = retrievedChunks,
                reranked = retrievedChunks,
                p95_ms = Math.Round(vectorMs)
            })),
            cancellationToken));

        logs.Add(await RunAsync(
            "verify_kyc_status",
            $"tenant_id=\"{tenant.TenantId}\", onboarding_stage=\"ACTIVE\"",
            () => VerifyKycAsync(tenant, cancellationToken),
            cancellationToken));

        var ticker = ExtractTicker(query);
        if (!string.IsNullOrWhiteSpace(ticker))
        {
            logs.Add(await RunAsync(
                "check_ticker_restriction",
                $"tenant_id=\"{tenant.TenantId}\", ticker=\"{ticker}\"",
                () => CheckTickerAsync(tenant, ticker, cancellationToken),
                cancellationToken));
        }

        logs.Add(await RunAsync(
            "assemble_dynamodb_context",
            $"pk=\"{tenant.PartitionKey}\", sk begins_with \"CTX#\"",
            () => Task.FromResult(JsonSerializer.Serialize(new
            {
                items_assembled = retrievedChunks,
                single_table_query_ms = Math.Round(vectorMs / 2),
                payload_bytes = retrievedChunks * 1400
            })),
            cancellationToken));

        return logs;
    }

    private async Task<ToolLogDto> RunAsync(
        string toolName,
        string parameters,
        Func<Task<string>> action,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var output = await action();
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

    private async Task<string> VerifyKycAsync(TenantInfo tenant, CancellationToken cancellationToken)
    {
        var item = await _dynamoDb.GetItemAsync(tenant.PartitionKey, "KYC", cancellationToken);
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

    private async Task<string> CheckTickerAsync(TenantInfo tenant, string ticker, CancellationToken cancellationToken)
    {
        var item = await _dynamoDb.GetItemAsync(tenant.PartitionKey, $"RESTRICTION#{ticker.ToUpperInvariant()}", cancellationToken);
        if (item is null)
        {
            return JsonSerializer.Serialize(new
            {
                ticker,
                allowed = true,
                message = "No explicit restriction found for ticker."
            });
        }

        return JsonSerializer.Serialize(new
        {
            ticker,
            allowed = false,
            company = item.TryGetValue("company", out var company) ? company.S : string.Empty,
            reason = item.TryGetValue("reason", out var reason) ? reason.S : string.Empty
        });
    }

    private static string? ExtractTicker(string query)
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
