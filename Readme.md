# Client Onboarding Lambda

AWS Lambda function that powers an AI assistant for a financial client management portal. It accepts user prompts over an HTTP API (API Gateway HTTP API v2 / Lambda Function URL), forwards them to the OpenAI Chat Completions API, and returns a structured JSON response.

## Overview

```
Client → API Gateway HTTP API v2 → Lambda → OpenAI Chat Completions API
```

The handler uses a fixed system prompt tailored to client onboarding, documentation, and workflow questions. User input is sent as the user message; the model reply is returned in the response body.

| Setting | Value |
|---------|-------|
| Runtime | .NET 10 (`dotnet10`) |
| Architecture | arm64 |
| Memory | 512 MB |
| Timeout | 30 seconds |
| OpenAI model | `gpt-4o-mini` |

## Project structure

| File | Purpose |
|------|---------|
| `Function.cs` | Lambda handler, OpenAI integration, and JSON models |
| `ClientOnboardingLambda.csproj` | .NET project and NuGet dependencies |
| `aws-lambda-tools-defaults.json` | Default deploy settings for `dotnet lambda` CLI |

## Configuration

### Environment variables

| Variable | Required | Description |
|----------|----------|-------------|
| `OPENAI_API_KEY` | Yes | OpenAI API key used as a Bearer token when calling the Chat Completions API |

Set this in the Lambda console under **Configuration → Environment variables**, or via your deployment tooling. If it is missing or empty, the function returns HTTP 500.

### Trigger

The handler expects an `APIGatewayHttpApiV2ProxyRequest` (HTTP API v2 or Lambda Function URL with the same payload shape). Configure API Gateway or a Function URL to invoke `ClientOnboardingLambda::ClientOnboardingLambda.Function::FunctionHandler`.

## API contract

### Request

Send a `POST` with `Content-Type: application/json`:

```json
{
  "prompt": "What documents are needed for a new corporate client?"
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `prompt` | string | Yes | User question or instruction for the assistant |

### Response

All responses use `Content-Type: application/json`:

```json
{
  "success": true,
  "message": "For a new corporate client, you typically need...",
  "timestamp": "2026-07-22T04:21:00.0000000Z"
}
```

| Field | Type | Description |
|-------|------|-------------|
| `success` | boolean | `true` when OpenAI returned a reply; `false` on error |
| `message` | string | AI reply on success, or an error description on failure |
| `timestamp` | string (UTC) | Time the response was generated |

### Status codes

| Code | When |
|------|------|
| 200 | Prompt processed successfully |
| 400 | Missing body, invalid JSON, or empty `prompt` |
| 500 | Missing `OPENAI_API_KEY`, unhandled exception, or other server error |
| Other | OpenAI API error (status code forwarded from OpenAI) |

## Local development

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Amazon.Lambda.Tools](https://github.com/aws/aws-extensions-for-dotnet-cli#aws-lambda-amazonlambdatools) global tool (for deployment)

### Build

```bash
dotnet build
```

### Deploy

Install or update the Lambda tools:

```bash
dotnet tool install -g Amazon.Lambda.Tools
# or
dotnet tool update -g Amazon.Lambda.Tools
```

From the project root:

```bash
dotnet lambda deploy-function
```

Defaults in `aws-lambda-tools-defaults.json` include region `us-east-1`, Release configuration, and the handler name above. Override as needed via CLI flags or that file.

### Deploy from Visual Studio

Right-click the project in Solution Explorer and choose **Publish to AWS Lambda**. Use the Function View window to test invokes, configure triggers, set environment variables, and view logs.

## Dependencies

- `Amazon.Lambda.APIGatewayEvents` — HTTP API v2 request/response types
- `Amazon.Lambda.Core` — Lambda runtime
- `Amazon.Lambda.Serialization.SystemTextJson` — source-generated JSON serialization

JSON serialization uses `System.Text.Json` with a source-generated `LambdaJsonContext` for AOT-friendly, trim-safe payloads.
