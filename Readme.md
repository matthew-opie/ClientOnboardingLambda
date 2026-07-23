# Client Onboarding Lambda

AWS Lambda function that powers a multi-tenant AI compliance assistant for an institutional client onboarding platform. It exposes HTTP routes for tenant-scoped RAG queries, document ingestion, and a legacy chat endpoint. Retrieved policy context is combined with DynamoDB metadata and OpenAI to produce grounded answers with tool execution logs and telemetry.

## Overview

```
Client → Lambda Function URL / API Gateway HTTP API v2 → RequestRouter
                                                              │
                    ┌─────────────────────────────────────────┼─────────────────────────┐
                    │                                         │                         │
              GET /health                              POST /chat (legacy)    POST /tenants/{id}/query
              GET /tenants                                                      POST /admin/ingest/{id}
                    │                                         │                         │
                    └─────────────────────────────────────────┼─────────────────────────┘
                                                              │
                              ┌───────────────────────────────┴───────────────────────────────┐
                              │                                                               │
                       DocumentIngestService                                          RagOrchestrator
                    S3 → PDF → chunk → DynamoDB + Qdrant                    HybridRetrieval → McpToolExecutor → OpenAI
```

| Setting | Value |
|---------|-------|
| Runtime | .NET 10 (`dotnet10`) |
| Architecture | arm64 |
| Memory | 1024 MB |
| Timeout | 60 seconds |
| Chat model | `gpt-4o-mini` |
| Embedding model | `text-embedding-3-small` |

## Features

- **Multi-tenant RAG** — Ten predefined institutional tenants, each with an isolated DynamoDB partition key and Qdrant collection.
- **Hybrid retrieval** — BM25 keyword scoring over child chunks in DynamoDB, fused with dense vector search in Qdrant via reciprocal rank fusion (RRF).
- **Document ingestion** — Admin endpoint reads tenant PDFs from S3, extracts text, creates parent/child chunks, embeds child chunks with OpenAI, and writes to DynamoDB and Qdrant.
- **Compliance tools** — Simulated MCP-style tools verify tenant isolation, KYC status, and ticker restrictions before answering.
- **Legacy chat** — Simple OpenAI chat endpoint without retrieval for backward compatibility.

## Project structure

| Path | Purpose |
|------|---------|
| `Function.cs` | Lambda entry point; loads config and delegates to `RequestRouter` |
| `Models/ApiModels.cs` | Request/response DTOs, telemetry, and chunk records |
| `Models/TenantRegistry.cs` | Hardcoded list of 10 tenant definitions |
| `Services/RequestRouter.cs` | HTTP routing and response factory |
| `Services/AppConfig.cs` | Environment variable loading and validation |
| `Services/RagOrchestrator.cs` | RAG query pipeline: retrieve → tools → OpenAI |
| `Services/RetrievalServices.cs` | Text chunking, BM25 scoring, and hybrid retrieval |
| `Services/DocumentIngestService.cs` | S3 PDF ingest, chunking, DynamoDB/Qdrant writes |
| `Services/OpenAiService.cs` | OpenAI chat completions and embeddings |
| `Services/QdrantClient.cs` | Qdrant vector upsert and search |
| `Services/DynamoDbRepository.cs` | Single-table DynamoDB access |
| `Services/McpToolExecutor.cs` | Compliance tool execution and logging |
| `ClientOnboardingLambda.csproj` | .NET project and NuGet dependencies |
| `aws-lambda-tools-defaults.json` | Default deploy settings for `dotnet lambda` CLI |

## Configuration

### Environment variables

| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `OPENAI_API_KEY` | Yes (query/ingest) | — | OpenAI API key for chat and embeddings |
| `QDRANT_URL` | Yes (query/ingest) | — | Qdrant REST API base URL (trailing slash stripped) |
| `QDRANT_API_KEY` | Yes (query/ingest) | — | Qdrant API key |
| `DYNAMODB_TABLE_NAME` | No | `OnboardingPlatform` | DynamoDB single-table name for chunks and metadata |
| `SEED_BUCKET_NAME` | Yes (ingest) | — | S3 bucket containing tenant seed documents |
| `ADMIN_API_KEY` | Yes (ingest) | — | Shared secret sent as `x-api-key` on ingest requests |

Set these in the Lambda console under **Configuration → Environment variables**, or via your deployment tooling. Missing required values for a route return HTTP 500 with an error message.

The Lambda execution role must allow DynamoDB read/write on the configured table and S3 read on the seed bucket.

### Trigger

The handler expects an `APIGatewayHttpApiV2ProxyRequest` (HTTP API v2 or Lambda Function URL with the same payload shape). Configure a Function URL or API Gateway to invoke:

`ClientOnboardingLambda::ClientOnboardingLambda.Function::FunctionHandler`

CORS is handled by the Lambda Function URL configuration, not in application code.

## Tenants

Ten tenants are defined in `TenantRegistry`. Each has a folder ID (e.g. `tenant_001`), display ID, name, DynamoDB partition key (`TENANT#001`), and Qdrant collection name.

List available tenants:

```http
GET /tenants
```

Seed documents for ingestion are expected under `s3://{SEED_BUCKET_NAME}/{tenantId}/`, including PDFs and an optional `tenant_metadata.json` for KYC and ticker restriction data.

## API

All JSON responses use `Content-Type: application/json`.

### `GET /health`

Returns configuration status (table name, seed bucket, Qdrant configured).

