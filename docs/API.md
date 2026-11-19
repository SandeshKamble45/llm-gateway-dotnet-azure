# HTTP API contract

The .NET API keeps the existing gateway route prefix and JSON field names so the Angular dashboard can continue to use the service without a DTO rewrite.

## Base path

The backend route prefix is /v1/gateway.

The Angular development proxy uses /api/v1/gateway and removes the /api prefix before forwarding. A deployed frontend must provide an equivalent reverse proxy or set its API base URL to the backend origin.

## POST /v1/gateway/chat

### Headers

- Content-Type: application/json
- X-Demo-Access-Code: optional demo model gate
- Idempotency-Key: optional client-generated key, 1–128 visible characters, scoped to the normalized tenant ID

### Request

    {
      "tenantId": "tenant-1",
      "prompt": "Explain Redis Lua atomicity",
      "model": "primary-model"
    }

Model is optional. If omitted, the API selects the configured primary provider model.

Validation:
- tenantId is required, trimmed, 1–128 characters, and allows letters, digits, period, underscore and hyphen.
- prompt is required and nonblank.
- prompt cannot exceed 10,000 characters.
- the estimated prompt token count cannot exceed the configured token-bucket capacity.
- selecting a non-primary model requires a configured demo access code.

### Successful response

    {
      "response": {
        "modelUsed": "primary-model",
        "completion": "Mock response returned by LLM Gateway.",
        "usage": {
          "inputTokens": 7,
          "outputTokens": 9,
          "totalTokens": 16
        }
      },
      "estimatedCostMicrodollars": 6450,
      "actualCostMicrodollars": 645,
      "settlement": {
        "reservedMicrodollars": 6450,
        "actualMicrodollars": 645,
        "adjustmentMicrodollars": -5805,
        "spentMicrodollars": 645,
        "remainingMicrodollars": 4999355,
        "withinBudget": true
      },
      "latencyMs": 12
    }

Numeric cost values in this example are illustrative; actual amounts are calculated from the model pricing table and provider-reported usage.

### Error contract

| HTTP status | Meaning | Body |
|---|---|---|
| 400 | Invalid body, tenant ID, prompt, model or oversized input | String message |
| 402 | Daily tenant budget could not be reserved | BudgetReservationResult |
| 403 | Missing/invalid demo access code or tenant claim mismatch | String message |
| 409 | Idempotency key is in progress, was used with a different payload, or an underlying budget reservation already exists | String message or BudgetReservationResult |
| 429 | Sliding-window or token-bucket rejection | RateLimitResult |
| 502 | Provider unavailable | GatewayErrorResponse |

429 response properties: allowed, remainingTokens, algorithm and retryAfterSeconds. The existing DTO field remainingTokens is retained even when the sliding-window algorithm reports remaining requests.

Provider error response properties: message, releasedReservationMicrodollars and settlement.

The idempotency record is separate from the budget reservation ledger. It owns the HTTP request lifecycle and replays completed responses; the budget reservation continues to protect the monetary ledger. Both use tenant-scoped Redis keys and bounded retention.

### Idempotency-Key semantics

When supplied, the API validates the request before acquiring the key. The request fingerprint covers the normalized tenant ID, prompt, normalized model, and demo access code. The raw idempotency key is SHA-256 hashed before it is used in the Redis key.

- The first request atomically creates an `in_progress` record before consuming rate-limit or token capacity.
- A concurrent request with the same key and same fingerprint receives HTTP 409 while the original is in progress.
- Reusing the key with a different fingerprint receives HTTP 409.
- Once a response is completed, its HTTP status, content type, and serialized body are stored. A later request receives the same response with `Idempotency-Replayed: true`.
- Admission outcomes that occur after the key is acquired (such as 402 or 429) are also stored and replayed. Validation failures (400/403) occur before acquisition and do not lock the key.
- The default record TTL is 93,600 seconds (26 hours), configured by `Gateway:IdempotencyKeyTtlSeconds`.
- If a request is cancelled or the process fails after acquisition but before the response is persisted, the record remains `in_progress` until its TTL expires. The API fails closed rather than risking a second provider call that could incur duplicate cost.

Clients should reuse the same key only when retrying the same logical request. Use a new key for a new operation. Requests without an Idempotency-Key retain the non-idempotent behaviour.

## GET /v1/gateway/status?tenantId=tenant-1

Returns:
- tenantId
- tokenBucket: availableTokens, capacity, refillRatePerSecond
- slidingWindow: currentRequests, maxRequests, windowSeconds, remainingRequests
- dailyBudget: allowed, spentMicrodollars, remainingMicrodollars
- circuitBreaker: name, state, failureRate, bufferedCalls, failedCalls

Failure rate and buffer counts are a 30-second application observation window. The Polly circuit breaker state is authoritative; the counters are operational telemetry.

## Diagnostic endpoints

- POST /v1/gateway/check/token-bucket?tenantId=tenant-1&cost=1
- POST /v1/gateway/check/sliding-window?tenantId=tenant-1

These endpoints mutate limiter state and are intended for demonstration/diagnostics, not general production client use.

## Health

- GET /actuator/health
- GET /healthz

Both return JSON with a status property. Redis health is included. The endpoints allow anonymous access so Container Apps health probes can call them.

## Authentication

Authentication is disabled by default for local/demo compatibility. When Authentication:Enabled is true:
- ASP.NET Core JWT bearer authentication validates issuer/authority and audience.
- All controller routes require an authenticated principal.
- The configured tenant claim (default tenant_id) must equal the requested tenantId.
- Health probes remain anonymous.

The Angular application has not yet implemented OIDC/MSAL token acquisition; do not enable JWT enforcement until the frontend identity integration is completed.
