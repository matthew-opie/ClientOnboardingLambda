using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;

[assembly: LambdaSerializer(typeof(SourceGeneratorLambdaJsonSerializer<ClientOnboardingLambda.LambdaJsonContext>))]

namespace ClientOnboardingLambda;

/// <summary>
/// Lambda entry point for the client onboarding AI assistant.
/// Invoked via API Gateway HTTP API v2 or a Lambda Function URL with the same payload shape.
/// </summary>
public class Function
{
    private const string OpenAiChatCompletionsUrl = "https://api.openai.com/v1/chat/completions";
    private const string OpenAiModel = "gpt-4o-mini";

    /// <summary>
    /// Sets the assistant's tone and domain. Edit this to change what the bot knows or how it responds.
    /// </summary>
    private const string SystemPrompt =
        "You are an AI assistant for a financial client management portal. Provide helpful, concise responses regarding client onboarding, documentation, and workflows.";

    /// <summary>
    /// Reused across invocations. HttpClient is thread-safe; a single instance avoids socket exhaustion under load.
    /// </summary>
    private static readonly HttpClient HttpClient = new();

    /// <summary>
    /// Accepts a JSON body with a user <c>prompt</c>, calls OpenAI Chat Completions, and returns a uniform JSON envelope.
    /// </summary>
    /// <param name="lambdaEvent">
    /// HTTP API v2 proxy event. <see cref="APIGatewayHttpApiV2ProxyRequest.Body"/> must be JSON:
    /// <c>{ "prompt": "your question here" }</c>
    /// </param>
    /// <param name="context">Lambda runtime context (logging, request id, etc.).</param>
    /// <returns>
    /// HTTP response with <see cref="ResponseModel"/> JSON body.
    /// On success, <see cref="ResponseModel.Message"/> contains the model reply; on failure, it contains an error description.
    /// </returns>
    /// <remarks>
    /// Requires the <c>OPENAI_API_KEY</c> environment variable to be set on the Lambda function.
    /// See Readme.md for the full request/response contract and status codes.
    /// </remarks>
    public async Task<APIGatewayHttpApiV2ProxyResponse> FunctionHandler(APIGatewayHttpApiV2ProxyRequest lambdaEvent, ILambdaContext context)
    {
        context.Logger.LogInformation($"Received raw Lambda URL body: {lambdaEvent.Body}");

        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return CreateJsonResponse(false, "OpenAI API Key retrieval failure. Check Lambda environment variables.", 500);
        }

        if (string.IsNullOrWhiteSpace(lambdaEvent.Body))
        {
            return CreateJsonResponse(false, "Prompt body is missing.", 400);
        }

        RequestModel? request;
        try
        {
            request = JsonSerializer.Deserialize(lambdaEvent.Body, LambdaJsonContext.Default.RequestModel);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"JSON Deserialization error: {ex.Message}");
            return CreateJsonResponse(false, "Invalid JSON payload.", 400);
        }

        if (string.IsNullOrWhiteSpace(request?.Prompt))
        {
            return CreateJsonResponse(false, "Prompt is empty.", 400);
        }

        try
        {
            var openAiRequest = new OpenAiRequestBody
            {
                Model = OpenAiModel,
                Messages =
                [
                    new OpenAiMessage { Role = "system", Content = SystemPrompt },
                    new OpenAiMessage { Role = "user", Content = request.Prompt }
                ]
            };

            string jsonPayload = JsonSerializer.Serialize(openAiRequest, LambdaJsonContext.Default.OpenAiRequestBody);

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, OpenAiChatCompletionsUrl);
            httpRequest.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var httpResponse = await HttpClient.SendAsync(httpRequest);
            var responseString = await httpResponse.Content.ReadAsStringAsync();

            if (!httpResponse.IsSuccessStatusCode)
            {
                context.Logger.LogError($"OpenAI API Error ({httpResponse.StatusCode}): {responseString}");
                // Forward OpenAI's status (e.g. 401, 429) so callers can distinguish auth vs rate-limit issues.
                return CreateJsonResponse(false, $"OpenAI API responded with status code {httpResponse.StatusCode}.", (int)httpResponse.StatusCode);
            }

            var openAiResult = JsonSerializer.Deserialize(responseString, LambdaJsonContext.Default.OpenAiResponseBody);
            var aiOutput = openAiResult?.Choices?[0].Message?.Content ?? "No content returned from OpenAI.";

            return CreateJsonResponse(true, aiOutput, 200);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Unhandled Exception: {ex.Message}");
            return CreateJsonResponse(false, $"Server Error: {ex.Message}", 500);
        }
    }

    /// <summary>
    /// Builds the standard API Gateway HTTP response returned to clients for both success and error paths.
    /// </summary>
    private APIGatewayHttpApiV2ProxyResponse CreateJsonResponse(bool success, string message, int statusCode)
    {
        var responseObj = new ResponseModel
        {
            Success = success,
            Message = message,
            Timestamp = DateTime.UtcNow
        };

        string jsonBody = JsonSerializer.Serialize(responseObj, LambdaJsonContext.Default.ResponseModel);

        return new APIGatewayHttpApiV2ProxyResponse
        {
            StatusCode = statusCode,
            Body = jsonBody,
            Headers = new Dictionary<string, string>
            {
                { "Content-Type", "application/json" }
            }
        };
    }
}

/// <summary>
/// Incoming request body from the client portal. Serialized as <c>{ "prompt": "..." }</c>.
/// </summary>
public class RequestModel
{
    /// <summary>User question or instruction passed to the assistant.</summary>
    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = string.Empty;
}

/// <summary>
/// Outgoing response envelope returned for every invocation, success or failure.
/// </summary>
public class ResponseModel
{
    /// <summary><c>true</c> when OpenAI returned a reply; <c>false</c> for validation or upstream errors.</summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>Model reply on success, or a human-readable error message on failure.</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>UTC timestamp when this response was generated.</summary>
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }
}

/// <summary>Payload sent to OpenAI <c>/v1/chat/completions</c>. Internal; not part of the public API.</summary>
public class OpenAiRequestBody
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "gpt-4o-mini";

    [JsonPropertyName("messages")]
    public OpenAiMessage[] Messages { get; set; } = Array.Empty<OpenAiMessage>();
}

/// <summary>A single chat message (system, user, or assistant) in the OpenAI request/response.</summary>
public class OpenAiMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}

/// <summary>Top-level shape of a successful OpenAI Chat Completions response. Only fields we read are modeled.</summary>
public class OpenAiResponseBody
{
    [JsonPropertyName("choices")]
    public OpenAiChoice[]? Choices { get; set; }
}

/// <summary>One completion choice from OpenAI. We use the first choice's message content.</summary>
public class OpenAiChoice
{
    [JsonPropertyName("message")]
    public OpenAiMessage? Message { get; set; }
}

/// <summary>
/// Source-generated JSON serializer context for Lambda cold-start performance and Native AOT compatibility.
/// Add new serializable types here when extending request/response models.
/// </summary>
[JsonSerializable(typeof(RequestModel))]
[JsonSerializable(typeof(ResponseModel))]
[JsonSerializable(typeof(OpenAiRequestBody))]
[JsonSerializable(typeof(OpenAiResponseBody))]
[JsonSerializable(typeof(APIGatewayHttpApiV2ProxyRequest))]
[JsonSerializable(typeof(APIGatewayHttpApiV2ProxyResponse))]
public partial class LambdaJsonContext : JsonSerializerContext
{
}
