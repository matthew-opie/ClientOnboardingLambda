using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;

[assembly: LambdaSerializer(typeof(SourceGeneratorLambdaJsonSerializer<ClientOnboardingLambda.LambdaJsonContext>))]

namespace ClientOnboardingLambda;

public class Function
{
    private static readonly HttpClient HttpClient = new HttpClient();

    public async Task<APIGatewayHttpApiV2ProxyResponse> FunctionHandler(APIGatewayHttpApiV2ProxyRequest lambdaEvent, ILambdaContext context)
    {
        context.Logger.LogInformation($"Received raw Lambda URL body: {lambdaEvent?.Body}");

        // 1. Fetch secret key safely from AWS Lambda Environment Variables
        string apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return CreateJsonResponse(false, "OpenAI API Key retrieval failure. Check Lambda environment variables.", 500);
        }

        // 2. Extract and deserialize the actual body sent by the client
        if (string.IsNullOrWhiteSpace(lambdaEvent?.Body))
        {
            return CreateJsonResponse(false, "Prompt body is missing.", 400);
        }

        RequestModel? request = null;
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
            // 3. Build payload for OpenAI Chat Completions API
            var openAiRequest = new OpenAIRequestBody
            {
                Model = "gpt-4o-mini",
                Messages = new[]
                {
                    new OpenAIMessage 
                    { 
                        Role = "system", 
                        Content = "You are an AI assistant for a financial client management portal. Provide helpful, concise responses regarding client onboarding, documentation, and workflows." 
                    },
                    new OpenAIMessage 
                    { 
                        Role = "user", 
                        Content = request.Prompt 
                    }
                }
            };

            string jsonPayload = JsonSerializer.Serialize(openAiRequest, LambdaJsonContext.Default.OpenAIRequestBody);

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
            {
                Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
            };
            
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            // 4. Send Request to OpenAI
            using var httpResponse = await HttpClient.SendAsync(httpRequest);
            string responseString = await httpResponse.Content.ReadAsStringAsync();

            if (!httpResponse.IsSuccessStatusCode)
            {
                context.Logger.LogError($"OpenAI API Error ({httpResponse.StatusCode}): {responseString}");
                return CreateJsonResponse(false, $"OpenAI API responded with status code {httpResponse.StatusCode}.", (int)httpResponse.StatusCode);
            }

            // 5. Parse AI Response
            var openAiResult = JsonSerializer.Deserialize(responseString, LambdaJsonContext.Default.OpenAIResponseBody);
            string aiOutput = openAiResult?.Choices?[0]?.Message?.Content ?? "No content returned from OpenAI.";

            return CreateJsonResponse(true, aiOutput, 200);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Unhandled Exception: {ex.Message}");
            return CreateJsonResponse(false, $"Server Error: {ex.Message}", 500);
        }
    }

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
            Headers = new System.Collections.Generic.Dictionary<string, string>
            {
                { "Content-Type", "application/json" }
            }
        };
    }
}

// ============================================================================
// Data Models & JSON Serialization Context
// ============================================================================

public class RequestModel
{
    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = string.Empty;
}

public class ResponseModel
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }
}

public class OpenAIRequestBody
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "gpt-4o-mini";

    [JsonPropertyName("messages")]
    public OpenAIMessage[] Messages { get; set; } = Array.Empty<OpenAIMessage>();
}

public class OpenAIMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}

public class OpenAIResponseBody
{
    [JsonPropertyName("choices")]
    public OpenAIChoice[]? Choices { get; set; }
}

public class OpenAIChoice
{
    [JsonPropertyName("message")]
    public OpenAIMessage? Message { get; set; }
}

[JsonSerializable(typeof(RequestModel))]
[JsonSerializable(typeof(ResponseModel))]
[JsonSerializable(typeof(OpenAIRequestBody))]
[JsonSerializable(typeof(OpenAIResponseBody))]
[JsonSerializable(typeof(APIGatewayHttpApiV2ProxyRequest))]
[JsonSerializable(typeof(APIGatewayHttpApiV2ProxyResponse))]
public partial class LambdaJsonContext : JsonSerializerContext
{
}