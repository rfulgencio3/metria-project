Metria.Api - Backend Guide

Backend em ASP.NET Core Minimal API (.NET 9) com PostgreSQL, JWT, OAuth Google e Stripe.

## 1) Endpoints principais

- Health check: `GET /health-check`
- Auth:
  - `POST /api/auth/signup`
  - `POST /api/auth/login`
  - `GET /api/auth/google/start`
  - `GET|POST /api/auth/google/callback`
- Billing:
  - `POST /api/billing/checkout`
  - `POST /api/billing/portal`
  - `GET /api/billing/subscription`
  - `GET /api/billing/subscriptions/history`
  - `POST /api/billing/webhook`
  - `POST /api/billing/sync`

## 2) Variaveis de ambiente

### Obrigatorias (producao)

- `DATABASE_URL` ou `POSTGRES_CONNECTION`
  - A API aceita formato URI (`postgresql://...`) e normaliza automaticamente.
- `Jwt__Issuer` (ex.: `Metria`)
- `Jwt__Audience` (ex.: `Metria.Web`)
- `Jwt__Key` (chave forte, >= 32 caracteres)
- `Jwt__ExpiresInSeconds` (ex.: `3600`)
- `FRONTEND_ORIGIN` (ex.: `https://seu-front.com`)
- `BACKEND_BASE_URL` (ex.: `https://seu-backend.up.railway.app`)

### Stripe

- `STRIPE_SECRET_KEY`
- `STRIPE_WEBHOOK_SECRET`
- `STRIPE_MONTHLY_PRICE_ID` (opcional, recomendado)
- `STRIPE_ANNUAL_PRICE_ID` (opcional, recomendado)

### Google OAuth

- `GOOGLE_CLIENT_ID`
- `GOOGLE_CLIENT_SECRET`

### Swagger em producao

- `ENABLE_SWAGGER=true` para habilitar Swagger UI na raiz `/`.
- Sem essa variavel, a raiz `/` pode retornar `404` em producao (comportamento esperado).

## 3) Executar localmente

1. Defina as variaveis acima (ou use `appsettings*.json` para chaves nao sensiveis).
2. Rode:

```bash
dotnet build src/Metria.Api/Metria.Api.csproj
dotnet run --project src/Metria.Api/Metria.Api.csproj
```

3. Teste:
- `http://localhost:5104/health-check` (ou porta do seu profile local)

## 4) Deploy no Railway (Dockerfile)

Configuracao recomendada:

- `Root Directory`: `/`
- `Builder`: `Dockerfile`
- `Dockerfile Path`: `/Dockerfile`
- `Healthcheck path`: `/health-check`
- Sem `Start Command` custom (deixe o Dockerfile controlar o start)

Depois do deploy, valide:

- `GET https://SEU-BACKEND/health-check` retorna `200`

## 5) Stripe webhook (producao)

No Stripe:

- Endpoint: `https://SEU-BACKEND/api/billing/webhook`
- Eventos:
  - `checkout.session.completed`
  - `customer.subscription.created`
  - `customer.subscription.updated`
  - `customer.subscription.deleted`

Copie o Signing Secret (`whsec_...`) para `STRIPE_WEBHOOK_SECRET`.

Validacao:

- Logs com `Stripe webhook event received: ...`
- Front consulta `GET /api/billing/subscription` e recebe `active=true` quando aplicavel

## 6) Google OAuth (producao)

No Google Cloud Console (OAuth Client):

- Authorized redirect URI:
  - `https://SEU-BACKEND/api/auth/google/callback`
- Authorized JavaScript origin:
  - `https://SEU-FRONTEND`

No frontend, iniciar login em:

- `GET https://SEU-BACKEND/api/auth/google/start?redirectUri=https%3A%2F%2FSEU-FRONTEND%2Foauth%2Fcallback`

## 7) Troubleshooting rapido

- `dotnet: command not found` no deploy:
  - Servico nao esta usando imagem correta/runtime.
  - Preferir deploy por Dockerfile.

- `Application failed to respond` / `502`:
  - Verificar deploy ativo, health check e dominio publico no Railway.
  - Confirmar app ouvindo em `0.0.0.0:$PORT` (ja feito no Dockerfile atual).

- `404` em `/?code=...` no OAuth:
  - Redirect URI no Google esta apontando para `/` em vez de `/api/auth/google/callback`.

## 8) Seguranca

- Nunca commitar credenciais reais (`sk_live`, `whsec`, senhas de banco, client secret).
- Rotacionar segredos se forem expostos em logs, chat ou screenshots.
