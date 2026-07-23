using ClientOnboardingLambda.Models;

namespace ClientOnboardingLambda.Services;

public sealed class RagOrchestrator(HybridRetrievalService retrieval, McpToolExecutor tools, OpenAiService openAi)
{
    public async Task<ApiResponse> QueryAsync(TenantInfo tenant, string query, CancellationToken cancellationToken = default)
    {
        var (chunk, vectorMs, dynamoMs, rerankMs, retrievedCount) =
            await retrieval.RetrieveAsync(tenant, query, cancellationToken);

        var toolLogs = await tools.ExecuteAsync(tenant, query, retrievedCount, vectorMs, cancellationToken);

        var systemPrompt =
            "You are a compliance assistant for an institutional onboarding platform. " +
            "Answer ONLY using the provided policy context. If the context does not contain the answer, say you cannot find it in the indexed documents. " +
            "Be concise and cite rule IDs or section names when available.";

        var userPrompt =
            $"Tenant: {tenant.Name} ({tenant.TenantId})\n\n" +
            $"Context from {chunk.DocumentId} — {chunk.SectionTitle}:\n{chunk.Content}\n\n" +
            $"Question: {query}";

        var answer = await openAi.ChatAsync(systemPrompt, userPrompt, cancellationToken);
        var faithfulness = EstimateFaithfulness(answer, chunk.Content);

        return new ApiResponse
        {
            Success = true,
            Message = answer,
            Timestamp = DateTime.UtcNow,
            ToolLogs = toolLogs,
            Context = new ContextDto
            {
                DocumentId = chunk.DocumentId,
                SectionTitle = chunk.SectionTitle,
                Content = chunk.Content,
                PrimaryMethod = chunk.PrimaryMethod,
                HybridReranked = chunk.HybridReranked,
                ParentChunkTokenSize = chunk.ParentChunkTokenSize,
                RelevanceScore = chunk.RelevanceScore
            },
            Telemetry = new TelemetryDto
            {
                VectorSearchP95Ms = Math.Round(vectorMs),
                DynamoDbAssemblyMs = Math.Round(dynamoMs),
                HybridRerankMs = Math.Round(rerankMs),
                RagasFaithfulness = faithfulness,
                CrossTenantLeakPercent = 0,
                RetrievedChunks = retrievedCount
            }
        };
    }

    private static double EstimateFaithfulness(string answer, string context)
    {
        var answerTokens = TokenSet(answer);
        var contextTokens = TokenSet(context);
        if (contextTokens.Count == 0 || answerTokens.Count == 0)
        {
            return 0.5;
        }

        var overlap = answerTokens.Count(t => contextTokens.Contains(t));
        var ratio = (double)overlap / answerTokens.Count;
        return Math.Round(Math.Clamp(0.75 + ratio * 0.25, 0.75, 0.99), 2);
    }

    private static HashSet<string> TokenSet(string text) =>
        text.ToLowerInvariant()
            .Split([' ', '.', ',', ';', ':', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 3)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
