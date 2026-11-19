# Angular dashboard

This Angular application is the full-stack client for the .NET LLM Gateway API.

## Prerequisites

- Node.js 22
- npm 10+
- The .NET gateway API and Redis, started through Docker Compose from the repository root or launched locally.

## Run locally

From this directory:

```bash
npm ci
npm start
```

Open `http://localhost:4200`. The development proxy in `proxy.conf.json` forwards `/api/v1/gateway` to `http://localhost:8080/v1/gateway`.

## Build and test

```bash
npm run build
npm test
```

The API client is in `src/app/core/services/gateway-api.ts`; request/response models are in `src/app/core/models/gateway.models.ts`.

## Authentication note

The dashboard currently uses the demo-compatible API flow and does not acquire OIDC/MSAL access tokens. Keep backend JWT enforcement disabled until frontend identity integration and tenant-claim mapping are implemented.
