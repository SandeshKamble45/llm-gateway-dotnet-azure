# Testing strategy

Prerequisites: .NET 10 SDK, Node.js 22/npm, and Docker.

From the repository root:

```bash
dotnet restore LlmGateway.sln
dotnet build LlmGateway.sln --configuration Release
dotnet test LlmGateway.sln --configuration Release --max-parallel-test-modules 2 --report-trx --results-directory TestResults
```

Build the Angular app:

```bash
cd frontend
npm ci
npm run build
```

Redis integration tests use Testcontainers.Redis and execute the production Lua scripts against a real Redis container.

## Current coverage

Unit tests cover integer microdollar pricing, rounding/overflow, tenant ID validation and key encoding, and CL100K token counting.

Redis integration tests cover sliding-window admission, concurrent token-bucket admissions, concurrent tenant budget reservations, duplicate settlement, in-progress/settled reservation rejection, and tenant isolation. Idempotency tests cover one-owner concurrent acquisition, in-progress responses, changed-payload conflict, exact completed-response replay, owner-only completion, safe release, and tenant isolation. Unit tests cover stable and payload-sensitive request fingerprints.

## CI

`.github/workflows/dotnet-ci.yml` runs .NET restore/build/test, Angular build, Docker image build, Bicep compilation and TRX upload. `azure-pipelines.yml` contains validation and a gated deployment template; Azure DevOps credentials and service connection are not configured here.

## Outstanding tests before production parity

- API contract tests with WebApplicationFactory.
- JWT valid/invalid audience and cross-tenant denial tests.
- Provider fallback and circuit-breaker recovery tests.
- Retry behaviour for explicit 429 and no-retry tests for timeout/5xx.
- Cancellation and reconciliation of budget reservations and idempotency records left in progress.
- API-level WebApplicationFactory tests for idempotency response replay, status codes, and the Idempotency-Replayed header.
- Multi-model isolation tests.
- Sliding-window expiry and Retry-After accuracy.
- Redis Cluster hash-slot validation.
- Reproducible .NET load test with p95/p99.

Do not present benchmark numbers from another implementation as performance evidence for this .NET implementation.
