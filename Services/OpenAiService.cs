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

    public async IAsyncEnumerable<string> ChatStreamAsync(
        string systemPrompt,
        string userPrompt,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            model = ChatModel,
            stream = true,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            }
        };

        using var request = CreateRequest(ChatUrl, payload);
        using var response = await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"OpenAI chat stream failed ({(int)response.StatusCode}): {body}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                yield break;
            }

            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line["data: ".Length..];
            if (data == "[DONE]")
            {
                yield break;
            }

            ChatStreamChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<ChatStreamChunk>(data, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            var delta = chunk?.Choices?.FirstOrDefault()?.Delta?.Content;
            if (!string.IsNullOrEmpty(delta))
            {
                yield return delta;
            }
        }
    }

    public async Task<double> ScoreFaithfulnessAsync(
        string question,
        string answer,
        string context,
        CancellationToken cancellationToken = default)
    {
        const string systemPrompt =
            "You are a RAGAS faithfulness evaluator. Given a question, retrieved context, and generated answer, " +
            "score how well the answer is grounded in the context from 0.0 to 1.0. " +
            "1.0 means every factual claim in the answer is directly supported by the context. " +
            "0.0 means the answer contradicts or ignores the context. " +
            "Respond with ONLY a decimal number between 0 and 1, with up to two decimal places.";

        var userPrompt =
            $"Question:\n{question}\n\nContext:\n{context}\n\nAnswer:\n{answer}\n\nFaithfulness score:";

        var raw = await ChatAsync(systemPrompt, userPrompt, cancellationToken);
        return ParseScore(raw);
    }

    private static double ParseScore(string raw)
    {
        var trimmed = raw.Trim();
        if (double.TryParse(trimmed, out var score))
        {
            return Math.Round(Math.Clamp(score, 0, 1), 4);
        }

        var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"0\.\d+|1\.0|1|0");
        if (match.Success && double.TryParse(match.Value, out score))
        {
            return Math.Round(Math.Clamp(score, 0, 1), 4);
        }

        throw new InvalidOperationException($"Could not parse faithfulness score from OpenAI response: {raw}");
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

    private sealed class ChatStreamChunk
    {
        [JsonPropertyName("choices")]
        public List<ChatStreamChoice>? Choices { get; set; }
    }

    private sealed class ChatStreamChoice
    {
        [JsonPropertyName("delta")]
        public ChatMessageBody? Delta { get; set; }
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
