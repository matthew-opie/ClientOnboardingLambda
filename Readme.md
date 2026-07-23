# Client Onboarding Lambda

AWS Lambda API for a **multi-tenant institutional compliance onboarding platform** — hybrid RAG queries, async ingest status, RAGAS eval, SSE streaming, and MCP compliance tools (local or remote wire protocol).

Live demo UI: [mattopie.com/onboarding](https://www.mattopie.com/onboarding) · Frontend: [blazor-portfolio-2026](https://github.com/matthew-opie/blazor-portfolio-2026)

## Related repos

| Repo | Role |
|------|------|
| [DocumentIngestLambda](https://github.com/matthew-opie/DocumentIngestLambda) | SQS worker — S3 PDF upload → re-index |
| [McpComplianceServer](https://github.com/matthew-opie/McpComplianceServer) | Standalone MCP JSON-RPC server |
| [onboarding-ragas-eval](https://github.com/matthew-opie/onboarding-ragas-eval) | Golden eval datasets + local smoke script |

## Architecture

```
Client / Blazor WASM
        │  Function URL (response streaming on /query/stream)
        ▼
ClientOnboardingLambda
        ├─ HybridRetrievalService  (BM25 + Qdrant → RRF → 2 parent chunks)
        ├─ McpClientRouter           (remote MCP or in-process fallback)
        ├─ OpenAiService             (gpt-4o-mini + embeddings)
        └─ DynamoDB single-table     (chunks, KYC, ingest/eval status)

Optional: MCP_SERVER_URL → McpComplianceServer (/mcp)
Async:     S3 → SQS → DocumentIngestLambda
```

| Setting | Value |
|---------|-------|
| Runtime | .NET 10 (`dotnet10`), arm64 |
| Memory | 1024 MB (query), see ingest Lambda for worker sizing |
| Chat model | `gpt-4o-mini` |
| Embeddings | `text-embedding-3-small` |

## Features (v2)

- **Multi-tenant RAG** — 10 tenants, isolated DynamoDB partitions + Qdrant collections
- **Hybrid retrieval** — BM25 over child chunks + dense search, fused with reciprocal rank fusion (RRF), top 2 parent chunks with page numbers
- **MCP compliance tools** — HTTP client to standalone MCP server, or in-process fallback when `MCP_SERVER_URL` is unset
- **SSE streaming** — `POST /tenants/{id}/query/stream` with real OpenAI token stream
- **RAGAS eval** — one-time admin eval, cached faithfulness in DynamoDB
- **Ingest status** — `GET /tenants/{id}/ingest-status` (async pipeline via DocumentIngestLambda)
- **Structured logging** — JSON stage logs + `x-request-id` on every response

## Project structure

| Path | Purpose |
|------|---------|
| `Function.cs` | Lambda entry (buffered + response streaming) |
| `Services/RequestRouter.cs` | HTTP routing |
| `Services/RagOrchestrator.cs` | Retrieve → MCP tools → OpenAI |
| `Services/RetrievalServices.cs` | Chunking, BM25, RRF fusion |
| `Services/ComplianceToolService.cs` | Tool logic + `McpClientRouter` |
| `Services/OpenAiService.cs` | Chat, streaming chat, embeddings |
| `Services/RagasEvalService.cs` | Golden-set faithfulness eval |
| `ClientOnboardingLambda.Tests/` | Unit tests (BM25, RRF, tenant isolation) |

## Configuration

| Variable | Required | Description |
|----------|----------|-------------|
| `OPENAI_API_KEY` | Yes | Chat + embeddings |
| `QDRANT_URL` / `QDRANT_API_KEY` | Yes | Vector search |
| `DYNAMODB_TABLE_NAME` | No | Default `OnboardingPlatform` |
| `SEED_BUCKET_NAME` | Ingest | S3 seed PDFs |
| `ADMIN_API_KEY` | Admin routes | `x-api-key` header |
| `MCP_SERVER_URL` | No | Remote MCP server base URL |
| `MCP_SERVER_API_KEY` | No | Sent as `x-api-key` to MCP server |
| `CORS_ALLOWED_ORIGINS` | No | Comma-separated browser origins for **SSE streaming** responses (Function URL CORS does not apply to `RESPONSE_STREAM`). Buffered routes (`/health`, `/tenants`, etc.) rely on Function URL CORS only — leave unset to avoid duplicate `Access-Control-Allow-Origin` headers. Example: `https://www.mattopie.com,http://localhost:5156` |

## API (summary)

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/health` | Config probe |
| `GET` | `/tenants` | List 10 tenants |
| `POST` | `/tenants/{id}/query` | RAG query (JSON) |
| `POST` | `/tenants/{id}/query/stream` | RAG query (SSE) |
| `GET` | `/tenants/{id}/ingest-status` | Latest async ingest job |
| `GET` | `/tenants/{id}/eval` | Cached RAGAS scores |
| `POST` | `/admin/eval/{id}` | Run eval (admin key) |
| `POST` | `/admin/ingest/{id}` | Sync ingest (admin key) |

See inline XML comments in `RequestRouter.cs` for response shapes.

## Tests

```powershell
# Unit tests (local)
dotnet test ClientOnboardingLambda.Tests

# Golden regression (live Function URL)
# From blazor-portfolio-2026:
.\scripts\run-golden-tests.ps1
```

## Deploy

```powershell
dotnet tool install -g Amazon.Lambda.Tools
dotnet lambda deploy-function ClientOnboardingLambda
```

Enable **Response streaming** on the Function URL for `/query/stream`.

## Local build

```powershell
dotnet build
dotnet test ClientOnboardingLambda.Tests
```

Requires .NET 10 SDK and AWS/OpenAI/Qdrant credentials for integration testing.
