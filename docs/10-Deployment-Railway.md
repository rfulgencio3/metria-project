# Deployment (Railway)

## Recommended Build Strategy

- `Root Directory`: `/`
- `Builder`: `Dockerfile`
- `Dockerfile Path`: `/Dockerfile`
- No custom start command
- Healthcheck path: `/health-check`

## Required Variables

- `DATABASE_URL` (recommended from `${{Postgres.DATABASE_URL}}`)
- `FRONTEND_ORIGIN`
- `BACKEND_BASE_URL`
- `GOOGLE_CLIENT_ID`
- `GOOGLE_CLIENT_SECRET`
- `STRIPE_SECRET_KEY`
- `STRIPE_WEBHOOK_SECRET`
- `Jwt__Issuer`
- `Jwt__Audience`
- `Jwt__Key`
- `Jwt__ExpiresInSeconds`

Optional:

- `ENABLE_SWAGGER=true`
- `STRIPE_MONTHLY_PRICE_ID`
- `STRIPE_ANNUAL_PRICE_ID`
- `FRONTEND_ORIGINS`

## Local Validation Against Railway

If you need to call the Railway backend from `http://localhost:3000`, configure:

- `FRONTEND_ORIGINS=https://metria-web-production.up.railway.app,http://localhost:3000`

Keep `FRONTEND_ORIGIN` pointing to the primary published frontend domain. Use `FRONTEND_ORIGINS` only to extend the allowed CORS list.

## Post-Deploy Smoke

1. `GET /health-check`
2. `GET /api/auth/google/start?...` returns redirect to Google
3. Login and access protected endpoint
4. Trigger checkout and confirm webhook delivery
5. Confirm `GET /api/billing/subscription` state update

## Domain Troubleshooting

- If logs show app healthy but public requests return `502`, verify:
  - active deployment id
  - domain binding in Railway networking
  - service restart and/or new generated public domain
