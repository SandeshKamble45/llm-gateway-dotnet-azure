# LLM Gateway — .NET 10 & Azure

A multi-tenant gateway for LLM workloads that combines request-rate limiting, token-capacity admission, tenant budget reservations, provider resilience, and usage-based settlement.

The repository contains an ASP.NET Core 10 backend, an Angular dashboard, Redis Lua scripts, automated tests, Docker tooling, and Azure infrastructure-as-code.

> **Deployment status:** The application and Azure deployment templates are maintained here, but an Azure deployment has not yet been verified. No .NET live-demo URL is published at this time. The repository's CI results are the source of truth for the current build and test status.

## Problem

LLM requests have different resource profiles. A short prompt and a large prompt should not necessarily consume the same capacity, and request-count limits alone do not prevent a tenant from exhausting a shared model budget.

The gateway independently controls:

- **Request frequency** with a Redis sorted-set sliding window.
- **Estimated input-token admission** with a Redis token bucket.
- **Tenant spend** with atomic budget reservations in integer microdollars.
- **Provider execution** with bounded resilience policies and a fallback provider.
- **Actual usage** with post-response cost calculation and reservation settlement.

## Request lifecycle

```text
Client / Angular Dashboard
          |
          v
  ASP.NET Core 10 API
          |
          v
   Validate tenant + prompt
          |
          v
 Redis sliding-window check
          |
          v
 Redis token-bucket check
          |
          v
 Estimate cost + reserve budget
          |
          v
 Polly provider pipeline
    |             |
    v             v
  Primary       Fallback
    |
    v
 Read actual provider usage
          |
          v
 Calculate actual model cost
          |
          v
 Atomic Redis settlement
          |
          v
 Response + usage + cost + latency
```

All shared admission and budget mutations are executed inside Redis Lua scripts. This keeps each read/check/update transition atomic across gateway instances.

## Architecture

| Layer | Project | Responsibility |
|---|---|---|
| Domain | `LlmGateway.Domain` | Tenant identity rules and gateway domain models |
| Application | `LlmGateway.Application` | Request orchestration, policies, and abstractions |
| Infrastructure | `LlmGateway.Infrastructure` | Redis state store, Lua scripts, tokenization, providers, Polly |
| API | `LlmGateway.Api` | ASP.NET Core endpoints, dependency injection, health, optional JWT |
| Unit tests | `LlmGateway.UnitTests` | Pricing, tokenizer, and tenant validation |
| Integration tests | `LlmGateway.IntegrationTests` | Redis scripts, reservation correctness, and concurrency |
| Frontend | `frontend/` | Angular dashboard and API client |
| Infrastructure | `infrastructure/azure/` | Azure Bicep templates and deployment scripts |

The backend is structured as a modular monolith. The request lifecycle has an ordered admission → reservation → provider → settlement flow, so the current design keeps those operations within one deployable API rather than introducing network boundaries prematurely.

## Engineering features

### Distributed admission control

- Sliding-window request limiting using a Redis sorted set.
- Token-bucket admission using estimated prompt tokens.
- Redis server time is used inside scripts for consistent time calculations.
- Atomic reservation and settlement scripts protect a tenant's daily budget under concurrent requests.
- Daily spend uses integer microdollars and checked arithmetic.

### Provider orchestration and resilience

- ASP.NET Core 10 and C#.
- OpenAI Responses API adapter.
- Deterministic mock provider for local development.
- Fallback provider.
- Polly timeout and circuit-breaker policies.
- A narrowly scoped retry for an explicit provider HTTP 429 response; ambiguous timeouts and generic 5xx responses are not blindly retried.

### Token and cost accounting

- CL100K_BASE token counting through SharpToken.
- Model-specific pricing configuration.
- Estimated cost reservation before provider execution.
- Actual usage-based settlement after the provider response.
- Budget reservation settlement is replay-safe at the Redis reservation level.

### Observability and cloud readiness

- OpenTelemetry instrumentation.
- Azure Monitor / Application Insights exporter configuration.
- Docker image and Docker Compose setup.
- Azure Container Apps and Azure Container Registry Bicep templates.
- User-assigned managed identity for registry pulls and Redis access.
- Azure Managed Redis configuration using Microsoft Entra authentication, TLS, and RESP3.
- GitHub Actions CI and an Azure DevOps pipeline template.

## Technology stack

**Backend:** C#, .NET 10, ASP.NET Core, Polly, StackExchange.Redis, SharpToken

**Data & coordination:** Redis, Redis Lua

**Frontend:** Angular, TypeScript, RxJS, SCSS

**Cloud & delivery:** Microsoft Azure, Azure Container Apps, Azure Container Registry, Azure Managed Redis, Managed Identity, Bicep, GitHub Actions, Azure Pipelines

