Metria API - MemoryBank

Purpose
- Consolidate backend architecture, operational setup, and troubleshooting notes.

Project Snapshot
- ASP.NET Core Minimal APIs on .NET 9.
- EF Core + PostgreSQL.
- JWT auth for API endpoints.
- Google OAuth login flow.
- Stripe billing with webhook processing.

Runtime and Hosting
- Current production deploy uses Railway + Dockerfile.
- Service listens on `0.0.0.0:$PORT` in container.
- Health endpoint: `GET /health-check`.

Environment Variables (backend)
- Database:
  - `DATABASE_URL` or `POSTGRES_CONNECTION`
- JWT:
  - `Jwt__Issuer`
  - `Jwt__Audience`
  - `Jwt__Key`
  - `Jwt__ExpiresInSeconds`
- App URLs:
  - `FRONTEND_ORIGIN`
  - `BACKEND_BASE_URL`
- Google OAuth:
  - `GOOGLE_CLIENT_ID`
  - `GOOGLE_CLIENT_SECRET`
- Stripe:
  - `STRIPE_SECRET_KEY`
  - `STRIPE_WEBHOOK_SECRET`
  - `STRIPE_MONTHLY_PRICE_ID` (optional)
  - `STRIPE_ANNUAL_PRICE_ID` (optional)
- Swagger in production:
  - `ENABLE_SWAGGER=true`

OAuth Notes
- Backend callback endpoint: `/api/auth/google/callback`.
- If Google returns to `/?code=...`, redirect URI is misconfigured in Google Cloud.

Stripe Notes
- Webhook endpoint: `/api/billing/webhook`.
- Required events:
  - `checkout.session.completed`
  - `customer.subscription.created`
  - `customer.subscription.updated`
  - `customer.subscription.deleted`

Operational Notes
- Startup runs `db.Database.Migrate()`.
- DataProtection warnings in container are expected unless key ring is externalized.

Troubleshooting
- `dotnet: command not found`: wrong runtime/build strategy.
- `502` with healthy logs: verify Railway domain routing and current active deployment.
- `404` at root in production can be expected when Swagger is disabled.
