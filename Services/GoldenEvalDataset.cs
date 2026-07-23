using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClientOnboardingLambda.Services;

public sealed record GoldenEvalCase(
    [property: JsonPropertyName("question")] string Question,
    [property: JsonPropertyName("expected_contains")] IReadOnlyList<string> ExpectedContains);

public static class GoldenEvalDataset
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<GoldenEvalCase> GetCases(string tenantId)
    {
        var resourceName = $"ClientOnboardingLambda.Golden.{tenantId}.json";
        var assembly = typeof(GoldenEvalDataset).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            throw new InvalidOperationException($"No golden eval dataset for {tenantId}.");
        }

        return JsonSerializer.Deserialize<List<GoldenEvalCase>>(stream, JsonOptions) ?? [];
    }
}