**Testing:** xUnit, Testcontainers.Redis, Redis integration tests

## Repository layout

```text
llm-gateway-dotnet-azure/
├── src/
│   ├── LlmGateway.Domain/
│   ├── LlmGateway.Application/
│   ├── LlmGateway.Infrastructure/
│   └── LlmGateway.Api/
├── tests/
│   ├── LlmGateway.UnitTests/
│   └── LlmGateway.IntegrationTests/
├── frontend/
├── infrastructure/
│   └── azure/
├── docs/
├── .github/
│   └── workflows/
├── Dockerfile
├── docker-compose.yml
├── LlmGateway.sln
├── global.json
├── azure-pipelines.yml
└── README.md
```

## Run locally

### Prerequisites

- .NET 10 SDK
- Node.js 22 and npm
- Docker Desktop or another Docker-compatible runtime

### Start the API and Redis

From the repository root:

```bash
docker compose up --build
```

The API is available at:

- Health: `http://localhost:8080/actuator/health`
- OpenAPI in Development: `http://localhost:8080/openapi/v1.json`
- Chat: `POST http://localhost:8080/v1/gateway/chat`
- Tenant status: `GET http://localhost:8080/v1/gateway/status?tenantId=tenant-1`

### Start the Angular dashboard

In another terminal:

```bash
cd frontend
npm ci
npm start
```

Open `http://localhost:4200`. The Angular development proxy forwards `/api/v1/gateway` to the local API at port 8080.

### Example request

```http
POST http://localhost:8080/v1/gateway/chat
Content-Type: application/json
```

```json
{
  "tenantId": "tenant-1",
  "prompt": "Explain how Redis Lua scripts provide atomicity.",
  "model": "primary-model"
}
```

The default provider is the deterministic mock provider. To use the OpenAI provider, configure the API key through an environment variable or secret store; do not commit credentials.

## Build and test

From the repository root:

```bash
dotnet restore LlmGateway.sln
dotnet build LlmGateway.sln --configuration Release
dotnet test LlmGateway.sln --configuration Release --max-parallel-test-modules 2 --report-trx --results-directory TestResults
```

Redis integration tests use Testcontainers and require Docker.

Build the Angular frontend:

```bash
cd frontend
npm ci
npm run build
```

The GitHub Actions workflow runs the .NET restore/build/tests, Angular production build, Docker image build, and Bicep compilation.

## Azure infrastructure

The Bicep templates are split into two stages:

1. `infrastructure/azure/foundation.bicep` provisions the registry, user-assigned identity, role assignment, Log Analytics workspace, and Application Insights.
2. `infrastructure/azure/main.bicep` provisions the Container Apps environment and API container using an image already pushed to ACR.

Azure Managed Redis is provisioned separately because its SKU and regional cost should be chosen deliberately. The deployment runbook documents Entra data-plane access, image publishing, secrets, health checks, and cleanup.

Start with:

- [Azure setup](docs/AZURE_SETUP.md)
- [Deployment runbook](docs/DEPLOYMENT.md)
- [Security model](docs/SECURITY.md)
- [Azure certification / lab mapping](docs/AZURE_CERTIFICATION_MAPPING.md)

Do not treat Bicep compilation as proof of an Azure deployment. The deployment still requires an Azure subscription, configured identities, role assignments, service connection, and live verification.

## Design and interview notes

- [High-level design](docs/HLD.md)
- [Low-level design](docs/LLD.md)
- [HTTP API contract](docs/API.md)
- [Testing strategy and known gaps](docs/TESTING.md)
- [Interview defence guide](docs/INTERVIEW_GUIDE.md)
- [Local development](docs/LOCAL_DEVELOPMENT.md)

## Known limitations

The following are explicit follow-up items; they are not claimed as complete:

- Full HTTP idempotency with response replay for repeated `Idempotency-Key` values.
- Reconciliation of reservations left open by cancellation, process termination, or uncertain provider outcomes.
- API contract tests using `WebApplicationFactory`.
- JWT authentication and cross-tenant authorization tests.
- Angular OIDC/MSAL token acquisition and token forwarding. JWT enforcement is disabled by default until this is implemented.
- Provider fallback and circuit-breaker recovery tests.
- Redis Cluster deployment validation.
- A reproducible .NET load test with documented concurrency, workload, p95, and p99 latency.
- A verified Azure deployment and .NET live demo.

The current test suite validates core pricing, tenant validation, tokenizer behavior, and Redis atomicity/concurrency scenarios. See [TESTING.md](docs/TESTING.md) for the coverage and production-parity gaps.

## Scope

This repository is the standalone .NET 10 and Azure implementation. It contains no Java backend or Maven build. The Angular dashboard is included as the full-stack client.
