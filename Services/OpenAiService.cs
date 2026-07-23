using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClientOnboardingLambda.Services;

public sealed class OpenAiService(string apiKey)
{
    private const string ChatUrl = "https://api.openai.com/v1/chat/completions";
    private const string EmbeddingsUrl = "https://api.openai.com/v1/embeddings";
    private const string ChatModel = "gpt-4o-mini";
    private const string EmbeddingModel = "text-embedding-3-small";

    private static readonly HttpClient HttpClient = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<string> ChatAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            model = ChatModel,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            }
        };

        using var request = CreateRequest(ChatUrl, payload);
        using var response = await HttpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI chat failed ({(int)response.StatusCode}): {body}");
        }

        var parsed = JsonSerializer.Deserialize<ChatCompletionResponse>(body, JsonOptions);
        return parsed?.Choices?.FirstOrDefault()?.Message?.Content
               ?? "No content returned from OpenAI.";
    }

    public async Task<string> ChatLegacyAsync(string userPrompt, CancellationToken cancellationToken = default)
    {
        const string legacySystem =
            "You are an AI assistant for a financial client management portal. Provide helpful, concise responses regarding client onboarding, documentation, and workflows.";

        return await ChatAsync(legacySystem, userPrompt, cancellationToken);
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        var payload = new { model = EmbeddingModel, input = text };
        using var request = CreateRequest(EmbeddingsUrl, payload);
        using var response = await HttpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI embeddings failed ({(int)response.StatusCode}): {body}");
        }

        var parsed = JsonSerializer.Deserialize<EmbeddingResponse>(body, JsonOptions);
        var vector = parsed?.Data?.FirstOrDefault()?.Embedding;

        if (vector is null || vector.Length == 0)
        {
            throw new InvalidOperationException("OpenAI returned an empty embedding vector.");
        }

        return vector;
    }

    public async Task<List<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        if (texts.Count == 0)
        {
            return [];
        }

        var payload = new { model = EmbeddingModel, input = texts };
        using var request = CreateRequest(EmbeddingsUrl, payload);
        using var response = await HttpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI batch embeddings failed ({(int)response.StatusCode}): {body}");
        }

        var parsed = JsonSerializer.Deserialize<EmbeddingResponse>(body, JsonOptions);
        return parsed?.Data?.OrderBy(d => d.Index).Select(d => d.Embedding).ToList() ?? [];
    }

    private HttpRequestMessage CreateRequest(string url, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    private sealed class ChatCompletionResponse
    {
        [JsonPropertyName("choices")]
        public List<ChatChoice>? Choices { get; set; }
    }

    private sealed class ChatChoice
    {
        [JsonPropertyName("message")]
        public ChatMessageBody? Message { get; set; }
    }

    private sealed class ChatMessageBody
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }

    private sealed class EmbeddingResponse
    {
        [JsonPropertyName("data")]
        public List<EmbeddingData>? Data { get; set; }
    }

    private sealed class EmbeddingData
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("embedding")]
        public float[] Embedding { get; set; } = [];
    }
}
