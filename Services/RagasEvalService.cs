using ClientOnboardingLambda.Models;

namespace ClientOnboardingLambda.Services;

public sealed record EvalSummary(
    double Faithfulness,
    int QuestionCount,
    IReadOnlyList<EvalQuestionResult> Results,
    string Message);

public sealed record EvalQuestionResult(
    string Question,
    double Faithfulness,
    bool ExpectedContainsPassed);

public sealed class RagasEvalService(
    RagOrchestrator rag,
    OpenAiService openAi,
    EvalStatusWriter evalStatusWriter)
{
    public async Task<EvalSummary> RunEvalAsync(TenantInfo tenant, CancellationToken cancellationToken = default)
    {
        var cases = GoldenEvalDataset.GetCases(tenant.TenantId);
        if (cases.Count == 0)
        {
            throw new InvalidOperationException($"Golden eval dataset is empty for {tenant.TenantId}.");
        }

        var runStarted = DateTime.UtcNow;
        await evalStatusWriter.WriteLatestAsync(tenant, "running", runStarted, cancellationToken: cancellationToken);

        var results = new List<EvalQuestionResult>();

        try
        {
            foreach (var evalCase in cases)
            {
                var response = await rag.QueryAsync(tenant, evalCase.Question, cancellationToken: cancellationToken);
                var context = BuildContextText(response);
                var faithfulness = await openAi.ScoreFaithfulnessAsync(
                    evalCase.Question,
                    response.Message,
                    context,
                    cancellationToken);

                var expectedPassed = evalCase.ExpectedContains.All(token =>
                    response.Message.Contains(token, StringComparison.OrdinalIgnoreCase));

                results.Add(new EvalQuestionResult(evalCase.Question, faithfulness, expectedPassed));
            }

            var averageFaithfulness = Math.Round(results.Average(r => r.Faithfulness), 4);
            var completedAt = DateTime.UtcNow;

            await evalStatusWriter.WriteLatestAsync(
                tenant,
                "completed",
                completedAt,
                faithfulness: averageFaithfulness,
                questionCount: results.Count,
                cancellationToken: cancellationToken);

            return new EvalSummary(
                averageFaithfulness,
                results.Count,
                results,
                $"RAGAS faithfulness {averageFaithfulness:F2} across {results.Count} golden question(s) for {tenant.TenantId}.");
        }
        catch (Exception ex)
        {
            await evalStatusWriter.WriteLatestAsync(
                tenant,
                "failed",
                DateTime.UtcNow,
                error: ex.Message,
                cancellationToken: cancellationToken);
            throw;
        }
    }

    private static string BuildContextText(ApiResponse response)
    {
        if (response.Contexts is { Count: > 0 })
        {
            return string.Join("\n\n", response.Contexts.Select(c => c.Content));
        }

        return response.Context?.Content ?? string.Empty;
    }
}