### `GET /tenants`

Returns the list of registered tenants with IDs, names, partition keys, and vector collection names.

### `POST /chat` (legacy)

Simple OpenAI chat without RAG. Requires `OPENAI_API_KEY` and Qdrant config (via `ValidateForQuery`).

**Request:**

```json
{
  "prompt": "What documents are needed for a new corporate client?"
}
```

**Response:**

```json
{
  "success": true,
  "message": "For a new corporate client, you typically need...",
  "timestamp": "2026-07-22T04:21:00.0000000Z"
}
```

### `POST /tenants/{tenantId}/query`

Primary RAG endpoint. Retrieves relevant policy context for the tenant, runs compliance tools, and generates a grounded answer.

**Request:**

```json
{
  "query": "What is the maximum allocation to private equity?"
}
```

**Response:**

```json
{
  "success": true,
  "message": "According to the Investment Management Agreement...",
  "timestamp": "2026-07-22T04:21:00.0000000Z",
  "toolLogs": [
    {
      "toolName": "verify_tenant_isolation",
      "parameters": "tenant_id=\"tenant_001\", partition=\"TENANT#001\"",
      "output": "{\"isolated\":true,...}",
      "status": "Success",
      "timestamp": "2026-07-22T04:21:00.0000000+00:00",
      "durationMs": 1
    }
  ],
  "context": {
    "documentId": "IMA_Beacon_Hill",
    "sectionTitle": "Investment Management Agreement",
    "content": "...",
    "primaryMethod": "DenseVector",
    "hybridReranked": true,
    "parentChunkTokenSize": 1000,
    "relevanceScore": 0.03
  },
  "telemetry": {
    "vectorSearchP95Ms": 420,
    "dynamoDbAssemblyMs": 85,
    "hybridRerankMs": 12,
    "ragasFaithfulness": 0.92,
    "crossTenantLeakPercent": 0,
    "retrievedChunks": 5
  }
}
```

| Field | Type | Description |
|-------|------|-------------|
| `success` | boolean | `true` on success; `false` on error |
| `message` | string | AI answer or error description |
| `timestamp` | string (UTC) | Response generation time |
| `toolLogs` | array | MCP-style tool execution records (query endpoint only) |
| `context` | object | Retrieved parent chunk used for the answer (query endpoint only) |
| `telemetry` | object | Retrieval and quality metrics (query endpoint only) |

Documents must be ingested for the tenant before querying; otherwise the function returns HTTP 500 with a message to run ingest first.

### `POST /admin/ingest/{tenantId}`

Re-ingests all PDFs for a tenant from S3. Deletes existing tenant data in DynamoDB, re-chunks documents, re-embeds child chunks, and upserts vectors to Qdrant.

**Headers:**

| Header | Required | Description |
|--------|----------|-------------|
| `x-api-key` | Yes | Must match `ADMIN_API_KEY` |

**Response:**

```json
{
  "success": true,
  "message": "Ingested 3 PDF(s), 142 child chunks into tenant_001.",
  "timestamp": "2026-07-22T04:21:00.0000000Z"
}
```

### Status codes

| Code | When |
|------|------|
| 200 | Request processed successfully |
| 400 | Missing body, invalid JSON, or empty `prompt` / `query` |
| 401 | Missing or invalid `x-api-key` on ingest |
| 404 | Unknown route or tenant |
| 500 | Missing configuration, no ingested chunks, unhandled exception, or upstream API failure |

## RAG pipeline

1. **Ingest** — PDFs are downloaded from S3, text is extracted with PdfPig, split into ~4000-char parent chunks and ~800-char child chunks (100-char overlap), stored in DynamoDB, and child embeddings are written to Qdrant.
2. **Retrieve** — On query, child chunks are loaded from DynamoDB for BM25 scoring; the query is embedded and searched in Qdrant; results are fused with RRF and the top parent chunk is loaded for context.
3. **Tools** — `McpToolExecutor` runs tenant isolation checks, KYC verification, optional ticker restriction lookup, and context assembly logging.
4. **Generate** — OpenAI (`gpt-4o-mini`) answers using the retrieved context and a compliance-focused system prompt.

## Local development

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Amazon.Lambda.Tools](https://github.com/aws/aws-extensions-for-dot-net-cli#aws-lambda-amazonlambdatools) global tool (for deployment)
- Access to configured AWS resources (DynamoDB, S3) and external services (OpenAI, Qdrant) for full RAG testing

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

Defaults in `aws-lambda-tools-defaults.json` include region `us-east-1`, Release configuration, 1024 MB memory, 60-second timeout, and the handler name above. Override as needed via CLI flags or that file.

### Deploy from Visual Studio

Right-click the project in Solution Explorer and choose **Publish to AWS Lambda**. Use the Function View window to test invokes, configure triggers, set environment variables, and view logs.

## Dependencies

- `Amazon.Lambda.APIGatewayEvents` — HTTP API v2 request/response types
- `Amazon.Lambda.Core` — Lambda runtime
- `Amazon.Lambda.Serialization.SystemTextJson` — JSON serialization
- `AWSSDK.DynamoDBv2` — DynamoDB single-table storage for chunks and tenant metadata
- `AWSSDK.S3` — Seed document retrieval during ingest
- `UglyToad.PdfPig` — PDF text extraction

JSON serialization uses `System.Text.Json` with a source-generated `LambdaJsonContext` for AOT-friendly, trim-safe payloads.
