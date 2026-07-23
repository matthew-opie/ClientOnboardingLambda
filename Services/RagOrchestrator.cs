using ClientOnboardingLambda.Models;

namespace ClientOnboardingLambda.Services;

public sealed class RagOrchestrator(HybridRetrievalService retrieval, McpToolExecutor tools, OpenAiService openAi)
{
    public async Task<ApiResponse> QueryAsync(
        TenantInfo tenant,
        string query,
        RequestLogContext? log = null,
        CancellationToken cancellationToken = default)
    {
        var retrievalStarted = Environment.TickCount64;
        var (chunks, vectorMs, dynamoMs, rerankMs, retrievedCount) =
            await retrieval.RetrieveAsync(tenant, query, cancellationToken);

        log?.LogStage(
            "retrieval",
            durationMs: Environment.TickCount64 - retrievalStarted,
            tenantId: tenant.TenantId,
            retrievedChunks: retrievedCount);

        var toolsStarted = Environment.TickCount64;
        var toolLogs = await tools.ExecuteAsync(tenant, query, retrievedCount, vectorMs, cancellationToken);

        log?.LogStage(
            "mcp_tools",
            durationMs: Environment.TickCount64 - toolsStarted,
            tenantId: tenant.TenantId,
            retrievedChunks: toolLogs.Count);

        var systemPrompt =
            "You are a compliance assistant for an institutional onboarding platform. " +
            "Answer ONLY using the provided policy context. If the context does not contain the answer, say you cannot find it in the indexed documents. " +
            "Be concise and cite rule IDs or section names when available.";

        var sourceBlocks = chunks
            .Select((chunk, index) =>
                $"[Source {index + 1}] {chunk.DocumentId} — {chunk.SectionTitle}" +
                (chunk.PageNumber > 0 ? $" (page {chunk.PageNumber})" : string.Empty) +
                $":\n{chunk.Content}")
            .ToList();

        var userPrompt =
            $"Tenant: {tenant.Name} ({tenant.TenantId})\n\n" +
            $"{string.Join("\n\n", sourceBlocks)}\n\n" +
            $"Question: {query}";

        var synthesisStarted = Environment.TickCount64;
        var answer = await openAi.ChatAsync(systemPrompt, userPrompt, cancellationToken);
        log?.LogStage(
            "openai_synthesis",
            durationMs: Environment.TickCount64 - synthesisStarted,
            tenantId: tenant.TenantId);

        var combinedContext = string.Join("\n\n", chunks.Select(c => c.Content));
        var contextDtos = chunks.Select(ToContextDto).ToList();

        return new ApiResponse
        {
            Success = true,
            Message = answer,
            Timestamp = DateTime.UtcNow,
            ToolLogs = toolLogs,
            Context = contextDtos.FirstOrDefault(),
            Contexts = contextDtos,
            Telemetry = new TelemetryDto
            {
                VectorSearchP95Ms = Math.Round(vectorMs),
                DynamoDbAssemblyMs = Math.Round(dynamoMs),
                HybridRerankMs = Math.Round(rerankMs),
                RagasFaithfulness = 0,
                CrossTenantLeakPercent = 0,
                RetrievedChunks = retrievedCount
            }
        };
    }

    public async Task QueryStreamAsync(
        TenantInfo tenant,
        string query,
        SseStreamWriter sse,
        RequestLogContext? log = null,
        CancellationToken cancellationToken = default)
    {
        var retrievalStarted = Environment.TickCount64;
        var (chunks, vectorMs, dynamoMs, rerankMs, retrievedCount) =
            await retrieval.RetrieveAsync(tenant, query, cancellationToken);

        log?.LogStage(
            "retrieval",
            durationMs: Environment.TickCount64 - retrievalStarted,
            tenantId: tenant.TenantId,
            retrievedChunks: retrievedCount);

        var contextDtos = chunks.Select(ToContextDto).ToList();
        await sse.WriteEventAsync("contexts", new { contexts = contextDtos }, cancellationToken);

        var toolsStarted = Environment.TickCount64;
        var toolLogs = await tools.ExecuteAsync(tenant, query, retrievedCount, vectorMs, cancellationToken);

        foreach (var toolLog in toolLogs)
        {
            await sse.WriteEventAsync("toolLog", toolLog, cancellationToken);
        }

        log?.LogStage(
            "mcp_tools",
            durationMs: Environment.TickCount64 - toolsStarted,
            tenantId: tenant.TenantId,
            retrievedChunks: toolLogs.Count);

        var systemPrompt =
            "You are a compliance assistant for an institutional onboarding platform. " +
            "Answer ONLY using the provided policy context. If the context does not contain the answer, say you cannot find it in the indexed documents. " +
            "Be concise and cite rule IDs or section names when available.";

        var sourceBlocks = chunks
            .Select((chunk, index) =>
                $"[Source {index + 1}] {chunk.DocumentId} — {chunk.SectionTitle}" +
                (chunk.PageNumber > 0 ? $" (page {chunk.PageNumber})" : string.Empty) +
                $":\n{chunk.Content}")
            .ToList();

        var userPrompt =
            $"Tenant: {tenant.Name} ({tenant.TenantId})\n\n" +
            $"{string.Join("\n\n", sourceBlocks)}\n\n" +
            $"Question: {query}";

        var synthesisStarted = Environment.TickCount64;
        await foreach (var token in openAi.ChatStreamAsync(systemPrompt, userPrompt, cancellationToken))
        {
            await sse.WriteEventAsync("token", new { text = token }, cancellationToken);
        }

        log?.LogStage(
            "openai_synthesis",
            durationMs: Environment.TickCount64 - synthesisStarted,
            tenantId: tenant.TenantId);

        var telemetry = new TelemetryDto
        {
            VectorSearchP95Ms = Math.Round(vectorMs),
            DynamoDbAssemblyMs = Math.Round(dynamoMs),
            HybridRerankMs = Math.Round(rerankMs),
            RagasFaithfulness = 0,
            CrossTenantLeakPercent = 0,
            RetrievedChunks = retrievedCount
        };

        await sse.WriteEventAsync("telemetry", telemetry, cancellationToken);
        await sse.WriteEventAsync("done", new { }, cancellationToken);
    }

    private static ContextDto ToContextDto(RetrievedChunkResult chunk) => new()
    {
        DocumentId = chunk.DocumentId,
        SectionTitle = chunk.SectionTitle,
        Content = chunk.Content,
        PrimaryMethod = chunk.PrimaryMethod,
        HybridReranked = chunk.HybridReranked,
        ParentChunkTokenSize = chunk.ParentChunkTokenSize,
        RelevanceScore = chunk.RelevanceScore,
        Page = chunk.PageNumber
    };
}
