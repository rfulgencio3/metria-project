# Environment Variables

## Core Backend

Required:

- `DATABASE_URL` or `POSTGRES_CONNECTION`
- `Jwt__Issuer`
- `Jwt__Audience`
- `Jwt__Key`
- `Jwt__ExpiresInSeconds`
- `FRONTEND_ORIGIN`
- `BACKEND_BASE_URL`

Optional frontend origin expansion:

- `FRONTEND_ORIGINS`

Notes:

- `DATABASE_URL` can be in URI format (`postgresql://...`) and is normalized by the backend.
- `FRONTEND_ORIGIN` is used by CORS and OAuth fallback logic.
- `FRONTEND_ORIGINS` can be used when the backend must accept more than one frontend origin, such as the published web domain plus `http://localhost:3000` for local validation.
- `BACKEND_BASE_URL` is used to build Google callback URL.

## Google OAuth

Required:

- `GOOGLE_CLIENT_ID`
- `GOOGLE_CLIENT_SECRET`

Optional:

- `FRONTEND_CALLBACK` (or config `FrontendCallback`)

## Stripe

Required:

- `STRIPE_SECRET_KEY`
- `STRIPE_WEBHOOK_SECRET`

Optional:

- `STRIPE_MONTHLY_PRICE_ID`
- `STRIPE_ANNUAL_PRICE_ID`

## Developer Experience

Optional:

- `ENABLE_SWAGGER=true` (enables Swagger UI in production at `/`)

## Example (Railway)

```env
DATABASE_URL=${{Postgres.DATABASE_URL}}
FRONTEND_ORIGIN=https://metria-web-production.up.railway.app
FRONTEND_ORIGINS=https://metria-web-production.up.railway.app,http://localhost:3000
BACKEND_BASE_URL=https://metria-project-production.up.railway.app
GOOGLE_CLIENT_ID=...
GOOGLE_CLIENT_SECRET=...
STRIPE_SECRET_KEY=...
STRIPE_WEBHOOK_SECRET=...
Jwt__Issuer=Metria
Jwt__Audience=Metria.Web
Jwt__Key=replace-with-strong-secret
Jwt__ExpiresInSeconds=3600
ENABLE_SWAGGER=true
```
